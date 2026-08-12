using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Flushes a deferred <see cref="ClientTeamFlowState.ConfirmTeamChoice"/> after TeamChoice /
    /// rejoin success. Runs first in InitializationSystemGroup.
    /// <para>
    /// [TITAN-ORBIT] Player.log 2026-07-28: Arm + same-frame Confirm still Crash!!!'d — need at
    /// least one frame of defer so suppress stays on for the TeamChoiceResult Instantiates window.
    /// Player.log 2026-07-30: flushing Confirm on the very next frame still Crash!!!'d —
    /// suppress-only ship gathers opened while GhostSpawn Instantiates the hull. Keep Confirm
    /// deferred for the full <see cref="ClientJoinSettleCache.ArmPostTeamChoiceHold"/> countdown.
    /// </para>
    /// <para>
    /// Debug 1af271: hold expired with Instantiates still at map meta-N and no seed — Confirm
    /// unlocked spawn-wait UI with no hull forever. After the hold, wait up to
    /// <see cref="MaxHullWaitFrames"/> for a <b>live owned-ship seed</b> (predicted Instantiates
    /// or GhostReceive hook) before flushing.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Do <b>not</b> treat <c>InstantiatesSession</c> climbing as “hull arrived”.
    /// That counter only tracks GhostSpawn map Instantiates — clicking Join Team while planets
    /// were still streaming made Confirm flush early (looked fine), but clicking after the map
    /// finished left InstantiatesSession flat, Confirm timed out empty, and the 8s spawn-wait
    /// watchdog bounced the player back to Join Team.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct ClientDeferredTeamChoiceConfirmSystem : ISystem
    {
        /// <summary>
        /// Extra frames after Instantiates hold to wait for hull Instantiates / seed before Confirm.
        /// ~4s at 60 Hz — covers Local Host GhostSend + client predicted Instantiates.
        /// </summary>
        public const int MaxHullWaitFrames = 240;

        /// <summary>Frames spent waiting for hull after the post–TeamChoice hold expired.</summary>
        static int s_HullWaitFrames;

        /// <summary>Clears hull-wait statics on domain reload.</summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_HullWaitFrames = 0;
        }

        /// <summary>
        /// Applies a queued deferred Confirm once the post–TeamChoice Instantiates hold has expired
        /// and (when possible) a live owned-ship seed is observed.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Nothing queued ---
            if (!ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
            {
                s_HullWaitFrames = 0;
                return;
            }

            // --- Drop stale seed handles (Domain Reload off) before hull checks ---
            var em = state.EntityManager;
            LocalShipEntitySeed.PruneStale(em);

            // --- Drain predicted Instantiates in Init (even while hold is active) ---
            // [TITAN-ORBIT] Editor.log 2026-08-12: Local Host Result arms Pending on ServerWorld
            // after ClientSimulation already ran → Request stayed Pending 240 frames with no
            // Instantiates. Drain every deferred-Confirm frame so seed exists when hold expires.
            if (ClientPredictedShipSpawnRequest.Pending)
                ClientPredictedShipSpawnRequest.TryDrainPending(em);

            // --- Hold still covering ship Instantiates Crash!!! window ---
            if (ClientJoinSettleCache.IsPostTeamChoiceHoldActive)
                return;

            // --- After hold: wait for live owned-ship seed (or timeout) ---
            bool hasLiveSeed = LocalShipEntitySeed.HasLiveOwnedShipSeed(em);
            bool hullArrived = hasLiveSeed;

            if (!hullArrived && s_HullWaitFrames < MaxHullWaitFrames)
            {
                s_HullWaitFrames++;

                // --- Re-arm + drain if the first Request was skipped / lost ---
                // [TITAN-ORBIT] Request() reuses the remembered server ring pose when this
                // call passes hasSpawnPos=false, so a retry cannot pick a new random angle.
                if (!ClientPredictedShipSpawnRequest.Pending &&
                    (s_HullWaitFrames == 1 || s_HullWaitFrames % 30 == 0))
                {
                    int networkId = 0;
                    using (var ids = em.CreateEntityQuery(
                                   typeof(NetworkStreamConnection),
                                   typeof(NetworkStreamInGame),
                                   typeof(NetworkId))
                               .ToComponentDataArray<NetworkId>(Allocator.Temp))
                    {
                        if (ids.Length > 0)
                            networkId = ids[0].Value;
                    }

                    var team = ClientTeamFlowState.LastRequestedTeam;
                    if (networkId > 0 && team != TeamId.None)
                    {
                        ClientPredictedShipSpawnRequest.Request(
                            networkId, team, float3.zero, hasSpawnPos: false);
                        ClientPredictedShipSpawnRequest.TryDrainPending(em);
                        hasLiveSeed = LocalShipEntitySeed.HasLiveOwnedShipSeed(em);
                        if (hasLiveSeed)
                        {
                            hullArrived = true;
                            // Fall through to Confirm flush below when hold already expired.
                        }
                    }
                }

                if (!hullArrived)
                {
                    if (s_HullWaitFrames == 1 || s_HullWaitFrames % 60 == 0)
                    {
                        UnityEngine.Debug.Log(
                            "[TeamChoiceResult] Waiting for live ship seed before Confirm " +
                            $"(wait={s_HullWaitFrames}/{MaxHullWaitFrames}, " +
                            $"hasSeed={hasLiveSeed}, " +
                            $"predPending={ClientPredictedShipSpawnRequest.Pending}, " +
                            $"instantiates={TitanOrbitJoinLoadCounters.InstantiatesSession}).");
                    }

                    return;
                }
            }

            // --- Unlock local ship control ---
            // GhostSpawnBacklog may still be true — ShouldSkipShipEntityQueries continues to gate gathers.
            ClientTeamFlowState.FlushDeferredConfirmTeamChoice();
            UnityEngine.Debug.Log(
                "[TeamChoiceResult] Deferred Confirm flushed — local ship control unlocked " +
                $"(skipShip={ClientJoinSettleCache.ShouldSkipShipEntityQueries} " +
                $"backlog={ClientJoinSettleCache.GhostSpawnBacklog} " +
                $"hasSeed={LocalShipEntitySeed.HasLiveOwnedShipSeed(em)} " +
                $"instantiates={TitanOrbitJoinLoadCounters.InstantiatesSession} " +
                $"hullWait={s_HullWaitFrames} hullArrived={hullArrived}).");

            s_HullWaitFrames = 0;
        }
    }
}

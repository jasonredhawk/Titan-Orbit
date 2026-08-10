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
    /// <see cref="MaxHullWaitFrames"/> for Instantiates-hook seed or InstantiatesSession climbing
    /// past the Result baseline before flushing.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct ClientDeferredTeamChoiceConfirmSystem : ISystem
    {
        /// <summary>
        /// Extra frames after Instantiates hold to wait for hull Instantiates / seed before Confirm.
        /// ~4s at 60 Hz — covers Local Host GhostSend + client Instantiates 1/frame.
        /// </summary>
        public const int MaxHullWaitFrames = 240;

        /// <summary>InstantiatesSession sampled when deferred Confirm was first seen.</summary>
        static int s_BaselineInstantiates = -1;

        /// <summary>Frames spent waiting for hull after the post–TeamChoice hold expired.</summary>
        static int s_HullWaitFrames;

        /// <summary>Clears hull-wait statics on domain reload.</summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_BaselineInstantiates = -1;
            s_HullWaitFrames = 0;
        }

        /// <summary>
        /// Applies a queued deferred Confirm once the post–TeamChoice Instantiates hold has expired
        /// and (when possible) a local hull seed or Instantiates bump is observed.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Nothing queued ---
            if (!ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending)
            {
                s_BaselineInstantiates = -1;
                s_HullWaitFrames = 0;
                return;
            }

            // --- Latch Instantiates baseline while deferred is pending ---
            // [TITAN-ORBIT] Map Instantiates may already equal meta N; ship Instantiates bumps the counter.
            if (s_BaselineInstantiates < 0)
                s_BaselineInstantiates = TitanOrbitJoinLoadCounters.InstantiatesSession;

            // --- Drop stale seed handles (Domain Reload off) before hull checks ---
            var em = state.EntityManager;
            LocalShipEntitySeed.PruneStale(em);

            // --- Hold still covering ship Instantiates Crash!!! window ---
            if (ClientJoinSettleCache.IsPostTeamChoiceHoldActive)
                return;

            // --- After hold: wait for hull Instantiates / live seed (or timeout) ---
            bool hasLiveSeed = LocalShipEntitySeed.HasLiveOwnedShipSeed(em);
            bool hullArrived =
                hasLiveSeed ||
                TitanOrbitJoinLoadCounters.InstantiatesSession > s_BaselineInstantiates;

            if (!hullArrived && s_HullWaitFrames < MaxHullWaitFrames)
            {
                s_HullWaitFrames++;

                // --- Re-arm predicted Instantiates if the first Request was skipped / lost ---
                // [TITAN-ORBIT] Editor.log: first Play Instantiates predicted hull; later Plays
                // (Domain Reload off) never logged Instantiates while Confirm waited 240 frames.
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
                        ClientPredictedShipSpawnRequest.Request(
                            networkId, team, float3.zero, hasSpawnPos: false);
                }

                if (s_HullWaitFrames == 1 || s_HullWaitFrames % 60 == 0)
                {
                    UnityEngine.Debug.Log(
                        "[TeamChoiceResult] Waiting for hull Instantiates/seed before Confirm " +
                        $"(wait={s_HullWaitFrames}/{MaxHullWaitFrames}, " +
                        $"instantiates={TitanOrbitJoinLoadCounters.InstantiatesSession}, " +
                        $"baseline={s_BaselineInstantiates}, hasSeed={hasLiveSeed}, " +
                        $"predPending={ClientPredictedShipSpawnRequest.Pending}).");
                }

                return;
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

            s_BaselineInstantiates = -1;
            s_HullWaitFrames = 0;
        }
    }
}

using TitanOrbit.Core;
using Unity.Entities;
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
    /// After the hold, wait up to <see cref="MaxHullWaitFrames"/> for the <b>real</b> owner ship
    /// from GhostReceive (<see cref="LocalShipEntitySeed.HasLiveOwnedShipSeed"/>). Do not Instantiates
    /// a client predicted hull — that workaround produced a visible ship that could not move.
    /// Overlay wait is the server spawn + snapshot RTT.
    /// </para>
    /// <para>
    /// Do <b>not</b> treat <c>InstantiatesSession</c> climbing as “hull arrived”. That counter
    /// only tracks GhostSpawn map Instantiates.
    /// </para>
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    public partial struct ClientDeferredTeamChoiceConfirmSystem : ISystem
    {
        /// <summary>
        /// Extra frames after Instantiates hold to wait for GhostReceive of the server ship.
        /// ~6s at 60 Hz — covers Relay RTT + GhostSend after sitting on Join Team.
        /// </summary>
        public const int MaxHullWaitFrames = 360;

        /// <summary>Frames spent waiting for the GhostReceive hull after the hold expired.</summary>
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
        /// and (when possible) GhostReceive has Instantiated the owned ship ghost.
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

            // --- Hold still covering ship Instantiates Crash!!! window ---
            if (ClientJoinSettleCache.IsPostTeamChoiceHoldActive)
                return;

            // --- After hold: wait for GhostReceive owner ship (or timeout) ---
            // [NETCODE] TeamManagementSystem Instantiates the ship on the server. GhostSend
            // streams it (relevancy includes ShipTag; GhostConnectionPosition is set at spawn).
            // LocalShipEntitySeed.NotifyShipInstantiated records the client replica — no gather.
            bool hullArrived = LocalShipEntitySeed.HasLiveOwnedShipSeed(em);

            if (!hullArrived && s_HullWaitFrames < MaxHullWaitFrames)
            {
                s_HullWaitFrames++;
                if (s_HullWaitFrames == 1 || s_HullWaitFrames % 60 == 0)
                {
                    UnityEngine.Debug.Log(
                        "[TeamChoiceResult] Waiting for GhostReceive owner ship before Confirm " +
                        $"(wait={s_HullWaitFrames}/{MaxHullWaitFrames}, " +
                        $"instantiates={TitanOrbitJoinLoadCounters.InstantiatesSession}).");
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

            s_HullWaitFrames = 0;
        }
    }
}

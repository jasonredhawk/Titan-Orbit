using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Retired Join Team predicted-hull queue. Kept so leftover callers compile and log instead
    /// of Instantiates a fake OwnerPredicted ship on ClientWorld.
    /// <para>
    /// [TITAN-ORBIT] 2026-08-13: Join Team must not Instantiates a client predicted hull.
    /// The old path Instantiated a local ship so “Spawning your ship…” would not hang when
    /// GhostReceive was slow. That hull often never got a valid <c>GhostInstance.spawnTick</c>
    /// (InitJob missed / wrong prefab), so <c>RequirePredictedGhost</c> skipped the prediction
    /// loop — visible ship, cannot move.
    /// </para>
    /// <para>
    /// Current flow: click team → <c>RequestTeamCommand</c> → server
    /// <see cref="TeamManagementSystem"/> Instantiates the ship ghost → GhostReceive delivers
    /// that ghost → <see cref="LocalShipEntitySeed.NotifyShipInstantiated"/> seeds it →
    /// <see cref="ClientDeferredTeamChoiceConfirmSystem"/> Confirm. Overlay wait is real RTT.
    /// </para>
    /// World: ClientSimulation (system is a no-op drain).
    /// </summary>
    public static class ClientPredictedShipSpawnRequest
    {
        /// <summary>Always false — Join Team no longer queues a client Instantiates.</summary>
        public static bool Pending => false;

        /// <summary>Unused. Kept so old Request signatures still compile.</summary>
        public static int NetworkId => 0;

        /// <summary>Unused. Kept so old Request signatures still compile.</summary>
        public static TeamId Team => TeamId.None;

        /// <summary>Unused. Kept so old Request signatures still compile.</summary>
        public static float3 SpawnPos => float3.zero;

        /// <summary>Unused. Kept so old Request signatures still compile.</summary>
        public static bool HasSpawnPos => false;

        /// <summary>True after we logged the retired-API notice once this Play Mode.</summary>
        static bool s_LoggedRetiredNotice;

        /// <summary>
        /// No-op. Join Team must not Instantiates a predicted hull on ClientWorld.
        /// Server <see cref="TeamManagementSystem"/> is the only ship spawn.
        /// </summary>
        /// <param name="networkId">Ignored.</param>
        /// <param name="team">Ignored.</param>
        /// <param name="spawnPos">Ignored.</param>
        /// <param name="hasSpawnPos">Ignored.</param>
        public static void Request(int networkId, TeamId team, float3 spawnPos, bool hasSpawnPos)
        {
            if (s_LoggedRetiredNotice)
                return;

            s_LoggedRetiredNotice = true;
            Debug.Log(
                "[ClientPredictedShipSpawn] Request ignored — Join Team no longer Instantiates a " +
                $"client predicted hull (networkId={networkId} team={team}). " +
                "Wait for GhostReceive of the server ship.");
        }

        /// <summary>No-op. There is no pending Instantiates queue.</summary>
        public static void Clear()
        {
        }

        /// <summary>
        /// Clears Play Mode statics. Call on Join Team click so a prior session cannot leak.
        /// </summary>
        public static void ResetForTeamPick()
        {
            s_LoggedRetiredNotice = false;
        }

        /// <summary>
        /// Always false. GhostReceive Instantiates the real owner ship; do not Instantiates here.
        /// </summary>
        /// <param name="em">Ignored.</param>
        /// <returns>Always false.</returns>
        public static bool TryDrainPending(EntityManager em)
        {
            return false;
        }

        /// <summary>Domain reload — drop the one-shot log latch.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticsSubsystem()
        {
            ResetForTeamPick();
        }

        /// <summary>[UNITY] Play Mode enter with Domain Reload disabled.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad()
        {
            ResetForTeamPick();
        }
    }

    /// <summary>
    /// ClientSimulation placeholder. Predicted Join Team Instantiates is retired —
    /// <see cref="ClientPredictedShipSpawnRequest.TryDrainPending"/> is a no-op.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamChoiceResultClientSystem))]
    public partial struct ClientPredictedShipSpawnSystem : ISystem
    {
        /// <summary>No RequireForUpdate — leftover Request() calls must still compile against this type.</summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>No-op. Server spawn + GhostReceive own the hull.</summary>
        public void OnUpdate(ref SystemState state)
        {
        }
    }
}

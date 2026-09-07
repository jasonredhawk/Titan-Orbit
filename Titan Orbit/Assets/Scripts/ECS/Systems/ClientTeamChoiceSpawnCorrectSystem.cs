using TitanOrbit.Core;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Re-applies TeamChoice owner / team / home-ring pose after GhostUpdate stomps the
    /// Instantiates-hook write. Dedicated Relay often Instantiates the hull at the prefab
    /// origin with <c>GhostOwner.NetworkId == 0</c> and <c>ShipState.Team == None</c> — that
    /// showed up as a grey ship nowhere near the home planet.
    /// <para>
    /// Runs for a short window after TeamChoice so later flight is not snapped back to spawn.
    /// World: ClientSimulation.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    public partial struct ClientTeamChoiceSpawnCorrectSystem : ISystem
    {
        /// <summary>~3s at 60 Hz — covers Relay snapshot lag without fighting real thrust.</summary>
        const int MaxCorrectFrames = 180;

        /// <summary>Frames since the last TeamChoice latch / pending Confirm.</summary>
        static int s_CorrectFrames;

        /// <summary>Clears Play Mode statics.</summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_CorrectFrames = 0;

        /// <summary>Call when TeamChoice succeeds so a retry gets a fresh snap window.</summary>
        public static void RestartWindow() => s_CorrectFrames = 0;

        /// <summary>Re-applies identity on the seeded TeamChoice hull while the window is open.</summary>
        public void OnUpdate(ref SystemState state)
        {
            bool teamChoiceActive =
                ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending ||
                ClientTeamFlowState.TeamChoiceConfirmed;
            if (!teamChoiceActive)
            {
                s_CorrectFrames = 0;
                return;
            }

            if (s_CorrectFrames >= MaxCorrectFrames)
                return;

            var em = state.EntityManager;
            LocalShipEntitySeed.PruneStale(em);
            if (!LocalShipEntitySeed.TryGetOwnedShipEntityUnchecked(em, out var ship) ||
                ship == Entity.Null)
                return;

            LocalShipEntitySeed.ApplyTeamChoiceIdentity(em, ship);
            s_CorrectFrames++;
        }
    }
}

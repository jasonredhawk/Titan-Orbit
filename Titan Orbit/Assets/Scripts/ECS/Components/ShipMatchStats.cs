using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Match-long cumulative scores on each ship ghost. Ghost — a networked entity
    /// replica replicated to all clients. Written only by the server; clients read these for
    /// minimap top-of-team badges (killer / miner / transporter) and future scoreboards.
    /// <para>
    /// [TITAN-ORBIT] Stats do not reset on death/respawn — they accumulate for the whole match.
    /// Baked on the ship ghost via <see cref="Authoring.StarshipGhostAuthoring"/> so GhostFields
    /// register for replication (runtime-only AddComponent would not ghost).
    /// </para>
    /// </summary>
    public struct ShipMatchStats : IComponentData
    {
        /// <summary>
        /// [TITAN-ORBIT] Ships this player destroyed (enemy deaths credited from last damager).
        /// </summary>
        [GhostField] public int Kills;

        /// <summary>
        /// [TITAN-ORBIT] Integer floor of gems successfully deposited to planets this match.
        /// Live cargo hold is <see cref="ShipState.CurrentGems"/> — this is the cumulative score.
        /// </summary>
        [GhostField] public int GemsDeposited;

        /// <summary>
        /// [TITAN-ORBIT] People successfully delivered via unload transports this match.
        /// Live hold is <see cref="ShipState.CurrentPeople"/> — this is the cumulative score.
        /// </summary>
        [GhostField] public int PeopleDelivered;
    }
}

using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server-only respawn timer added by <see cref="ShipDeathRecordingSystem"/> when
    /// <see cref="ShipState.IsDead"/> becomes true. Not ghost-serialized — clients infer death from
    /// replicated <see cref="ShipState.IsDead"/>. Removed by <see cref="ShipRespawnSystem"/> when the
    /// ship respawns at its home planet.
    /// </summary>
    public struct ShipDeathState : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [UNITY] ElapsedTime (seconds, server world clock) when the ship should respawn.
        /// Compared each server tick by <see cref="ShipRespawnSystem"/>.
        /// </summary>
        public float RespawnAtTime;
    }
}

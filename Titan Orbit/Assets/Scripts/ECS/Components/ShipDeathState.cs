using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server-only death bookkeeping added by <see cref="ShipDeathRecordingSystem"/> when
    /// <see cref="ShipState.IsDead"/> becomes true. Not ghost-serialized — clients infer death from
    /// replicated <see cref="ShipState.IsDead"/> and use a local 10s fallback for the plaque.
    /// Removed by <see cref="ShipRespawnSystem"/> after a valid <see cref="RequestRespawnPlanetCommand"/>.
    /// The ship does <b>not</b> auto-respawn when <see cref="RespawnAtTime"/> is reached; that time
    /// is only the earliest the player may click a friendly planet.
    /// </summary>
    public struct ShipDeathState : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [UNITY] ElapsedTime (seconds, server world clock) when the player may request respawn.
        /// Compared each server tick by <see cref="ShipRespawnSystem"/> when an RPC arrives.
        /// </summary>
        public float RespawnAtTime;
    }
}

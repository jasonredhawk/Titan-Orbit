using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only timer added by <see cref="ShipDeathRecordingSystem"/> when
    /// <see cref="ShipState.IsDead"/> becomes true. Not ghost-serialized — clients infer death
    /// from replicated ShipState.IsDead. Removed by <see cref="ShipRespawnSystem"/> on respawn.
    /// </summary>
    public struct ShipDeathState : IComponentData
    {
        /// <summary>ElapsedTime (seconds) when the ship should respawn at home planet.</summary>
        public float RespawnAtTime;
    }
}

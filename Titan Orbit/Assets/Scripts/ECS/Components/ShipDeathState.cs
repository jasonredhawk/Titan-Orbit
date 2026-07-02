using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>Server-only timer for delayed respawn after lethal damage.</summary>
    public struct ShipDeathState : IComponentData
    {
        public float RespawnAtTime;
    }
}

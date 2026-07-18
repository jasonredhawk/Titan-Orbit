using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client-only flag: this Instantiated map ghost is waiting for
    /// <c>EcsWorldVisualizer</c> to create its GameObject proxy.
    /// <para>
    /// Added by <see cref="MapBodyHybridVisualRequestSystem"/> after NetCode Instantiates
    /// (entity has real sim components, not <see cref="Unity.NetCode.PendingSpawnPlaceholder"/>).
    /// The visualizer drains this tag at a few Instantiates per frame — never scans every asteroid
    /// via <c>ToEntityArray</c> during join (that Burst path hard-crashed Windows).
    /// </para>
    /// </summary>
    public struct MapBodyHybridVisualPending : IComponentData { }

    /// <summary>
    /// [HYBRID] Client-only flag: a GameObject proxy was created for this map ghost.
    /// Prevents re-queueing <see cref="MapBodyHybridVisualPending"/>.
    /// </summary>
    public struct MapBodyHybridVisualLinked : IComponentData { }
}

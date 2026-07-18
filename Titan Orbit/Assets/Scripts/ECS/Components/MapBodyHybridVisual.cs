using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client-only flag: this Instantiated map ghost is waiting for
    /// <c>EcsWorldVisualizer</c> to create its GameObject proxy.
    /// <para>
    /// Baked onto planet/asteroid/gem ghost prefabs (<see cref="GhostPrefabType.Client"/>) so
    /// GhostSpawn Instantiates already carry Pending — the visualizer can drain a small Pending
    /// queue during join without <see cref="MapBodyHybridVisualRequestSystem"/> scanning every
    /// Instantiated asteroid via <c>ToEntityArray</c> (that path hard-crashes Windows even when
    /// placeholders briefly hit zero mid-settle).
    /// </para>
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct MapBodyHybridVisualPending : IComponentData { }

    /// <summary>
    /// [HYBRID] Client-only flag: a GameObject proxy was created for this map ghost.
    /// Prevents re-queueing <see cref="MapBodyHybridVisualPending"/>.
    /// Added at runtime by the visualizer — not baked, not replicated.
    /// </summary>
    public struct MapBodyHybridVisualLinked : IComponentData { }
}

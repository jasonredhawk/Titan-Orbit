using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [HYBRID] Client-only flag baked onto planet/asteroid/gem ghost prefabs:
    /// this Instantiated map ghost is waiting for <c>EcsWorldVisualizer</c> to create its GameObject.
    /// <para>
    /// [NETCODE] <see cref="GhostPrefabType.Client"/> — present on client Instantiates from bake.
    /// Do <b>not</b> <c>AddComponent</c> this at runtime on ghost entities — NetCode rejects / strips
    /// dynamic adds of ghost component types. Windows player EntityScenes often lack this bake;
    /// use <see cref="MapBodyHybridVisualSpawnRequest"/> for runtime backfill instead.
    /// </para>
    /// </summary>
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct MapBodyHybridVisualPending : IComponentData { }

    /// <summary>
    /// [HYBRID] Non-ghost runtime queue tag — same meaning as <see cref="MapBodyHybridVisualPending"/>.
    /// Added by <see cref="MapBodyHybridVisualRequestSystem"/> when Instantiates lack baked Pending
    /// (typical Windows player build). Safe to AddComponent on ghosts; visualizer drains both.
    /// </summary>
    public struct MapBodyHybridVisualSpawnRequest : IComponentData { }

    /// <summary>
    /// [HYBRID] Client-only flag: a GameObject proxy was created for this map ghost.
    /// Prevents re-queueing Pending / SpawnRequest. Added at runtime — not baked, not replicated.
    /// </summary>
    public struct MapBodyHybridVisualLinked : IComponentData { }
}

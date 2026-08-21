using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Marker on the four static PhysX boxes that bound the finite Euclidean map.
    /// Not a ghost — each world (server + predicted client) creates its own walls.
    /// </summary>
    public struct MapEdgeWallTag : IComponentData
    {
    }
}

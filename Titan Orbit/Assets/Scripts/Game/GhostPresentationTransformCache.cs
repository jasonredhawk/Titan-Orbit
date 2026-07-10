using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Per-frame ghost transforms captured in <see cref="ShipVisualSyncSystem"/> (PresentationSystemGroup).
    /// Hybrid bridge only: we use GameObject proxies instead of Entities Graphics / GhostPresentation.
    /// </summary>
    internal static class GhostPresentationTransformCache
    {
        internal struct Snapshot
        {
            public float3 Position;
            public quaternion Rotation;
            public float Scale;
        }

        static readonly Dictionary<Entity, Snapshot> Ships = new Dictionary<Entity, Snapshot>();
        static readonly Dictionary<Entity, Snapshot> PeopleTransports = new Dictionary<Entity, Snapshot>();
        static int _publishFrame = -1;

        internal static int PublishFrame => _publishFrame;

        internal static void BeginPublish(int frame)
        {
            _publishFrame = frame;
            Ships.Clear();
            PeopleTransports.Clear();
        }

        internal static void PublishShip(Entity entity, in Snapshot snapshot)
        {
            Ships[entity] = snapshot;
        }

        internal static void PublishPeopleTransport(Entity entity, in Snapshot snapshot)
        {
            PeopleTransports[entity] = snapshot;
        }

        internal static bool TryGetShip(Entity entity, out Snapshot snapshot) =>
            Ships.TryGetValue(entity, out snapshot);

        internal static bool TryGetPeopleTransport(Entity entity, out Snapshot snapshot) =>
            PeopleTransports.TryGetValue(entity, out snapshot);
    }
}

using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Per-frame dictionary of ghost presentation transforms for hybrid GameObject proxies.
    /// Written by <see cref="ShipVisualSyncSystem"/> in PresentationSystemGroup; read by
    /// <see cref="EcsWorldVisualizer"/> and other bridges that cannot use Entities Graphics.
    /// This is a temporary bridge — transforms here are for rendering only, never sim authority.
    /// </summary>
    internal static class GhostPresentationTransformCache
    {
        /// <summary>Position, rotation, and uniform scale snapshot for one ghost entity.</summary>
        internal struct Snapshot
        {
            public float3 Position;
            public quaternion Rotation;
            public float Scale;
        }

        // [STANDARD] Per-type dictionaries — ships and people transports publish separately.
        static readonly Dictionary<Entity, Snapshot> Ships = new Dictionary<Entity, Snapshot>();
        static readonly Dictionary<Entity, Snapshot> PeopleTransports = new Dictionary<Entity, Snapshot>();

        /// <summary>Unity frame index when BeginPublish last ran — detects stale reads.</summary>
        static int _publishFrame = -1;

        /// <summary>Frame counter from the most recent presentation publish pass.</summary>
        internal static int PublishFrame => _publishFrame;

        /// <summary>
        /// Clears all entries at the start of each presentation frame. Called from ShipVisualSyncSystem
        /// before repopulating from NetCode presentation transforms.
        /// </summary>
        internal static void BeginPublish(int frame)
        {
            // --- Reset dictionaries each presentation frame ---
            _publishFrame = frame;
            Ships.Clear();
            PeopleTransports.Clear();
        }

        /// <summary>Stores one ship ghost's presentation pose for EcsWorldVisualizer this frame.</summary>
        internal static void PublishShip(Entity entity, in Snapshot snapshot)
        {
            Ships[entity] = snapshot;
        }

        /// <summary>Stores one people-transport ghost's presentation pose for visual proxies.</summary>
        internal static void PublishPeopleTransport(Entity entity, in Snapshot snapshot)
        {
            PeopleTransports[entity] = snapshot;
        }

        /// <summary>
        /// Lookup ship presentation pose by entity. Returns false if not published this frame.
        /// </summary>
        internal static bool TryGetShip(Entity entity, out Snapshot snapshot) =>
            Ships.TryGetValue(entity, out snapshot);

        /// <summary>
        /// Lookup people-transport presentation pose by entity.
        /// </summary>
        internal static bool TryGetPeopleTransport(Entity entity, out Snapshot snapshot) =>
            PeopleTransports.TryGetValue(entity, out snapshot);
    }
}

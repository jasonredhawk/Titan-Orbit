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

        /// <summary>
        /// Last published ship pose per entity — used when this frame's publish has not run yet so
        /// MonoBehaviour readers never fall back to raw sim <see cref="LocalTransform"/> (pose fighting).
        /// </summary>
        static readonly Dictionary<Entity, Snapshot> LastShipSnapshots = new Dictionary<Entity, Snapshot>();

        /// <summary>
        /// Last people-transport pose — LateUpdate / onBeforeRender can read one frame late without
        /// falling back to a stuck spawn LocalTransform (which hid mid-flight spheres).
        /// </summary>
        static readonly Dictionary<Entity, Snapshot> LastPeopleTransportSnapshots = new Dictionary<Entity, Snapshot>();

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
            // --- Reset this-frame dictionaries; keep Last* so mid-flight poses survive a missed publish ---
            _publishFrame = frame;
            Ships.Clear();
            PeopleTransports.Clear();
        }

        /// <summary>Stores one ship ghost's presentation pose for EcsWorldVisualizer this frame.</summary>
        internal static void PublishShip(Entity entity, in Snapshot snapshot)
        {
            Ships[entity] = snapshot;
            LastShipSnapshots[entity] = snapshot;
        }

        /// <summary>Stores one people-transport ghost's presentation pose for visual proxies.</summary>
        internal static void PublishPeopleTransport(Entity entity, in Snapshot snapshot)
        {
            PeopleTransports[entity] = snapshot;
            LastPeopleTransportSnapshots[entity] = snapshot;
        }

        /// <summary>Drops last-known pose when a transport despawns (stops unbounded Last* growth).</summary>
        internal static void ForgetPeopleTransport(Entity entity)
        {
            PeopleTransports.Remove(entity);
            LastPeopleTransportSnapshots.Remove(entity);
        }

        /// <summary>
        /// Lookup ship presentation pose by entity. Returns false if not published this frame.
        /// </summary>
        internal static bool TryGetShip(Entity entity, out Snapshot snapshot)
        {
            if (Ships.TryGetValue(entity, out snapshot))
                return true;

            // [NETCODE] Stale presentation beats fresh sim — avoids camera/proxy oscillation one frame early.
            return LastShipSnapshots.TryGetValue(entity, out snapshot);
        }

        /// <summary>
        /// Copies ship entity keys from this-frame publish (falls back to last snapshots).
        /// [TITAN-ORBIT] Dictionary walk only — safe under TransformQuarantine (no archetype gather).
        /// </summary>
        internal static void CopyShipEntities(List<Entity> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            var source = Ships.Count > 0 ? Ships : LastShipSnapshots;
            foreach (var kv in source)
            {
                if (kv.Key != Entity.Null)
                    dst.Add(kv.Key);
            }
        }

        /// <summary>
        /// Lookup people-transport presentation pose by entity (this frame, else last published).
        /// </summary>
        internal static bool TryGetPeopleTransport(Entity entity, out Snapshot snapshot)
        {
            if (PeopleTransports.TryGetValue(entity, out snapshot))
                return true;
            return LastPeopleTransportSnapshots.TryGetValue(entity, out snapshot);
        }
    }
}

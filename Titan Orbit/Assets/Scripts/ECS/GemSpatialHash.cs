using System;
using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One live gem in the spatial hash: pose plus the NetCode ids relevancy and tractor pin need.
    /// Pickup / tractor look up <see cref="GemState"/> from <see cref="Entity"/> after a nearby gather.
    /// </summary>
    public struct GemSpatialEntry
    {
        /// <summary>Server gem entity (valid this tick).</summary>
        public Entity Entity;

        /// <summary>World pose used for cell insert and toroidal distance filter.</summary>
        public float3 Position;

        /// <summary>NetCode ghost id, or 0 if not assigned yet (cannot enter relevancy set).</summary>
        public int GhostId;

        /// <summary>
        /// <see cref="GemMotionState.TractorShipId"/> — the locking ship's
        /// <see cref="GhostOwner.NetworkId"/>, or 0 if unlocked.
        /// </summary>
        public int TractorShipId;
    }

    /// <summary>
    /// [TITAN-ORBIT] Toroidal XZ spatial hash of live gems for one server tick.
    /// <para>
    /// Unity NetCode's scale rule for thousands of collectibles: simulate all on the server,
    /// replicate only a spatial subset per connection. This hash is the shared nearby query
    /// for gem relevancy, tractor assignment, and pickup — not O(connections × all gems).
    /// </para>
    /// Cell size is smaller than relevancy radius so tractor search (~3–4.5u) does not dump
    /// an entire 40u fight into one bucket. Query cells overlapping the radius, then filter
    /// with <see cref="ToroidalMapEcs.ToroidalDistance"/> (seams included).
    /// Dispose every tick (Allocator.Temp).
    /// </summary>
    public struct GemSpatialHash : IDisposable
    {
        /// <summary>
        /// Cell edge in world units. 8u: tractor (~4.5) hits ~3×3 cells; relevancy 40u hits
        /// a ring of cells then distance-filters. Not 40u — that would clump whole fights.
        /// </summary>
        public const float CellSize = 8f;

        /// <summary>Packed gems this tick (index into this list is the hash payload).</summary>
        public NativeList<GemSpatialEntry> Entries;

        NativeParallelMultiHashMap<int, int> _cells;
        NativeParallelMultiHashMap<int, int> _byTractorShip;
        int _cellsX;
        int _cellsZ;
        float _mapW;
        float _mapH;
        bool _created;

        /// <summary>
        /// Builds the hash from parallel gem arrays. Optional ghost / motion arrays pin
        /// tractored gems for relevancy; pass default (uncreated) when only nearby queries matter.
        /// </summary>
        public static GemSpatialHash Build(
            NativeArray<Entity> entities,
            NativeArray<LocalTransform> transforms,
            NativeArray<GhostInstance> ghosts,
            NativeArray<GemMotionState> motions,
            float mapW,
            float mapH,
            Allocator allocator)
        {
            int n = entities.IsCreated ? entities.Length : 0;
            var hash = new GemSpatialHash
            {
                Entries = new NativeList<GemSpatialEntry>(math.max(n, 8), allocator),
                _cells = new NativeParallelMultiHashMap<int, int>(math.max(n, 8), allocator),
                _byTractorShip = new NativeParallelMultiHashMap<int, int>(math.max(8, n / 4), allocator),
                _mapW = mapW,
                _mapH = mapH,
                _created = true,
            };

            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH) || n <= 0)
            {
                hash._cellsX = 1;
                hash._cellsZ = 1;
                return hash;
            }

            hash._cellsX = math.max(1, (int)math.ceil(mapW / CellSize));
            hash._cellsZ = math.max(1, (int)math.ceil(mapH / CellSize));

            bool haveGhosts = ghosts.IsCreated && ghosts.Length == n;
            bool haveMotion = motions.IsCreated && motions.Length == n;

            for (int i = 0; i < n; i++)
            {
                Entity e = entities[i];
                if (e == Entity.Null)
                    continue;

                float3 pos = transforms[i].Position;
                int ghostId = haveGhosts ? ghosts[i].ghostId : 0;
                int tractorId = haveMotion ? motions[i].TractorShipId : 0;

                var entry = new GemSpatialEntry
                {
                    Entity = e,
                    Position = pos,
                    GhostId = ghostId,
                    TractorShipId = tractorId,
                };
                int index = hash.Entries.Length;
                hash.Entries.Add(entry);
                hash._cells.Add(hash.CellKeyFromPosition(pos), index);
                if (tractorId != 0)
                    hash._byTractorShip.Add(tractorId, index);
            }

            return hash;
        }

        /// <summary>Nearby-only build (tractor / pickup) — no ghost or lock ids.</summary>
        public static GemSpatialHash Build(
            NativeArray<Entity> entities,
            NativeArray<LocalTransform> transforms,
            float mapW,
            float mapH,
            Allocator allocator)
        {
            return Build(entities, transforms, default, default, mapW, mapH, allocator);
        }

        /// <summary>True after <see cref="Build"/> (even when the map has zero gems).</summary>
        public bool IsCreated => _created;

        /// <summary>How many gems were inserted this tick.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;

        /// <summary>
        /// Fills <paramref name="dst"/> with entry indices whose toroidal distance to
        /// <paramref name="pos"/> is ≤ <paramref name="radius"/>.
        /// <paramref name="seenScratch"/> dedupes cell overlap (cleared when
        /// <paramref name="clearDst"/> is true).
        /// </summary>
        public void GatherNearby(
            float3 pos,
            float radius,
            NativeList<int> dst,
            NativeHashSet<int> seenScratch,
            bool clearDst = true)
        {
            if (clearDst)
            {
                dst.Clear();
                seenScratch.Clear();
            }

            if (!_created || !Entries.IsCreated || Entries.Length == 0 || radius <= 0f)
                return;
            if (!ToroidalMapEcs.IsValidMapSize(_mapW, _mapH))
                return;

            int cellRadius = (int)math.ceil(radius / CellSize) + 1;
            int baseX = CellX(pos.x);
            int baseZ = CellZ(pos.z);

            for (int dz = -cellRadius; dz <= cellRadius; dz++)
            {
                int cz = WrapCell(baseZ + dz, _cellsZ);
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    int cx = WrapCell(baseX + dx, _cellsX);
                    int key = cx + cz * _cellsX;
                    if (!_cells.TryGetFirstValue(key, out int idx, out var it))
                        continue;

                    do
                    {
                        if (!seenScratch.Add(idx))
                            continue;
                        float d = ToroidalMapEcs.ToroidalDistance(pos, Entries[idx].Position, _mapW, _mapH);
                        if (d <= radius)
                            dst.Add(idx);
                    } while (_cells.TryGetNextValue(out idx, ref it));
                }
            }
        }

        /// <summary>
        /// Appends gems whose <see cref="GemSpatialEntry.TractorShipId"/> equals
        /// <paramref name="tractorShipNetworkId"/> (relevancy pin / pickup of far-wing locks).
        /// Does not clear <paramref name="dst"/>. Reuses <paramref name="seenScratch"/> from
        /// <see cref="GatherNearby"/> so a ship does not allocate a second hash set.
        /// </summary>
        public void AppendPinnedToShip(
            int tractorShipNetworkId,
            NativeList<int> dst,
            NativeHashSet<int> seenScratch)
        {
            if (!_created || tractorShipNetworkId == 0 || !Entries.IsCreated)
                return;
            if (!_byTractorShip.TryGetFirstValue(tractorShipNetworkId, out int idx, out var it))
                return;

            do
            {
                if (seenScratch.IsCreated && !seenScratch.Add(idx))
                    continue;
                dst.Add(idx);
            } while (_byTractorShip.TryGetNextValue(out idx, ref it));
        }

        /// <summary>Releases native containers. Safe to call on a default struct.</summary>
        public void Dispose()
        {
            if (Entries.IsCreated)
                Entries.Dispose();
            if (_cells.IsCreated)
                _cells.Dispose();
            if (_byTractorShip.IsCreated)
                _byTractorShip.Dispose();
            _created = false;
        }

        int CellKeyFromPosition(float3 pos)
        {
            return CellX(pos.x) + CellZ(pos.z) * _cellsX;
        }

        /// <summary>
        /// Cell index on X. Map is centered <c>[-half, half)</c>; shift to <c>[0, mapW)</c> first.
        /// </summary>
        int CellX(float x)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(x, 0f, 0f), _mapW, _mapH);
            float u = wrapped.x + _mapW * 0.5f;
            int c = (int)math.floor(u / CellSize);
            return math.clamp(c, 0, _cellsX - 1);
        }

        /// <summary>Cell index on Z. Same centered-to-unit shift as <see cref="CellX"/>.</summary>
        int CellZ(float z)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(0f, 0f, z), _mapW, _mapH);
            float v = wrapped.z + _mapH * 0.5f;
            int c = (int)math.floor(v / CellSize);
            return math.clamp(c, 0, _cellsZ - 1);
        }

        static int WrapCell(int c, int count)
        {
            if (count <= 0)
                return 0;
            int m = c % count;
            return m < 0 ? m + count : m;
        }
    }
}

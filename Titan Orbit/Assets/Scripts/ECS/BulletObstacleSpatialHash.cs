using System;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Broadphase kind stored in <see cref="BulletObstacleSpatialHash"/>.
    /// Planets / moons stay on a linear scan (few, and moons orbit).
    /// </summary>
    public enum BulletObstacleKind : byte
    {
        Ship = 0,
        Asteroid = 1,
        Transport = 2,
    }

    /// <summary>One hashed combat body for a single server tick.</summary>
    public struct BulletObstacleEntry
    {
        public Entity Entity;
        public float3 Position;
        public float Radius;
        public BulletObstacleKind Kind;
        public byte Team;
        public int OwnerNetworkId;
    }

    /// <summary>
    /// [TITAN-ORBIT] Toroidal XZ spatial hash of ships / asteroids / transports
    /// for <see cref="BulletSimulationSystem"/>
    /// (<c>WorldSystemFilterFlags.ServerSimulation</c> only — never client join gathers).
    /// <para>
    /// Built once per tick when a live bullet exists or a shot is about to spawn.
    /// Empty ticks skip this. Each bullet segment queries nearby cells instead of
    /// walking every rock on the map. Exact hit math stays in
    /// <c>TryResolveBulletHit</c> — this is broadphase only.
    /// Map size comes from <see cref="MapStateSingleton"/> (passed in).
    /// Dispose every tick (Allocator.Temp).
    /// </para>
    /// </summary>
    public struct BulletObstacleSpatialHash : IDisposable
    {
        /// <summary>
        /// Cell edge in world units. Larger than <see cref="GemSpatialHash.CellSize"/>
        /// because combat bodies are bigger than gems.
        /// </summary>
        public const float CellSize = 16f;

        /// <summary>Bullet-scale pad so a heavy bolt still finds its cell.</summary>
        public const float BulletPad = 0.85f;

        public NativeList<BulletObstacleEntry> Entries;

        NativeParallelMultiHashMap<int, int> _cells;
        int _cellsX;
        int _cellsZ;
        float _mapW;
        float _mapH;
        bool _created;

        /// <summary>True after <see cref="Build"/>.</summary>
        public bool IsCreated => _created;

        /// <summary>How many bodies were inserted this tick.</summary>
        public int Count => Entries.IsCreated ? Entries.Length : 0;

        /// <summary>
        /// Builds the hash from the same entity sets Phase A already needs.
        /// Skips dead / stowed / destroyed bodies so they never enter a cell.
        /// </summary>
        public static BulletObstacleSpatialHash Build(
            EntityManager em,
            EntityQuery shipQuery,
            EntityQuery asteroidQuery,
            EntityQuery transportQuery,
            float mapW,
            float mapH,
            Allocator allocator)
        {
            using var ships = shipQuery.ToEntityArray(Allocator.Temp);
            using var shipXf = shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var shipOwners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var asteroids = asteroidQuery.ToEntityArray(Allocator.Temp);
            using var asteroidXf = asteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var asteroidStates = asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var transports = transportQuery.ToEntityArray(Allocator.Temp);
            using var transportXf = transportQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var transportStates = transportQuery.ToComponentDataArray<PeopleTransportState>(Allocator.Temp);

            int n = ships.Length + asteroids.Length + transports.Length;
            // MEGA hulls stamp many cells — oversize so the map does not resize mid-build.
            var hash = new BulletObstacleSpatialHash
            {
                Entries = new NativeList<BulletObstacleEntry>(math.max(n, 8), allocator),
                _cells = new NativeParallelMultiHashMap<int, int>(math.max(n * 8, 32), allocator),
                _mapW = mapW,
                _mapH = mapH,
                _created = true,
            };

            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
            {
                hash._cellsX = 1;
                hash._cellsZ = 1;
                return hash;
            }

            hash._cellsX = math.max(1, (int)math.ceil(mapW / CellSize));
            hash._cellsZ = math.max(1, (int)math.ceil(mapH / CellSize));

            for (int i = 0; i < ships.Length; i++)
            {
                Entity e = ships[i];
                var ship = shipStates[i];
                if (ship.IsDead)
                    continue;
                if (em.HasComponent<ShipTurretControlState>(e) &&
                    em.GetComponentData<ShipTurretControlState>(e).IsControlling)
                    continue;

                var xf = shipXf[i];
                float3 center = MegaShipCombatAim.GetAimPoint(em, e, xf);
                // Regular ships: cheap hull radius. MEGA AABB is once per mega, not every fighter.
                float radius = BodyCollisionMath.GetShipHullRadiusWorld(xf.Scale);
                bool isMega = em.HasComponent<MegaShipState>(e) &&
                              em.GetComponentData<MegaShipState>(e).IsMega;
                if (isMega && em.HasComponent<PhysicsCollider>(e))
                {
                    var physicsCollider = em.GetComponentData<PhysicsCollider>(e);
                    radius = MegaShipCombatAim.GetHitRadiusWorld(em, e, physicsCollider, xf.Scale);
                }

                hash.Add(new BulletObstacleEntry
                {
                    Entity = e,
                    Position = center,
                    Radius = radius,
                    Kind = BulletObstacleKind.Ship,
                    Team = (byte)ship.Team,
                    OwnerNetworkId = shipOwners[i].NetworkId,
                });
            }

            for (int i = 0; i < asteroids.Length; i++)
            {
                var asteroid = asteroidStates[i];
                if (asteroid.IsDestroyed || asteroid.Health <= 0f)
                    continue;

                float3 pos = asteroidXf[i].Position;
                float radius = BulletCollision.AsteroidHitRadiusForSweep(asteroidXf[i].Scale, 1f);
                hash.Add(new BulletObstacleEntry
                {
                    Entity = asteroids[i],
                    Position = pos,
                    Radius = radius,
                    Kind = BulletObstacleKind.Asteroid,
                });
            }

            for (int i = 0; i < transports.Length; i++)
            {
                var t = transportStates[i];
                if (t.Amount <= 0f || t.Health <= 0f)
                    continue;

                float3 pos = transportXf[i].Position;
                float radius = PeopleTransportMath.GetBulletHitRadius(transportXf[i].Scale);
                hash.Add(new BulletObstacleEntry
                {
                    Entity = transports[i],
                    Position = pos,
                    Radius = radius,
                    Kind = BulletObstacleKind.Transport,
                    Team = t.Team,
                });
            }

            return hash;
        }

        /// <summary>
        /// Entry indices in cells overlapping the segment. Large hulls were
        /// stamped into every cell they cover, so the query radius is only
        /// the step plus a small pad — not the MEGA covering sphere.
        /// </summary>
        public void GatherAlongSegment(
            float3 from,
            float3 to,
            NativeList<int> dst,
            NativeHashSet<int> seenScratch)
        {
            float step = math.distance(from, to);
            GatherNearby(from, step + BulletPad + 1f, dst, seenScratch);
        }

        /// <summary>
        /// Unique entries in cells around <paramref name="pos"/>. No distance
        /// filter — a MEGA center can be far while this cell still overlaps the hull.
        /// </summary>
        public void GatherNearby(
            float3 pos,
            float radius,
            NativeList<int> dst,
            NativeHashSet<int> seenScratch)
        {
            dst.Clear();
            seenScratch.Clear();

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
                        dst.Add(idx);
                    } while (_cells.TryGetNextValue(out idx, ref it));
                }
            }
        }

        /// <summary>Releases native containers. Safe to call on a default struct.</summary>
        public void Dispose()
        {
            if (Entries.IsCreated)
                Entries.Dispose();
            if (_cells.IsCreated)
                _cells.Dispose();
            _created = false;
        }

        void Add(in BulletObstacleEntry entry)
        {
            int index = Entries.Length;
            Entries.Add(entry);
            int cellR = (int)math.ceil((entry.Radius + BulletPad) / CellSize);
            int baseX = CellX(entry.Position.x);
            int baseZ = CellZ(entry.Position.z);
            if (cellR <= 0)
            {
                _cells.Add(baseX + baseZ * _cellsX, index);
                return;
            }

            for (int dz = -cellR; dz <= cellR; dz++)
            {
                int cz = WrapCell(baseZ + dz, _cellsZ);
                for (int dx = -cellR; dx <= cellR; dx++)
                    _cells.Add(WrapCell(baseX + dx, _cellsX) + cz * _cellsX, index);
            }
        }

        int CellX(float x)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(x, 0f, 0f), _mapW, _mapH);
            float u = wrapped.x + _mapW * 0.5f;
            int c = (int)math.floor(u / CellSize);
            return math.clamp(c, 0, _cellsX - 1);
        }

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

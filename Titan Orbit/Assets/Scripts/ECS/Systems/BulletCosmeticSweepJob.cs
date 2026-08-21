using TitanOrbit.Data;
using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Blittable cosmetic obstacle for <see cref="BulletCosmeticSweepJob"/>.
    /// Copied from hybrid-proxy spheres on the client — no EntityManager in Burst.
    /// MEGA hulls emit one body per baked part (box or sphere) so tracers stop
    /// on the mesh instead of the covering AABB sphere.
    /// </summary>
    public struct CosmeticSweepBody
    {
        public float3 Position;
        public float Radius;
        public float2 BoxHalfExtents;
        public float BoxYawRadians;
        public float Scale;
        public float MoonBodyRadius;
        public float MoonShieldRadius;
        public float CurrentShield;
        public int PlanetLevel;
        public int PlanetId;
        public int OwnerNetworkId;
        public int SlotIndex;
        public byte Kind;
        public byte Team;
        public byte IsHome;
    }

    /// <summary>One straight (non-homing) tracer step for the cosmetic Burst sweep.</summary>
    public struct CosmeticSweepRequest
    {
        public float3 Position;
        public float3 Velocity;
        public float Dt;
        public float Traveled;
        public float RemainingLifetime;
        public float MaxDistance;
        public float ScaleMultiplier;
        public int OwnerNetworkId;
        public byte OwnerTeam;
        public byte DamageFilter;
        public byte HealFriendly;
    }

    /// <summary>Burst outcome for one straight tracer.</summary>
    public struct CosmeticSweepResult
    {
        public const byte Fly = 0;
        public const byte Hit = 1;
        public const byte Expire = 2;

        public byte Outcome;
        public float3 HitPoint;
        public float3 NewPos;
        public float3 NewVelocity;
        public float NewTraveled;
        public float NewLifetime;
    }

    /// <summary>
    /// Client presentation sweep — same sphere/box math as the server job, no RPCs.
    /// MEGA ships are one oriented box or sphere per baked part (not a covering hull
    /// sphere). Mega volleys stay on this path so LateUpdate is not O(tracers × bodies).
    /// </summary>
    [BurstCompile]
    public struct BulletCosmeticSweepJob : IJob
    {
        public const byte KindPlanet = 0;
        public const byte KindMoon = 1;
        public const byte KindShip = 2;
        public const byte KindAsteroid = 3;
        public const byte KindDefense = 4;
        public const byte KindTransport = 5;
        public const byte KindDrone = 6;

        public const float CellSize = 16f;

        public float MapW;
        public float MapH;
        public double MoonElapsed;
        public int MaxSubsteps;
        public int CellsX;
        public int CellsZ;

        [ReadOnly] public NativeArray<CosmeticSweepRequest> Requests;
        [ReadOnly] public NativeArray<CosmeticSweepBody> Bodies;
        [ReadOnly] public NativeArray<int> AlwaysTest;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> Cells;
        public NativeList<int> Nearby;
        public NativeHashSet<int> Seen;
        public NativeArray<CosmeticSweepResult> Results;

        public static void Run(
            NativeArray<CosmeticSweepRequest> requests,
            NativeArray<CosmeticSweepResult> results,
            NativeArray<CosmeticSweepBody> bodies,
            NativeArray<int> alwaysTest,
            NativeParallelMultiHashMap<int, int> cells,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH,
            double moonElapsed,
            int maxSubsteps,
            int cellsX,
            int cellsZ)
        {
            new BulletCosmeticSweepJob
            {
                MapW = mapW,
                MapH = mapH,
                MoonElapsed = moonElapsed,
                MaxSubsteps = math.clamp(maxSubsteps, 1, BulletCollision.MaxAdvanceSubsteps),
                CellsX = math.max(1, cellsX),
                CellsZ = math.max(1, cellsZ),
                Requests = requests,
                Bodies = bodies,
                AlwaysTest = alwaysTest,
                Cells = cells,
                Nearby = nearby,
                Seen = seen,
                Results = results,
            }.Run();
        }

        [BurstCompile]
        public void Execute()
        {
            for (int i = 0; i < Requests.Length; i++)
            {
                var req = Requests[i];
                float lifetime = req.RemainingLifetime - req.Dt;
                BulletFlight.GetStep(req.Position, req.Velocity, req.Dt, out float3 end, out float3 velOnShell, out int substeps);
                if (substeps > MaxSubsteps)
                    substeps = MaxSubsteps;

                float step = math.distance(req.Position, end);
                float traveled = req.Traveled + step;

                GatherNearby(req.Position, math.distance(req.Position, end) + 1.85f);

                float3 cursor = req.Position;
                bool hit = false;
                float3 hitPoint = end;
                for (int s = 0; s < substeps; s++)
                {
                    float3 next = BulletFlight.SubstepEnd(req.Position, end, s, substeps);
                    if (TryHitSegment(in req, cursor, next, out hitPoint))
                    {
                        hit = true;
                        break;
                    }

                    cursor = next;
                }

                if (hit)
                {
                    Results[i] = new CosmeticSweepResult
                    {
                        Outcome = CosmeticSweepResult.Hit,
                        HitPoint = hitPoint,
                        NewPos = hitPoint,
                        NewVelocity = velOnShell,
                        NewTraveled = traveled,
                        NewLifetime = lifetime,
                    };
                    continue;
                }

                if (lifetime <= 0f || traveled >= math.max(0.5f, req.MaxDistance))
                {
                    Results[i] = new CosmeticSweepResult
                    {
                        Outcome = CosmeticSweepResult.Expire,
                        HitPoint = end,
                        NewPos = end,
                        NewVelocity = velOnShell,
                        NewTraveled = traveled,
                        NewLifetime = lifetime,
                    };
                    continue;
                }

                Results[i] = new CosmeticSweepResult
                {
                    Outcome = CosmeticSweepResult.Fly,
                    HitPoint = end,
                    NewPos = end,
                    NewVelocity = velOnShell,
                    NewTraveled = traveled,
                    NewLifetime = lifetime,
                };
            }
        }

        bool TryHitSegment(in CosmeticSweepRequest req, float3 from, float3 to, out float3 hitPoint)
        {
            hitPoint = to;
            float bestT = float.MaxValue;
            float3 bestHit = to;
            byte bestKind = KindAsteroid;
            bool any = false;
            float bestDefenseT = float.MaxValue;
            float3 bestDefenseHit = to;
            bool anyDefense = false;
            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            var filter = (BulletDamageFilter)req.DamageFilter;

            for (int a = 0; a < AlwaysTest.Length; a++)
            {
                if (TryBody(in req, AlwaysTest[a], from, to, filter,
                        ref bestT, ref bestHit, ref bestKind, ref any,
                        ref bestDefenseT, ref bestDefenseHit, ref anyDefense, delta, deltaLenSq))
                    continue;
            }

            for (int n = 0; n < Nearby.Length; n++)
            {
                if (TryBody(in req, Nearby[n], from, to, filter,
                        ref bestT, ref bestHit, ref bestKind, ref any,
                        ref bestDefenseT, ref bestDefenseHit, ref anyDefense, delta, deltaLenSq))
                    continue;
            }

            if (!any)
                return false;

            if (anyDefense &&
                bestKind == KindPlanet &&
                PlanetaryDefenseHitScan.PreferDefenseOverPlanetBody(bestDefenseT, bestT))
            {
                bestHit = bestDefenseHit;
            }

            hitPoint = bestHit;
            return true;
        }

        bool TryBody(
            in CosmeticSweepRequest req,
            int b,
            float3 from,
            float3 to,
            BulletDamageFilter filter,
            ref float bestT,
            ref float3 bestHit,
            ref byte bestKind,
            ref bool any,
            ref float bestDefenseT,
            ref float3 bestDefenseHit,
            ref bool anyDefense,
            float3 delta,
            float deltaLenSq)
        {
            if (b < 0 || b >= Bodies.Length)
                return false;

            var body = Bodies[b];
            if (!PassesTeam(in body, req.OwnerTeam, req.OwnerNetworkId, req.HealFriendly != 0))
                return false;
            if (!PassesFilter(filter, body.Kind))
                return false;

            bool hit;
            float3 hp;
            if (body.Kind == KindMoon)
            {
                bool friendly = body.Team == req.OwnerTeam;
                float radius = friendly ? body.MoonBodyRadius : body.MoonShieldRadius;
                hit = BulletCollision.SegmentHitsMoonNear(
                    from, to, body.Position, body.Scale,
                    body.PlanetLevel, body.PlanetId, MoonElapsed,
                    body.IsHome != 0, radius, MapW, MapH, out hp);
            }
            else
            {
                float pad = math.clamp(req.ScaleMultiplier * 0.18f, 0f, 0.85f);
                if (body.Kind == KindShip &&
                    body.BoxHalfExtents.x > 0.01f &&
                    body.BoxHalfExtents.y > 0.01f)
                {
                    hit = BulletCollision.SegmentHitsOrientedBoxToroidal(
                        from, to, body.Position, body.BoxHalfExtents + pad,
                        body.BoxYawRadians, MapW, MapH, out hp);
                }
                else
                {
                    float radius = body.Radius;
                    if (body.Kind == KindDefense)
                    {
                        radius = PlanetaryDefenseHitScan.ExpandRadiusForBulletScale(
                            body.Radius, req.ScaleMultiplier);
                    }
                    else if (body.Kind == KindShip)
                    {
                        radius += pad;
                    }

                    hit = BulletCollision.SegmentHitsSphereToroidal(
                        from, to, body.Position, radius, MapW, MapH, out hp);
                }
            }

            if (!hit)
                return false;

            if (body.Kind == KindDefense)
            {
                float defenseT = deltaLenSq < 1e-8f
                    ? 0f
                    : math.dot(hp - from, delta) / deltaLenSq;
                if (defenseT <= bestDefenseT)
                {
                    bestDefenseT = defenseT;
                    bestDefenseHit = hp;
                    anyDefense = true;
                }
            }

            float t = BulletCollision.GetSegmentHitParameter(from, to, hp);
            if (t > bestT)
                return false;
            bestT = t;
            bestHit = hp;
            bestKind = body.Kind;
            any = true;
            return true;
        }

        void GatherNearby(float3 pos, float radius)
        {
            Nearby.Clear();
            Seen.Clear();
            if (!Cells.IsCreated || radius <= 0f)
                return;

            int cellRadius = (int)math.ceil(radius / CellSize) + 1;
            int baseX = CellX(pos.x);
            int baseZ = CellZ(pos.z);
            for (int dz = -cellRadius; dz <= cellRadius; dz++)
            {
                int cz = WrapCell(baseZ + dz, CellsZ);
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    int cx = WrapCell(baseX + dx, CellsX);
                    int key = cx + cz * CellsX;
                    if (!Cells.TryGetFirstValue(key, out int idx, out var it))
                        continue;
                    do
                    {
                        if (!Seen.Add(idx))
                            continue;
                        Nearby.Add(idx);
                    } while (Cells.TryGetNextValue(out idx, ref it));
                }
            }
        }

        int CellX(float x)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(x, 0f, 0f), MapW, MapH);
            float u = wrapped.x + MapW * 0.5f;
            int c = (int)math.floor(u / CellSize);
            return math.clamp(c, 0, CellsX - 1);
        }

        int CellZ(float z)
        {
            float3 wrapped = ToroidalMapEcs.Wrap(new float3(0f, 0f, z), MapW, MapH);
            float v = wrapped.z + MapH * 0.5f;
            int c = (int)math.floor(v / CellSize);
            return math.clamp(c, 0, CellsZ - 1);
        }

        static int WrapCell(int c, int count)
        {
            if (count <= 0)
                return 0;
            int m = c % count;
            return m < 0 ? m + count : m;
        }

        static bool PassesTeam(in CosmeticSweepBody o, byte ownerTeam, int ownerNetworkId, bool healFriendly)
        {
            if (o.Kind == KindShip)
            {
                if (ownerNetworkId > 0 && o.OwnerNetworkId == ownerNetworkId)
                    return false;
                if (healFriendly)
                    return true;
                return o.Team != ownerTeam;
            }

            if (o.Kind == KindTransport || o.Kind == KindDrone)
            {
                if (o.Team == ownerTeam)
                    return false;
                if (ownerNetworkId > 0 && o.OwnerNetworkId == ownerNetworkId)
                    return false;
                return true;
            }

            if (o.Kind == KindDefense)
                return o.Team != ownerTeam;
            return true;
        }

        static bool PassesFilter(BulletDamageFilter filter, byte kind)
        {
            if (kind == KindPlanet || kind == KindMoon)
                return true;
            switch (filter)
            {
                case BulletDamageFilter.AsteroidsOnly:
                    return kind == KindAsteroid;
                case BulletDamageFilter.ShipsOnly:
                    return kind == KindShip || kind == KindDrone || kind == KindDefense;
                case BulletDamageFilter.ShipsAndTransports:
                    return kind == KindShip || kind == KindTransport || kind == KindAsteroid;
                default:
                    return true;
            }
        }
    }
}

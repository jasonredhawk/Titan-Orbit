using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Burst Phase A: advance every live bullet and nearest-hit scan against the
    /// spatial hash plus pre-copied planet / PD / drone spheres.
    /// <para>
    /// <see cref="BulletSimulationSystem"/> cannot be Burst (RPCs / ECB). This job
    /// is the O(bullets × nearby) sweep. Hits are applied on the main thread.
    /// Map size is passed in from <see cref="MapStateSingleton"/>.
    /// Server-only companion of <c>WorldSystemFilterFlags.ServerSimulation</c>.
    /// </para>
    /// </summary>
    [BurstCompile]
    public struct BulletAdvanceJob : IJob
    {
        public const byte OutcomeFly = 0;
        public const byte OutcomeExpire = 1;
        public const byte OutcomeHit = 2;

        public float Dt;
        public float MapW;
        public float MapH;
        public double MoonElapsed;

        public NativeArray<BulletElement> Bullets;
        public NativeArray<byte> Outcomes;
        public NativeArray<float3> HitPoints;
        public NativeArray<float3> StepFrom;
        public NativeArray<float3> StepTo;

        [ReadOnly] public BulletObstacleSpatialHash Hash;
        public NativeList<int> Nearby;
        public NativeHashSet<int> Seen;

        [ReadOnly] public NativeArray<BulletJobPlanet> Planets;
        [ReadOnly] public NativeArray<PlanetaryDefenseHitTarget> Defense;
        [ReadOnly] public NativeArray<DroneHitTarget> Drones;

        /// <summary>
        /// 1 when debug self-harm rockets are enabled. Homing rockets past
        /// <see cref="SelfHarmArmDelay"/> may then collide with the shooter / same team —
        /// otherwise this Burst sweep skips those hulls and never hands the segment to
        /// managed hit resolution (rockets fly through you).
        /// </summary>
        public byte AllowSelfHarmHits;

        /// <summary>Copied from <c>TitanOrbitDebugFlags.SelfHarmArmDelaySeconds</c> (Burst-safe).</summary>
        public float SelfHarmArmDelay;

        [BurstCompile]
        public void Execute()
        {
            for (int i = 0; i < Bullets.Length; i++)
            {
                var b = Bullets[i];
                float3 start = b.Position;
                BulletFlight.GetStep(start, b.Velocity, Dt, out float3 end, out int substeps);
                float stepDistance = math.distance(start, end);
                StepFrom[i] = start;
                StepTo[i] = end;

                bool lifetimeExpired = b.Lifetime > 0f && (b.Age + Dt) >= b.Lifetime;
                bool rangeExpired = (b.Traveled + stepDistance) >= b.MaxDistance;

                Hash.GatherAlongSegment(start, end, Nearby, Seen);

                float3 cursor = start;
                bool hit = false;
                float3 hitPoint = end;
                for (int s = 0; s < substeps; s++)
                {
                    float3 next = BulletFlight.SubstepEnd(start, end, s, substeps);
                    if (TryHitSegment(in b, cursor, next, out hitPoint))
                    {
                        hit = true;
                        break;
                    }

                    cursor = next;
                }

                if (hit)
                {
                    Outcomes[i] = OutcomeHit;
                    HitPoints[i] = hitPoint;
                    continue;
                }

                b.Age += Dt;
                b.Traveled += stepDistance;
                if (lifetimeExpired || rangeExpired)
                {
                    Outcomes[i] = OutcomeExpire;
                    Bullets[i] = b;
                    continue;
                }

                b.Position = end;
                Bullets[i] = b;
                Outcomes[i] = OutcomeFly;
            }
        }

        bool TryHitSegment(in BulletElement b, float3 from, float3 to, out float3 hitPoint)
        {
            hitPoint = to;
            float bestT = float.MaxValue;
            float3 bestHit = to;
            bool any = false;

            for (int n = 0; n < Nearby.Length; n++)
            {
                var e = Hash.Entries[Nearby[n]];
                if (!AllowsHashKind(b.DamageFilter, e.Kind))
                    continue;
                if (e.Kind == BulletObstacleKind.Ship)
                {
                    bool selfHarm = AllowSelfHarmHits != 0 &&
                                    b.Homing != 0 &&
                                    b.Age >= SelfHarmArmDelay;
                    if (!selfHarm)
                    {
                        if (e.Team == b.OwnerTeam)
                            continue;
                        if (b.OwnerNetworkId > 0 && e.OwnerNetworkId == b.OwnerNetworkId)
                            continue;
                    }
                }
                else if (e.Kind == BulletObstacleKind.Transport)
                {
                    if (e.Team == 0 || e.Team == b.OwnerTeam)
                        continue;
                }

                float radius = e.Radius;
                if (e.Kind == BulletObstacleKind.Ship)
                    radius += math.clamp(b.ScaleMultiplier * 0.18f, 0f, 0.85f);

                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, e.Position, radius, MapW, MapH, out float3 hp))
                    continue;
                if (!KeepNearest(from, to, hp, ref bestT, ref bestHit))
                    continue;
                any = true;
            }

            for (int p = 0; p < Planets.Length; p++)
            {
                var planet = Planets[p];
                if (BulletCollision.SegmentHitsPlanetToroidal(
                        from, to, planet.Position, planet.Scale, MapW, MapH, out float3 planetHit) &&
                    KeepNearest(from, to, planetHit, ref bestT, ref bestHit))
                    any = true;

                if (!planet.HasMoon)
                    continue;

                bool friendly = planet.Ownership == b.OwnerTeam;
                float moonR = friendly ? planet.MoonBodyRadius : planet.MoonShieldRadius;
                if (BulletCollision.SegmentHitsMoonNear(
                        from, to, planet.Position, planet.Scale,
                        planet.PlanetLevel, planet.PlanetId, MoonElapsed,
                        planet.IsHome, moonR, MapW, MapH, out float3 moonHit) &&
                    KeepNearest(from, to, moonHit, ref bestT, ref bestHit))
                    any = true;
            }

            if (AllowsDefense(b.DamageFilter))
            {
                for (int d = 0; d < Defense.Length; d++)
                {
                    var pad = Defense[d];
                    if (pad.Team == b.OwnerTeam)
                        continue;
                    float radius = PlanetaryDefenseHitScan.ExpandRadiusForBulletScale(
                        pad.HitRadius, b.ScaleMultiplier);
                    if (!BulletCollision.SegmentHitsSphereToroidal(
                            from, to, pad.Position, radius, MapW, MapH, out float3 hp))
                        continue;
                    if (!KeepNearest(from, to, hp, ref bestT, ref bestHit))
                        continue;
                    any = true;
                }
            }

            if (AllowsDrone(b.DamageFilter))
            {
                for (int d = 0; d < Drones.Length; d++)
                {
                    var drone = Drones[d];
                    if (drone.Team == b.OwnerTeam)
                        continue;
                    if (b.OwnerNetworkId > 0 && drone.OwnerNetworkId == b.OwnerNetworkId)
                        continue;
                    float radius = DroneSwarmPositioning.DroneHitSphereRadius
                        * math.max(0.25f, drone.HitRadiusScale > 0.01f ? drone.HitRadiusScale : 1f);
                    if (!BulletCollision.SegmentHitsSphereToroidal(
                            from, to, drone.Position, radius, MapW, MapH, out float3 hp))
                        continue;
                    if (!KeepNearest(from, to, hp, ref bestT, ref bestHit))
                        continue;
                    any = true;
                }
            }

            if (!any)
                return false;
            hitPoint = bestHit;
            return true;
        }

        static bool KeepNearest(float3 from, float3 to, float3 candidate, ref float bestT, ref float3 bestHit)
        {
            float t = BulletCollision.GetSegmentHitParameter(from, to, candidate);
            if (t > bestT)
                return false;
            bestT = t;
            bestHit = candidate;
            return true;
        }

        static bool AllowsHashKind(BulletDamageFilter filter, BulletObstacleKind kind)
        {
            switch (filter)
            {
                case BulletDamageFilter.AsteroidsOnly:
                    return kind == BulletObstacleKind.Asteroid;
                case BulletDamageFilter.ShipsOnly:
                    return kind == BulletObstacleKind.Ship;
                case BulletDamageFilter.ShipsAndTransports:
                    return kind == BulletObstacleKind.Ship ||
                           kind == BulletObstacleKind.Transport ||
                           kind == BulletObstacleKind.Asteroid;
                default:
                    return true;
            }
        }

        static bool AllowsDefense(BulletDamageFilter filter)
        {
            return filter != BulletDamageFilter.AsteroidsOnly;
        }

        static bool AllowsDrone(BulletDamageFilter filter)
        {
            return filter == BulletDamageFilter.Everything ||
                   filter == BulletDamageFilter.ShipsOnly;
        }
    }

    /// <summary>One planet + moon sphere for <see cref="BulletAdvanceJob"/> (main-thread copy).</summary>
    public struct BulletJobPlanet
    {
        public float3 Position;
        public float Scale;
        public float MoonBodyRadius;
        public float MoonShieldRadius;
        public int PlanetLevel;
        public int PlanetId;
        public byte Ownership;
        public bool IsHome;
        public bool HasMoon;
    }
}

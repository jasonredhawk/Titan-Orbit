using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Pure math helpers for swept bullet segment tests on a toroidal map. Shared by
    /// authoritative <see cref="BulletSimulationSystem"/> and cosmetic tracer update systems.
    /// [BurstCompile] target — no EntityManager access.
    /// </summary>
    public static class BulletCollision
    {
        /// <summary>
        /// Max Euclidean length of one collision substep. Half of
        /// <see cref="GemEconomyConstants.MinAsteroidHitRadius"/> so a fast
        /// <c>shipVel + BulletSpeed</c> step cannot skip the smallest rock in one sample.
        /// </summary>
        public static float MaxAdvanceSubstepLength =>
            math.max(0.05f, GemEconomyConstants.MinAsteroidHitRadius * 0.5f);

        /// <summary>
        /// Hard cap on substeps per tick.
        /// <para>
        /// [TITAN-ORBIT] Cap must cover upgraded hulls: <c>|shipVel| + BulletSpeed</c> at 60 Hz
        /// with attribute/chassis speed can exceed ~1 unit/tick. With MaxAdvanceSubstepLength≈0.075
        /// that needs ~14+ samples. Cap=4 let fast bullets tunnel small rocks — starter ships
        /// (slow bullets) hit reliably; Moon-menu upgraded ships missed more often.
        /// </para>
        /// </summary>
        public const int MaxAdvanceSubsteps = 32;

        /// <summary>
        /// How many equal substeps to split a tick travel into. Always at least 1; at most
        /// <see cref="MaxAdvanceSubsteps"/>.
        /// </summary>
        public static int ComputeAdvanceSubstepCount(float stepDistance)
        {
            float maxStep = MaxAdvanceSubstepLength;
            if (stepDistance <= maxStep || maxStep <= 1e-6f)
                return 1;
            int n = (int)math.ceil(stepDistance / maxStep);
            return math.clamp(n, 1, MaxAdvanceSubsteps);
        }

        /// <summary>
        /// Uncapped substep count (before <see cref="MaxAdvanceSubsteps"/>). Used for tunnel-risk logs.
        /// </summary>
        public static int ComputeUncappedAdvanceSubstepCount(float stepDistance)
        {
            float maxStep = MaxAdvanceSubstepLength;
            if (stepDistance <= maxStep || maxStep <= 1e-6f)
                return 1;
            return math.max(1, (int)math.ceil(stepDistance / maxStep));
        }

        /// <summary>
        /// [TITAN-ORBIT] Logical center repositioned to the map tile nearest unwrapOrigin for toroidal accuracy.
        /// </summary>
        public static float3 UnwrapCenterNear(float3 unwrapOrigin, float3 logicalCenter, float mapW, float mapH)
        {
            // [TITAN-ORBIT] Shortest toroidal offset places obstacle in the same "tile" as the segment.
            float3 center = unwrapOrigin + ToroidalMapEcs.ShortestOffsetXZ(unwrapOrigin, logicalCenter, mapW, mapH);
            center.y = logicalCenter.y;
            return center;
        }

        /// <summary>
        /// [TITAN-ORBIT] Swept segment vs sphere on a torus — unwraps obstacle center near segment start.
        /// </summary>
        public static bool SegmentHitsSphereToroidal(
            float3 from,
            float3 to,
            float3 logicalCenter,
            float radius,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            float3 center = UnwrapCenterNear(from, logicalCenter, mapW, mapH);
            return SegmentHitsSphere(from, to, center, radius, out hitPoint);
        }

        /// <summary>
        /// [STANDARD] Swept segment vs sphere — returns the first contact point along [from, to].
        /// Uses quadratic ray-sphere intersection with t clamped to [0,1].
        /// </summary>
        public static bool SegmentHitsSphere(float3 from, float3 to, float3 center, float radius, out float3 hitPoint)
        {
            hitPoint = to;
            // --- Flatten to XZ plane (top-down shooter) ---
            from.y = center.y;
            to.y = center.y;

            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            // --- Degenerate segment: point-in-sphere test ---
            if (deltaLenSq < 1e-8f)
                return math.distance(from, center) <= radius;

            // --- Quadratic coefficients for ray-sphere ---
            float3 oc = from - center;
            float a = deltaLenSq;
            float b = 2f * math.dot(oc, delta);
            float c = math.lengthsq(oc) - radius * radius;
            float discriminant = b * b - 4f * a * c;
            if (discriminant < 0f)
                return false;

            float sqrtDisc = math.sqrt(discriminant);
            float inv2a = 1f / (2f * a);
            float tEnter = (-b - sqrtDisc) * inv2a;
            float tExit = (-b + sqrtDisc) * inv2a;

            if (tEnter > 1f || tExit < 0f)
                return false;

            float t = math.clamp(tEnter, 0f, 1f);
            if (tEnter < 0f && tExit >= 0f)
            {
                // --- Started inside the sphere ---
                // [TITAN-ORBIT] Multi-cannon wing muzzles often spawn already inside a *side*
                // asteroid in a dense cluster. Counting that as a hit damaged rocks the player
                // was not aiming at (client tracers look forward; server “point hit” killed sides).
                // Only accept an interior start when the bullet is moving toward the rock center
                // (nose-touch / digging into the aimed body). Lateral interior starts are ignored.
                float3 toCenter = center - from;
                toCenter.y = 0f;
                float3 move = delta;
                move.y = 0f;
                if (math.lengthsq(toCenter) > 1e-8f && math.lengthsq(move) > 1e-8f)
                {
                    if (math.dot(math.normalize(move), math.normalize(toCenter)) < 0.25f)
                        return false;
                }

                t = 0f;
            }

            hitPoint = from + delta * t;
            hitPoint.y = center.y;
            return true;
        }

        /// <summary>
        /// Parameter t along [from, to] for a contact point (0 = start, 1 = end).
        /// Used when several obstacles intersect the same segment — nearest t wins.
        /// </summary>
        /// <param name="from">Segment start.</param>
        /// <param name="to">Segment end.</param>
        /// <param name="hitPoint">Contact from <see cref="SegmentHitsSphere"/> (or toroidal wrapper).</param>
        /// <returns>Projection of hit onto the segment as a scalar in roughly [0,1].</returns>
        public static float GetSegmentHitParameter(float3 from, float3 to, float3 hitPoint)
        {
            // --- Point segment: every contact is at the muzzle ---
            float3 delta = to - from;
            float deltaLenSq = math.lengthsq(delta);
            if (deltaLenSq < 1e-8f)
                return 0f;

            // [STANDARD] Scalar projection onto the segment direction.
            return math.dot(hitPoint - from, delta) / deltaLenSq;
        }

        /// <summary>World hit radius for asteroid mesh scale (mining VFX alignment).</summary>
        public static float AsteroidHitRadius(float scale)
        {
            float meshRadius = scale * GemEconomyConstants.AsteroidMeshBaseRadius;
            return math.max(
                GemEconomyConstants.MinAsteroidHitRadius,
                meshRadius * GemEconomyConstants.AsteroidHitRadiusScale);
        }

        /// <summary>Planet body sphere radius from visual scale.</summary>
        public static bool SegmentHitsPlanetToroidal(
            float3 from,
            float3 to,
            float3 logicalPlanetCenter,
            float planetScale,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            float radius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetScale);
            return SegmentHitsSphereToroidal(from, to, logicalPlanetCenter, radius, mapW, mapH, out hitPoint);
        }

        /// <summary>
        /// Gem-moon position orbits the planet — unwrap moon center near segment start for toroidal accuracy.
        /// </summary>
        public static bool SegmentHitsMoonNear(
            float3 from,
            float3 to,
            float3 logicalPlanetCenter,
            float planetScale,
            int planetLevel,
            int planetId,
            double elapsedSeconds,
            bool isHomePlanet,
            float hitRadius,
            float mapW,
            float mapH,
            out float3 hitPoint)
        {
            hitPoint = to;
            float3 moonCenter = PlanetOrbitMath.GetMoonWorldPositionNear(
                from,
                logicalPlanetCenter,
                planetScale,
                planetLevel,
                planetId,
                elapsedSeconds,
                mapW,
                mapH);
            return SegmentHitsSphere(from, to, moonCenter, hitRadius, out hitPoint);
        }
    }
}

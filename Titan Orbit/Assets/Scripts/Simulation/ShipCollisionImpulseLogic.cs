using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared 1D contact-impulse math for ship bounce. Owns normal energy transfer so
    /// mass and closing speed produce the correct rebound — Unity Physics only depenetrates
    /// (material restitution is 0) and raises collision events.
    /// <para>
    /// Used by same-tile <c>ShipCollisionBounceSystem</c> and cross-seam
    /// <c>ShipToroidalWorldCollisionLogic</c> so predicted client and server stay in lockstep.
    /// Tangential grip stays in <c>AsteroidColliderMaterialLogic.ApplyTangentialFriction</c>
    /// (orthogonal to this normal response).
    /// </para>
    /// <para>
    /// [STANDARD] Classical contact impulse along a unit normal with coefficient of restitution
    /// <c>e</c>. [TITAN-ORBIT] Asteroids stay WorldStatic (do not move) but still contribute a
    /// finite virtual mass so light ships rebound harder off heavy rocks.
    /// </para>
    /// </summary>
    public static class ShipCollisionImpulseLogic
    {
        /// <summary>
        /// Default ship↔ship bounce (0 = sticky merge along the normal, 1 = perfectly elastic).
        /// Tuned softer than a billiard ball so rams feel weighty without launching ships.
        /// </summary>
        public const float DefaultShipShipRestitution = 0.4f;

        /// <summary>
        /// Default bounce when hitting an infinite-mass wall (planets / moons).
        /// Matches the old world-static material feel (~0.5).
        /// </summary>
        public const float DefaultInfiniteMassRestitution = 0.5f;

        /// <summary>
        /// Minimum mass used in impulse denominators so tiny / missing values do not explode Δv.
        /// Matches <see cref="ShipMassLogic.MinMass"/>.
        /// </summary>
        public const float MinCollisionMass = ShipMassLogic.MinMass;

        /// <summary>
        /// Virtual asteroid collision mass from designer Size × mass-per-size.
        /// Rocks never move, but this mass still shapes how hard the ship rebounds.
        /// </summary>
        /// <param name="asteroidSize">Designer Size stored on <c>AsteroidState.Size</c>.</param>
        /// <param name="massPerSize">From <c>AsteroidSettings.CollisionMassPerSize</c>.</param>
        /// <returns>Clamped positive mass for the impulse denominator.</returns>
        public static float ComputeAsteroidCollisionMass(float asteroidSize, float massPerSize)
        {
            float size = math.max(0.01f, asteroidSize);
            float per = math.max(0f, massPerSize);
            return math.max(MinCollisionMass, size * per);
        }

        /// <summary>
        /// Classical 1D impulse scalar along the contact normal.
        /// Only closing contacts (relative normal speed &lt; 0) produce a non-zero impulse.
        /// </summary>
        /// <param name="velocityA">Linear velocity of body A before the impulse.</param>
        /// <param name="velocityB">Linear velocity of body B before the impulse.</param>
        /// <param name="normalAFromB">Unit normal pointing from B toward A (separation direction for A).</param>
        /// <param name="massA">Mass of A (≥ <see cref="MinCollisionMass"/>).</param>
        /// <param name="massB">Mass of B (≥ <see cref="MinCollisionMass"/>).</param>
        /// <param name="restitution">Bounce coefficient in [0, 1].</param>
        /// <returns>
        /// Impulse magnitude J applied as <c>+(J/mA)·n</c> on A and <c>−(J/mB)·n</c> on B.
        /// Zero when separating or grazing.
        /// </returns>
        public static float ComputeContactImpulse(
            float3 velocityA,
            float3 velocityB,
            float3 normalAFromB,
            float massA,
            float massB,
            float restitution)
        {
            float3 n = normalAFromB;
            if (math.lengthsq(n) < 1e-8f)
                return 0f;
            n = math.normalize(n);

            float3 vA = velocityA;
            float3 vB = velocityB;

            // Relative velocity of A as seen from B, projected onto the separation normal.
            // Negative ⇒ A is approaching B along n ⇒ we need a separating impulse.
            float vnRel = math.dot(vA - vB, n);
            if (vnRel >= 0f)
                return 0f;

            float mA = math.max(MinCollisionMass, massA);
            float mB = math.max(MinCollisionMass, massB);
            float e = math.saturate(restitution);

            // [STANDARD] J = −(1+e) · vnRel / (1/mA + 1/mB)
            return -(1f + e) * vnRel / (1f / mA + 1f / mB);
        }

        /// <summary>
        /// Applies a two-body contact impulse to both velocities (ship↔ship energy transfer).
        /// The moving ship loses normal speed; the other gains it along the force direction.
        /// </summary>
        /// <param name="velocityA">Body A linear velocity — written on bounce.</param>
        /// <param name="velocityB">Body B linear velocity — written on bounce.</param>
        /// <param name="normalAFromB">Unit normal from B toward A.</param>
        /// <param name="massA">Mass of A.</param>
        /// <param name="massB">Mass of B.</param>
        /// <param name="restitution">Bounce coefficient (typically <see cref="DefaultShipShipRestitution"/>).</param>
        /// <returns>True when a non-zero impulse was applied.</returns>
        public static bool ApplyTwoBodyImpulse(
            ref float3 velocityA,
            ref float3 velocityB,
            float3 normalAFromB,
            float massA,
            float massB,
            float restitution)
        {
            float3 n = normalAFromB;
            if (math.lengthsq(n) < 1e-8f)
                return false;
            n = math.normalize(n);

            float J = ComputeContactImpulse(velocityA, velocityB, n, massA, massB, restitution);
            if (math.abs(J) < 1e-8f)
                return false;

            float mA = math.max(MinCollisionMass, massA);
            float mB = math.max(MinCollisionMass, massB);

            float3 vA = velocityA;
            float3 vB = velocityB;
            vA += (J / mA) * n;
            vB -= (J / mB) * n;
            velocityA = vA;
            velocityB = vB;
            return true;
        }

        /// <summary>
        /// Ship hits a static-but-massive body (asteroid). The body does not move; only the ship
        /// velocity is rewritten. Finite <paramref name="bodyMass"/> still shapes the rebound:
        /// light ship vs heavy rock ≈ full bounce; heavy ship vs pebble ≈ soft kick.
        /// </summary>
        /// <param name="shipVelocity">Ship linear velocity — written on bounce.</param>
        /// <param name="normalShipFromBody">Unit normal from body toward ship.</param>
        /// <param name="shipMass">Ship collision mass (ramming mass).</param>
        /// <param name="bodyMass">Virtual body mass (asteroid Size × mass-per-size).</param>
        /// <param name="restitution">Bounce coefficient from asteroid settings.</param>
        /// <returns>True when a non-zero impulse was applied.</returns>
        public static bool ApplyShipVsStaticMassiveImpulse(
            ref float3 shipVelocity,
            float3 normalShipFromBody,
            float shipMass,
            float bodyMass,
            float restitution)
        {
            // Body velocity is always zero and stays zero (WorldStatic / intentional).
            float3 bodyVel = float3.zero;
            float3 shipVel = shipVelocity;
            if (!ApplyTwoBodyImpulse(
                    ref shipVel,
                    ref bodyVel,
                    normalShipFromBody,
                    shipMass,
                    bodyMass,
                    restitution))
                return false;

            shipVelocity = shipVel;
            return true;
        }

        /// <summary>
        /// Ship hits an infinite-mass wall (planet / moon). Equivalent to the classical reflect
        /// <c>v − n·vn·(1+e)</c> when the wall does not move.
        /// </summary>
        /// <param name="shipVelocity">Ship linear velocity — written on bounce.</param>
        /// <param name="normalShipFromBody">Unit normal from body toward ship.</param>
        /// <param name="restitution">Bounce coefficient (typically <see cref="DefaultInfiniteMassRestitution"/>).</param>
        /// <returns>True when inbound normal speed was reflected.</returns>
        public static bool ApplyInfiniteMassWallImpulse(
            ref float3 shipVelocity,
            float3 normalShipFromBody,
            float restitution)
        {
            float3 n = normalShipFromBody;
            if (math.lengthsq(n) < 1e-8f)
                return false;
            n = math.normalize(n);

            float3 vel = shipVelocity;
            float vn = math.dot(vel, n);
            if (vn >= 0f)
                return false;

            float e = math.saturate(restitution);
            // [STANDARD] Infinite-mass wall: J/m_ship = −(1+e)·vn along n.
            vel -= n * vn * (1f + e);
            shipVelocity = vel;
            return true;
        }
    }
}

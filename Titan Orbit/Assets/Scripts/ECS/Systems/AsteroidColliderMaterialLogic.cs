using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Builds asteroid <see cref="PhysicsCollider"/> blobs with designer friction from
    /// <see cref="AsteroidSettings"/>. Shared by SubScene bake and runtime
    /// <see cref="AsteroidSpawning"/> so client Instantiates and server spawns match.
    /// </summary>
    public static class AsteroidColliderMaterialLogic
    {
        /// <summary>
        /// Bake / fallback PhysX restitution. Always 0 — custom
        /// <c>ShipCollisionImpulseLogic</c> owns bounce so mass ratios are correct.
        /// </summary>
        public const float DefaultRestitution = 0f;

        /// <summary>
        /// Default friction when no <see cref="AsteroidSettings"/> asset is loaded.
        /// Higher than Unity Physics <c>Material.Default</c> (0.5) so rams/grinds do not feel icy
        /// against the ship's low hull friction (0.05).
        /// </summary>
        public const float DefaultFriction = 1.5f;

        /// <summary>
        /// Creates a WorldStatic sphere collider with the given surface friction.
        /// Uses <see cref="Material.CombinePolicy.Maximum"/> so asteroid grip is not killed by the
        /// ship's GeometricMean combine with Friction 0.05.
        /// PhysX restitution stays 0 — bounce is applied after Export by the impulse systems.
        /// </summary>
        /// <param name="friction">Designer friction from <see cref="AsteroidSettings.Friction"/>.</param>
        /// <param name="restitution">Ignored for bounce feel (forced to 0); kept for API compatibility.</param>
        public static BlobAssetReference<Collider> CreateWorldStaticSphere(
            float friction,
            float restitution = DefaultRestitution)
        {
            var material = Material.Default;
            material.Friction = math.max(0f, friction);
            // [TITAN-ORBIT] Always 0 — mass-aware bounce is not PhysX restitution.
            material.Restitution = 0f;
            // [PHYSICS] Maximum — combined friction = max(ship, asteroid) instead of sqrt(0.05×μ).
            material.FrictionCombinePolicy = Material.CombinePolicy.Maximum;
            material.RestitutionCombinePolicy = Material.CombinePolicy.GeometricMean;

            return SphereCollider.Create(
                new SphereGeometry
                {
                    Center = float3.zero,
                    Radius = BodyCollisionMath.AsteroidMeshBaseRadius,
                },
                TitanOrbitPhysicsLayers.WorldStatic,
                material);
        }

        /// <summary>Reads <see cref="AsteroidSettingsCache"/> (or defaults) and builds a collider blob.</summary>
        public static BlobAssetReference<Collider> CreateFromSettingsCache()
        {
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            return CreateWorldStaticSphere(settings.Friction, DefaultRestitution);
        }

        /// <summary>
        /// Applies Coulomb-style tangential damping for ship slide on an asteroid surface.
        /// Call after the normal bounce / PhysX solve so only the slide component is reduced.
        /// </summary>
        /// <param name="linearVelocity">Ship planar linear velocity (Y ignored).</param>
        /// <param name="surfaceNormal">Unit normal from asteroid toward ship (XZ).</param>
        /// <param name="friction">Designer <see cref="AsteroidSettings.Friction"/> (≥ 0).</param>
        /// <param name="dt">Fixed step seconds.</param>
        /// <returns>Velocity with reduced tangential slide; normal component unchanged.</returns>
        public static float3 ApplyTangentialFriction(
            float3 linearVelocity,
            float3 surfaceNormal,
            float friction,
            float dt)
        {
            if (friction <= 0f || dt <= 0f)
                return linearVelocity;

            float3 n = surfaceNormal;
            if (math.lengthsq(n) < 1e-8f)
                return linearVelocity;
            n = math.normalize(n);

            float3 vel = linearVelocity;
            float vn = math.dot(vel, n);
            float3 vt = vel - n * vn;
            // PhysX-like bleed on the slide: higher Friction → sticks faster while grinding.
            float damp = 1f / (1f + friction * 6f * dt);
            vt *= damp;
            return n * vn + vt;
        }
    }
}

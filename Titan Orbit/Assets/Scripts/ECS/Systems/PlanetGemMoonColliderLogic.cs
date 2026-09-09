using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One solid sphere for the moon rock, one solid sphere for the shield.
    /// Same contract as asteroids / planets: PhysX e = 0, gameplay wall bounce.
    /// Friendly ships omit the owner's shield layer in <see cref="TitanOrbitPhysicsLayers.ShipForTeam"/>
    /// and owned shields use a negative <c>GroupIndex</c> so that shell is off for them —
    /// they only hit the rock.
    /// </summary>
    public static class PlanetGemMoonColliderLogic
    {
        /// <summary>Moon rock sphere (world-static filter, like a planet).</summary>
        public static BlobAssetReference<Collider> CreateMoonBodySphere(float radiusLocal)
        {
            return CreateSolidSphere(
                radiusLocal,
                TitanOrbitPhysicsLayers.WorldStatic);
        }

        /// <summary>Shield sphere. Owner layer — friendly ships do not collide with it.</summary>
        public static BlobAssetReference<Collider> CreateMoonShieldSphere(float radiusLocal, TeamId owner)
        {
            return CreateSolidSphere(
                radiusLocal,
                TitanOrbitPhysicsLayers.MoonShieldForOwner(owner));
        }

        static BlobAssetReference<Collider> CreateSolidSphere(float radiusLocal, CollisionFilter filter)
        {
            var material = Material.Default;
            material.Restitution = 0f;
            material.Friction = AsteroidColliderMaterialLogic.DefaultFriction;
            material.FrictionCombinePolicy = Material.CombinePolicy.Maximum;
            material.CollisionResponse = CollisionResponsePolicy.CollideRaiseCollisionEvents;
            return SphereCollider.Create(
                new SphereGeometry { Center = float3.zero, Radius = math.max(0.05f, radiusLocal) },
                filter,
                material);
        }
    }
}

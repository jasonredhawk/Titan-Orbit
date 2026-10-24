using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Non-owning copy of the solid hull blob while <see cref="PhysicsCollider"/> points at
    /// the shared no-collide sphere. Never dispose: the baked blob or
    /// <see cref="ShipRuntimeHullColliderBlob"/> owns the memory.
    /// </summary>
    public struct ShipDeathHeldHullCollider : IComponentData
    {
        public BlobAssetReference<Collider> Value;
    }

    /// <summary>
    /// Swaps a dead ship's Unity Physics hull to the shared no-collide sphere, including
    /// <see cref="LinkedEntityGroup"/> children, and puts the solid blob back on respawn.
    /// <para>
    /// [TITAN-ORBIT] Death hides the hybrid mesh and freezes the motor, but the ghost stays
    /// in the world until respawn. Leaving <see cref="PhysicsCollider"/> on that body blocks
    /// other ships at the explosion point. Same swap asteroids use: do not
    /// <c>RemoveComponent</c> (that rebuilds the physics world).
    /// </para>
    /// [PHYSICS] <see cref="PhysicsStep.SynchronizeCollisionWorld"/> is off, so an in-place
    /// filter edit does not drop an existing pair. Replacing the blob reference does.
    /// </summary>
    public static class ShipDeathPhysics
    {
        /// <summary>
        /// Points every solid collider on <paramref name="ship"/> and its linked children
        /// at <see cref="AsteroidClientCullPhysicsSystem.NoCollideCollider"/>.
        /// </summary>
        public static void SuppressShip(EntityManager em, Entity ship)
        {
            var members = new NativeList<Entity>(8, Allocator.Temp);
            CollectMembers(em, ship, members);
            for (int i = 0; i < members.Length; i++)
                SuppressEntity(em, members[i]);
            members.Dispose();
        }

        /// <summary>
        /// Restores the hull blob saved at death. No-op when the body is already solid.
        /// </summary>
        public static void RestoreShip(EntityManager em, Entity ship)
        {
            var members = new NativeList<Entity>(8, Allocator.Temp);
            CollectMembers(em, ship, members);
            for (int i = 0; i < members.Length; i++)
                RestoreEntity(em, members[i]);
            members.Dispose();
        }

        static void CollectMembers(EntityManager em, Entity root, NativeList<Entity> members)
        {
            members.Add(root);
            if (!em.HasBuffer<LinkedEntityGroup>(root))
                return;

            var group = em.GetBuffer<LinkedEntityGroup>(root);
            for (int i = 0; i < group.Length; i++)
            {
                Entity member = group[i].Value;
                if (member == Entity.Null || member == root || !em.Exists(member))
                    continue;
                if (em.HasComponent<Prefab>(member))
                    continue;
                members.Add(member);
            }
        }

        static void SuppressEntity(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity) || !em.HasComponent<PhysicsCollider>(entity))
                return;

            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            if (!noCollide.IsCreated)
                return;

            var current = em.GetComponentData<PhysicsCollider>(entity);
            if (!current.Value.IsCreated || current.Value == noCollide)
                return;

            // Save the live blob before the swap. Do not write it onto
            // ShipRuntimeHullColliderBlob — that cleanup path disposes its blob,
            // and a baked ghost collider must never be disposed.
            var held = new ShipDeathHeldHullCollider { Value = current.Value };
            if (em.HasComponent<ShipDeathHeldHullCollider>(entity))
                em.SetComponentData(entity, held);
            else
                em.AddComponentData(entity, held);

            em.SetComponentData(entity, new PhysicsCollider { Value = noCollide });
            if (em.HasComponent<PhysicsVelocity>(entity))
                em.SetComponentData(entity, PhysicsVelocity.Zero);
        }

        static void RestoreEntity(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity) || !em.HasComponent<PhysicsCollider>(entity))
                return;

            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            var current = em.GetComponentData<PhysicsCollider>(entity);
            if (current.Value != noCollide)
                return;

            BlobAssetReference<Collider> solid = default;
            if (em.HasComponent<ShipDeathHeldHullCollider>(entity))
                solid = em.GetComponentData<ShipDeathHeldHullCollider>(entity).Value;

            if ((!solid.IsCreated || solid == noCollide) &&
                em.HasComponent<ShipRuntimeHullColliderBlob>(entity))
            {
                solid = em.GetComponentData<ShipRuntimeHullColliderBlob>(entity).Value;
            }

            if (!solid.IsCreated || solid == noCollide)
                return;

            em.SetComponentData(entity, new PhysicsCollider { Value = solid });
            if (em.HasComponent<ShipDeathHeldHullCollider>(entity))
                em.SetComponentData(entity, new ShipDeathHeldHullCollider());
        }
    }

    /// <summary>
    /// Server and client predicted step: dead ships lose their hull collider before
    /// <see cref="ShipPhysicsMassSyncSystem"/> and the solver. Living ships that still
    /// wear the no-collide sphere get the saved hull back (respawn).
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipPhysicsMassSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipDeathColliderSystem : ISystem
    {
        /// <summary>Wait until a ship exists. The no-collide blob is created on first use.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Strips hulls whose <see cref="ShipState.IsDead"/> is set, and restores the rest.
        /// Clients do this locally: <see cref="PhysicsCollider"/> is not a ghost field.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var noCollide = AsteroidClientCullPhysicsSystem.NoCollideCollider;
            if (!noCollide.IsCreated)
                return;

            var strip = new NativeList<Entity>(4, Allocator.Temp);
            var restore = new NativeList<Entity>(4, Allocator.Temp);

            foreach (var (ship, collider, entity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<PhysicsCollider>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                bool suppressed = collider.ValueRO.Value == noCollide;
                if (ship.ValueRO.IsDead)
                {
                    // Transition only. A corpse already on the no-collide sphere stays that way
                    // without a per-tick write (that would dirty the dynamic broadphase).
                    if (!suppressed)
                        strip.Add(entity);
                    continue;
                }

                if (suppressed)
                    restore.Add(entity);
            }

            var em = state.EntityManager;
            for (int i = 0; i < strip.Length; i++)
                ShipDeathPhysics.SuppressShip(em, strip[i]);
            for (int i = 0; i < restore.Length; i++)
                ShipDeathPhysics.RestoreShip(em, restore[i]);

            strip.Dispose();
            restore.Dispose();
        }
    }
}

using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server predicted step: disable 0-HP / IsDestroyed asteroid hulls
    /// <b>before</b> the solver runs (shared no-collide blob). Paired with
    /// <see cref="AsteroidClientCullPhysicsSystem"/> on the client.
    /// <para>
    /// [TITAN-ORBIT] Ram / bullet kill happens after physics this tick. The next tick must not
    /// still collide with a rock the client already hid (HitRpc Health=0). If
    /// <see cref="AsteroidDestructionSystem"/> is late or <c>DestroyEntity</c> leaves a ghost
    /// cleanup zombie, this pass still removes <see cref="PhysicsCollider"/> so the ship can
    /// fly through.
    /// </para>
    /// World: ServerSimulation. Group: PredictedFixedStepSimulationSystemGroup OrderFirst
    /// (before PhysicsSystemGroup).
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct AsteroidServerDeadPhysicsStripSystem : ISystem
    {
        /// <summary>
        /// No RequireForUpdate on a dead-asteroid tag — 0-HP rocks may never have been tagged.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AsteroidTag>();
        }

        /// <summary>
        /// Swaps dead asteroid hulls to the shared no-collide blob.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Dead rocks that still have a hull ---
            // [PHYSICS] Static Unity Physics can keep a sphere after Health=0 if we only set
            // IsDestroyed and wait for DestroyEntity. Strip first so this tick's solver is clean.
            foreach (var (asteroidState, entity) in SystemAPI
                         .Query<RefRO<AsteroidState>>()
                         .WithAll<AsteroidTag, PhysicsCollider>()
                         .WithEntityAccess())
            {
                var a = asteroidState.ValueRO;
                if (!a.IsDestroyed && a.Health > 0.01f)
                    continue;

                AsteroidDeathPhysics.QueueStripAndDisable(ecb, em, entity);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }
    }
}

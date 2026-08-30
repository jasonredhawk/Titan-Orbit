using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Canonical Pac-Man wrap for predicted ships after Unity Physics integrates and bounce
    /// / friction have run. Writes <see cref="LocalTransform.Position"/> into
    /// <c>[-MapWidth/2, MapWidth/2) × [-MapHeight/2, MapHeight/2)</c> so the hull collider
    /// sits on the same chart as planets and asteroids — no display tiles, no sphere-resolve
    /// seam fakes. Velocity is unchanged (the ship keeps flying).
    /// <para>
    /// [TITAN-ORBIT] A wrap that lands inside a solid depenetrates against the real
    /// <see cref="PhysicsCollider"/> via <see cref="CollisionWorld.CalculateDistance"/> —
    /// not a new AABB-sphere game. Client join skips only that query
    /// (<see cref="ClientJoinSettleCache.ShouldSkipShipSimulation"/>); wrap math always runs.
    /// </para>
    /// Pipeline: Drive → Physics → Bounce → Friction → Wrap (this) → Planar → KinematicsSync.
    /// Paired with gem / bullet / transport wrap at their integrate sites.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(ShipPlanarPhysicsConstraintSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCanonicalWrapSystem : ISystem
    {
        /// <summary>Need at least one ship before we resolve map size / physics world.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Wraps each simulated living ship. When the pose actually jumped a map side and
        /// the physics world is safe to query, pushes the hull out of any overlapping
        /// world / ship collider.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // Presentation ship gathers use ShouldSkipShipEntityQueries (Instantiates trickle).
            // This is predicted sim — same short Crash!!! window as planar lock / bounce.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;

            bool canQueryPhysics = SystemAPI.TryGetSingleton(out PhysicsWorldSingleton physicsWorld);
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                canQueryPhysics = false;

            CollisionWorld collisionWorld = default;
            if (canQueryPhysics)
                collisionWorld = physicsWorld.CollisionWorld;

            bool client = state.World.IsClient();
            var predictedLookup = SystemAPI.GetComponentLookup<PredictedGhost>(true);

            foreach (var (transform, physicsCollider, shipState, shipEntity) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<PhysicsCollider>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                if (client && !predictedLookup.HasComponent(shipEntity))
                    continue;
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                float3 before = transform.ValueRO.Position;
                float3 wrapped = ToroidalMapEcs.Wrap(before, mapW, mapH);
                wrapped.y = 0f;
                if (math.distancesq(before, wrapped) < 1e-8f)
                    continue;

                // --- Landed on the opposite edge ---
                var lt = transform.ValueRO;
                lt.Position = wrapped;

                if (canQueryPhysics && physicsCollider.ValueRO.Value.IsCreated)
                {
                    TryDepenetrateWrappedShip(
                        ref lt,
                        physicsCollider.ValueRO,
                        collisionWorld,
                        shipEntity);
                }

                transform.ValueRW = lt;
            }
        }

        /// <summary>
        /// If the wrapped hull overlaps a Unity.Physics body, slide out along the contact
        /// normal by the penetration depth. Uses the ship's collider AABB as the query
        /// radius so compound chassis keep-out matches bake, not a guessed sphere.
        /// </summary>
        static void TryDepenetrateWrappedShip(
            ref LocalTransform transform,
            in PhysicsCollider physicsCollider,
            in CollisionWorld collisionWorld,
            Entity self)
        {
            Aabb aabb = physicsCollider.Value.Value.CalculateAabb(
                new RigidTransform(transform.Rotation, transform.Position));
            float radius = math.max(aabb.Extents.x, aabb.Extents.z) * 0.5f;
            if (radius < 0.05f)
                return;

            var input = new PointDistanceInput
            {
                Position = transform.Position,
                MaxDistance = radius,
                Filter = TitanOrbitPhysicsLayers.Ship,
            };

            if (!collisionWorld.CalculateDistance(input, out DistanceHit hit))
                return;
            if (hit.Entity == self)
                return;

            // Negative distance = still overlapping. Push along the planar normal.
            float clearance = radius - hit.Distance;
            if (clearance <= 0f)
                return;

            float3 normal = hit.SurfaceNormal;
            normal.y = 0f;
            if (math.lengthsq(normal) < 1e-8f)
                normal = new float3(1f, 0f, 0f);
            else
                normal = math.normalize(normal);

            float3 pos = transform.Position + normal * clearance;
            pos.y = 0f;
            transform.Position = pos;
        }
    }
}

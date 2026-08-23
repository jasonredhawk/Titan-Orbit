using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics exports, stops ship↔ship tunneling that discrete contacts miss.
    /// <list type="number">
    /// <item>
    /// Sweeps this ship's hull from the pre-physics snapshot to the exported pose
    /// (continuous collision). If the sweep hits another ship, rewind to the contact
    /// and bounce the remaining closing speed.
    /// </item>
    /// <item>
    /// If hulls are still overlapping (<c>Distance &lt; 0</c> only — not "nearby"), slide
    /// this ship out along the contact normal. No min separating speed, no extra
    /// proximity skin (that felt like magnets).
    /// </item>
    /// </list>
    /// Client: interpolated remotes have no <see cref="PhysicsVelocity"/>
    /// (predicted-only ghost variant); only the predicted local hull is moved.
    /// Server: both simulated hulls may move (each takes half the penetration).
    /// <para>
    /// [TITAN-ORBIT] Predicted sim — skip with
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipSimulation"/>. Presentation ship
    /// gathers use <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>.
    /// </para>
    /// Pipeline: Drive → Snapshot → Physics → Bounce → Friction → SolidContact (this) → Wrap.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipAsteroidContactFrictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipShipSolidContactSystem : ISystem
    {
        const float OverlapEpsilon = 0.001f;
        const float ContactSkin = 0.02f;
        const float SweepMotionEpsilonSq = 1e-6f;
        /// <summary>Ignore Fraction 0 (already overlapping — that is the distance pass).</summary>
        const float SweepMinFraction = 0.01f;

        /// <summary>Need exported physics world + a ship hull to query against.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PhysicsWorldSingleton>();
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Sweeps then overlap-resolves every living simulated ship against other ship hulls.
        /// Safe to write pose / velocity here (post-Export).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] ShouldSkipShipEntityQueries is the presentation gather gate.
            // This is predicted sim: ShouldSkipShipSimulation covers TeamChoice Instantiates.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            if (!SystemAPI.TryGetSingleton(out PhysicsWorldSingleton physicsWorld))
                return;

            CollisionWorld collisionWorld = physicsWorld.CollisionWorld;
            var shipLookup = SystemAPI.GetComponentLookup<ShipTag>(true);
            var shipStateLookup = SystemAPI.GetComponentLookup<ShipState>(true);
            var simulateLookup = SystemAPI.GetComponentLookup<Simulate>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var kinematicsLookup = SystemAPI.GetComponentLookup<ShipKinematics>(true);
            var snapshotLookup = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);

            bool client = state.World.IsClient();
            var predictedLookup = SystemAPI.GetComponentLookup<PredictedGhost>(true);

            foreach (var (transform, physicsCollider, physicsVelocity, shipState, shipEntity) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<PhysicsCollider>, RefRW<PhysicsVelocity>,
                             RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                if (client && !predictedLookup.HasComponent(shipEntity))
                    continue;
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (!physicsCollider.ValueRO.Value.IsCreated)
                    continue;

                var lt = transform.ValueRO;
                float3 vel = physicsVelocity.ValueRO.Linear;
                vel.y = 0f;
                bool wrote = false;

                if (snapshotLookup.HasComponent(shipEntity))
                {
                    wrote |= TrySweepAgainstShips(
                        shipEntity,
                        physicsCollider.ValueRO.Value,
                        snapshotLookup[shipEntity].Position,
                        ref lt,
                        ref vel,
                        collisionWorld,
                        shipLookup,
                        shipStateLookup,
                        kinematicsLookup,
                        velocityLookup);
                }

                wrote |= TryOverlapDepenetrate(
                    shipEntity,
                    physicsCollider.ValueRO.Value,
                    ref lt,
                    ref vel,
                    collisionWorld,
                    shipLookup,
                    shipStateLookup,
                    simulateLookup,
                    kinematicsLookup,
                    velocityLookup);

                if (!wrote)
                    continue;

                lt.Position.y = 0f;
                vel.y = 0f;
                transform.ValueRW = lt;
                physicsVelocity.ValueRW = new PhysicsVelocity
                {
                    Linear = vel,
                    Angular = float3.zero,
                };
            }
        }

        /// <summary>
        /// Collider-cast from the pre-physics pose to the exported pose. Rewind + bounce
        /// only when the first hit is another living ship (world bodies stay PhysX-owned).
        /// </summary>
        static bool TrySweepAgainstShips(
            Entity self,
            BlobAssetReference<Unity.Physics.Collider> collider,
            float3 from,
            ref LocalTransform transform,
            ref float3 velocity,
            in CollisionWorld collisionWorld,
            ComponentLookup<ShipTag> ships,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<PhysicsVelocity> velocities)
        {
            float3 to = transform.Position;
            from.y = 0f;
            to.y = 0f;
            if (math.distancesq(from, to) < SweepMotionEpsilonSq)
                return false;

            var input = new ColliderCastInput(collider, from, to, transform.Rotation);
            var hits = new NativeList<ColliderCastHit>(8, Allocator.Temp);
            bool any = collisionWorld.CastCollider(input, ref hits);
            if (!any)
            {
                hits.Dispose();
                return false;
            }

            float bestFraction = 2f;
            ColliderCastHit best = default;
            for (int i = 0; i < hits.Length; i++)
            {
                ColliderCastHit hit = hits[i];
                if (hit.Entity == self || !ships.HasComponent(hit.Entity))
                    continue;
                if (shipStates.HasComponent(hit.Entity) &&
                    (shipStates[hit.Entity].IsDead || shipStates[hit.Entity].AwaitingTeamSelection))
                    continue;
                if (hit.Fraction < SweepMinFraction || hit.Fraction >= 1f || hit.Fraction >= bestFraction)
                    continue;

                bestFraction = hit.Fraction;
                best = hit;
            }

            hits.Dispose();
            if (bestFraction >= 1f)
                return false;

            float3 n = best.SurfaceNormal;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                n = to - from;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                return false;
            n = math.normalize(n);

            float3 pos = from + (to - from) * math.max(0f, bestFraction);
            pos += n * ContactSkin;
            pos.y = 0f;
            transform.Position = pos;

            BounceOffOther(ref velocity, best.Entity, n, kinematics, velocities);
            return true;
        }

        /// <summary>
        /// Overlap-only (Distance &lt; 0). Nearby-but-not-touching hulls are left alone.
        /// </summary>
        static bool TryOverlapDepenetrate(
            Entity self,
            BlobAssetReference<Unity.Physics.Collider> collider,
            ref LocalTransform transform,
            ref float3 velocity,
            in CollisionWorld collisionWorld,
            ComponentLookup<ShipTag> ships,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<Simulate> simulate,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<PhysicsVelocity> velocities)
        {
            var input = new ColliderDistanceInput(
                collider,
                0f,
                new RigidTransform(transform.Rotation, transform.Position));
            var hits = new NativeList<DistanceHit>(8, Allocator.Temp);
            bool any = collisionWorld.CalculateDistance(input, ref hits);
            if (!any)
            {
                hits.Dispose();
                return false;
            }

            float deepest = 0f;
            DistanceHit best = default;
            for (int i = 0; i < hits.Length; i++)
            {
                DistanceHit hit = hits[i];
                if (hit.Entity == self || !ships.HasComponent(hit.Entity))
                    continue;
                if (shipStates.HasComponent(hit.Entity) &&
                    (shipStates[hit.Entity].IsDead || shipStates[hit.Entity].AwaitingTeamSelection))
                    continue;
                if (hit.Distance >= -OverlapEpsilon)
                    continue;
                if (hit.Distance >= deepest)
                    continue;

                deepest = hit.Distance;
                best = hit;
            }

            hits.Dispose();
            if (deepest >= -OverlapEpsilon)
                return false;

            float3 n = best.SurfaceNormal;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                n = new float3(1f, 0f, 0f);
            else
                n = math.normalize(n);

            bool otherMoves = simulate.HasComponent(best.Entity) &&
                              simulate.IsComponentEnabled(best.Entity);
            float share = otherMoves ? 0.5f : 1f;
            float3 pos = transform.Position;
            pos += n * ((-best.Distance) * share + ContactSkin);
            pos.y = 0f;
            transform.Position = pos;

            BounceOffOther(ref velocity, best.Entity, n, kinematics, velocities);
            return true;
        }

        /// <summary>
        /// Reflect this ship's remaining inbound normal speed off the other hull
        /// (moving-wall: interpolated remotes; two-body-equivalent on the server when
        /// both write their own pass). No forced 6 m/s pop.
        /// </summary>
        static void BounceOffOther(
            ref float3 velocity,
            Entity other,
            float3 normalSelfFromOther,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<PhysicsVelocity> velocities)
        {
            float3 otherVel = float3.zero;
            if (kinematics.HasComponent(other))
                otherVel = kinematics[other].Velocity;
            else if (velocities.HasComponent(other))
                otherVel = velocities[other].Linear;
            otherVel.y = 0f;

            ShipCollisionImpulseLogic.ApplyMovingWallImpulse(
                ref velocity,
                otherVel,
                normalSelfFromOther,
                0.55f);
        }
    }
}

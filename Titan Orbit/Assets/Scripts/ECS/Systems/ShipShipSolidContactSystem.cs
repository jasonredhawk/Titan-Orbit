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
    /// (you ram them).
    /// </item>
    /// <item>
    /// Client: sweeps each interpolated remote's hull along its ghosted
    /// <see cref="ShipKinematics"/> (they ram you). A parked-static solver miss
    /// plus a full overlap snap every interpolation tick is what made incoming
    /// rams look jerky.
    /// </item>
    /// <item>
    /// Overlap-only (<c>Distance &lt; 0</c>) depenetrate. Remotes are moving walls
    /// (impulse only while closing). Server splits penetration when both simulate.
    /// </item>
    /// </list>
    /// Sets <see cref="ShipAsteroidContactState.InContact"/> so local display raw-follows
    /// during the hit (coast vs ram snaps was a second jerk).
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

        struct RemoteHull
        {
            public Entity Entity;
            public float3 Position;
            public quaternion Rotation;
            public float3 Velocity;
            public BlobAssetReference<Unity.Physics.Collider> Collider;
        }

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
            var contactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);

            bool client = state.World.IsClient();
            var predictedLookup = SystemAPI.GetComponentLookup<PredictedGhost>(true);

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            var remotes = new NativeList<RemoteHull>(8, Allocator.Temp);
            if (client)
                CollectInterpolatedRemotes(ref state, ref remotes);

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
                float3 contactNormal = float3.zero;

                if (snapshotLookup.HasComponent(shipEntity))
                {
                    wrote |= TrySweepAgainstShips(
                        shipEntity,
                        physicsCollider.ValueRO.Value,
                        snapshotLookup[shipEntity].Position,
                        ref lt,
                        ref vel,
                        ref contactNormal,
                        collisionWorld,
                        shipLookup,
                        shipStateLookup,
                        kinematicsLookup,
                        velocityLookup);
                }

                if (client && remotes.Length > 0 && !wrote)
                {
                    wrote |= TryIncomingRemoteSweeps(
                        shipEntity,
                        dt,
                        remotes,
                        ref lt,
                        ref vel,
                        ref contactNormal,
                        collisionWorld);
                }

                wrote |= TryOverlapDepenetrate(
                    shipEntity,
                    physicsCollider.ValueRO.Value,
                    dt,
                    ref lt,
                    ref vel,
                    ref contactNormal,
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

                if (contactLookup.HasComponent(shipEntity))
                {
                    contactLookup[shipEntity] = new ShipAsteroidContactState
                    {
                        InContact = 1,
                        OutwardNormal = contactNormal,
                    };
                }
            }

            remotes.Dispose();
        }

        /// <summary>
        /// Interpolated remotes have no <see cref="PhysicsVelocity"/>. Their ghosted
        /// kinematics is the only incoming-ram velocity the local hull can see.
        /// </summary>
        void CollectInterpolatedRemotes(ref SystemState state, ref NativeList<RemoteHull> remotes)
        {
            foreach (var (transform, collider, kinematics, shipState, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<PhysicsCollider>, RefRO<ShipKinematics>,
                             RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<PhysicsVelocity, PredictedGhost, GhostOwnerIsLocal>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (!collider.ValueRO.Value.IsCreated)
                    continue;

                float3 vel = kinematics.ValueRO.Velocity;
                vel.y = 0f;
                remotes.Add(new RemoteHull
                {
                    Entity = entity,
                    Position = transform.ValueRO.Position,
                    Rotation = transform.ValueRO.Rotation,
                    Velocity = vel,
                    Collider = collider.ValueRO.Value,
                });
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
            ref float3 contactNormal,
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

            float3 n = PlanarNormal(best.SurfaceNormal, to - from);
            if (math.lengthsq(n) < 1e-8f)
                return false;

            float3 pos = from + (to - from) * math.max(0f, bestFraction);
            pos += n * ContactSkin;
            pos.y = 0f;
            transform.Position = pos;
            contactNormal = n;

            BounceOffOther(ref velocity, best.Entity, n, kinematics, velocities);
            return true;
        }

        /// <summary>
        /// Remote hull swept along ghosted velocity into the local predicted ship.
        /// Local sweep misses this because the local hull may not have moved.
        /// </summary>
        static bool TryIncomingRemoteSweeps(
            Entity local,
            float dt,
            NativeList<RemoteHull> remotes,
            ref LocalTransform transform,
            ref float3 velocity,
            ref float3 contactNormal,
            in CollisionWorld collisionWorld)
        {
            for (int i = 0; i < remotes.Length; i++)
            {
                RemoteHull remote = remotes[i];
                if (remote.Entity == local)
                    continue;

                float3 to = remote.Position;
                to.y = 0f;
                float3 from = to - remote.Velocity * dt;
                from.y = 0f;
                if (math.distancesq(from, to) < SweepMotionEpsilonSq)
                    continue;

                var input = new ColliderCastInput(remote.Collider, from, to, remote.Rotation);
                var hits = new NativeList<ColliderCastHit>(8, Allocator.Temp);
                bool any = collisionWorld.CastCollider(input, ref hits);
                if (!any)
                {
                    hits.Dispose();
                    continue;
                }

                float bestFraction = 2f;
                ColliderCastHit best = default;
                for (int h = 0; h < hits.Length; h++)
                {
                    ColliderCastHit hit = hits[h];
                    if (hit.Entity != local)
                        continue;
                    if (hit.Fraction < SweepMinFraction || hit.Fraction >= 1f || hit.Fraction >= bestFraction)
                        continue;
                    bestFraction = hit.Fraction;
                    best = hit;
                }

                hits.Dispose();
                if (bestFraction >= 1f)
                    continue;

                // Cast is the remote: surface normal points out of the local hull.
                float3 n = PlanarNormal(best.SurfaceNormal, transform.Position - to);
                if (math.lengthsq(n) < 1e-8f)
                    continue;

                float3 pos = transform.Position;
                pos += n * ContactSkin;
                pos.y = 0f;
                transform.Position = pos;
                contactNormal = n;

                ShipCollisionImpulseLogic.ApplyMovingWallImpulse(
                    ref velocity,
                    remote.Velocity,
                    n,
                    0.55f);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Overlap-only (Distance &lt; 0). Nearby-but-not-touching hulls are left alone.
        /// </summary>
        static bool TryOverlapDepenetrate(
            Entity self,
            BlobAssetReference<Unity.Physics.Collider> collider,
            float dt,
            ref LocalTransform transform,
            ref float3 velocity,
            ref float3 contactNormal,
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

            float3 n = PlanarNormal(best.SurfaceNormal, new float3(1f, 0f, 0f));
            if (math.lengthsq(n) < 1e-8f)
                return false;

            bool otherIsRemote = !velocities.HasComponent(best.Entity);
            bool otherMoves = !otherIsRemote &&
                              simulate.HasComponent(best.Entity) &&
                              simulate.IsComponentEnabled(best.Entity);
            float share = otherMoves ? 0.5f : 1f;

            float3 otherVel = float3.zero;
            if (kinematics.HasComponent(best.Entity))
                otherVel = kinematics[best.Entity].Velocity;
            otherVel.y = 0f;

            float penetration = -best.Distance;
            float push = penetration * share + ContactSkin;
            if (otherIsRemote)
            {
                // Do not teleport the full interpolation step; ride their incoming motion.
                float incoming = math.max(0f, -math.dot(otherVel, n)) * dt;
                push = math.min(push, math.max(incoming + ContactSkin, ContactSkin));
            }

            float3 pos = transform.Position;
            pos += n * push;
            pos.y = 0f;
            transform.Position = pos;
            contactNormal = n;

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

        static float3 PlanarNormal(float3 raw, float3 fallback)
        {
            float3 n = raw;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
            {
                n = fallback;
                n.y = 0f;
            }

            if (math.lengthsq(n) < 1e-8f)
                return float3.zero;
            return math.normalize(n);
        }
    }
}

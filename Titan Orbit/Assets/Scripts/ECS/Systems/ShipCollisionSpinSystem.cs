using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After linear bounce, applies glancing yaw into <see cref="ShipImpactSpinState"/>.
    /// Predicted on server + owner client. Skips MEGA plow, dock/takeoff, and dead hulls.
    /// Does not write <see cref="Unity.Physics.PhysicsVelocity.Angular"/>.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipCollisionBounceSystem))]
    [UpdateBefore(typeof(ShipAsteroidContactFrictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCollisionSpinSystem : ISystem
    {
        NativeHashSet<long> _seenShipPairs;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
            _seenShipPairs = new NativeHashSet<long>(16, Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_seenShipPairs.IsCreated)
                _seenShipPairs.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs) || pairs.Length == 0)
                return;

            var tuning = ShipImpactSpinLogic.FromSettings(ShipImpactSpinSettingsCache.ResolveOrDefault());
            var shipStates = SystemAPI.GetComponentLookup<ShipState>(true);
            var megas = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var moonDock = SystemAPI.GetComponentLookup<ShipMoonDockState>(true);
            var snapshots = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);
            var transforms = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var spins = SystemAPI.GetComponentLookup<ShipImpactSpinState>(false);
            var kinematics = SystemAPI.GetComponentLookup<ShipKinematics>(true);

            if (_seenShipPairs.Capacity < pairs.Length)
            {
                _seenShipPairs.Dispose();
                _seenShipPairs = new NativeHashSet<long>(math.max(16, pairs.Length * 2), Allocator.Persistent);
            }

            _seenShipPairs.Clear();

            for (int i = 0; i < pairs.Length; i++)
            {
                ShipPhysicsContactElement pair = pairs[i];
                if (pair.Kind == ShipPhysicsContactKind.Ship)
                {
                    long key = PackEntityPairKey(pair.Ship, pair.Other);
                    if (!_seenShipPairs.Add(key))
                        continue;
                }

                if (ShouldSkipSpin(pair, shipStates, megas, moonDock))
                    continue;

                ApplySpinToShip(
                    pair.Ship, pair, pair.ContactOffsetShipXZ, pair.NormalShipFromOther,
                    ref spins, snapshots, kinematics, transforms, shipStates, megas, in tuning);

                if (pair.Kind == ShipPhysicsContactKind.Ship)
                {
                    float2 otherLever = ResolveOtherLever(pair, transforms);
                    ApplySpinToShip(
                        pair.Other, pair, otherLever, -pair.NormalShipFromOther,
                        ref spins, snapshots, kinematics, transforms, shipStates, megas, in tuning);
                }
            }
        }

        static bool ShouldSkipSpin(
            in ShipPhysicsContactElement pair,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<ShipMoonDockState> moonDock)
        {
            if (shipStates.HasComponent(pair.Ship) && shipStates[pair.Ship].IsDead)
                return true;
            if (megas.HasComponent(pair.Ship) && megas[pair.Ship].IsMega)
                return true;
            if (moonDock.HasComponent(pair.Ship))
            {
                var dock = moonDock[pair.Ship];
                if (dock.IsTakingOff || dock.IsFullyLanded)
                    return true;
            }

            if (pair.Kind == ShipPhysicsContactKind.Ship)
            {
                if (shipStates.HasComponent(pair.Other) && shipStates[pair.Other].IsDead)
                    return true;
                if (megas.HasComponent(pair.Other) && megas[pair.Other].IsMega)
                    return true;
            }

            return pair.Kind == ShipPhysicsContactKind.Shield;
        }

        static void ApplySpinToShip(
            Entity ship,
            in ShipPhysicsContactElement pair,
            float2 leverXZ,
            float3 normalFromOther,
            ref ComponentLookup<ShipImpactSpinState> spins,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<LocalTransform> transforms,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            in ShipImpactSpinTuning tuning)
        {
            if (!spins.HasComponent(ship) || !shipStates.HasComponent(ship))
                return;
            if (shipStates[ship].IsDead)
                return;
            if (megas.HasComponent(ship) && megas[ship].IsMega)
                return;

            float3 vShip = snapshots.HasComponent(ship) ? snapshots[ship].Linear : float3.zero;
            float3 vOther = float3.zero;
            if (pair.Kind == ShipPhysicsContactKind.Ship)
            {
                if (snapshots.HasComponent(pair.Other))
                    vOther = snapshots[pair.Other].Linear;
                else if (kinematics.HasComponent(pair.Other))
                    vOther = kinematics[pair.Other].Velocity;
            }

            vShip.y = 0f;
            vOther.y = 0f;
            float2 rel = new float2(vShip.x - vOther.x, vShip.z - vOther.z);
            float2 n = new float2(normalFromOther.x, normalFromOther.z);
            float glancing = ShipImpactSpinLogic.GlancingSpeed(rel, n);
            if (!ShipImpactSpinLogic.PassesGlancingThreshold(pair.ClosingSpeed, glancing, in tuning))
                return;

            float radius = transforms.HasComponent(ship)
                ? BodyCollisionMath.GetShipHullRadiusWorld(transforms[ship].Scale)
                : 0.7f;
            float2 lever = leverXZ;
            if (math.lengthsq(lever) < 1e-8f)
                lever = ShipImpactSpinLogic.FallbackLeverXZ(n, rel, radius);

            var ss = shipStates[ship];
            uint seed = ShipImpactSpinLogic.MixSeed(
                (uint)ship.Index,
                (uint)pair.Other.Index,
                math.asuint(math.round(pair.ClosingSpeed * 8f)));
            float delta = ShipImpactSpinLogic.ComputeCollisionYawDelta(
                pair.ClosingSpeed,
                glancing,
                lever,
                n,
                ss.Health,
                ss.MaxHealth,
                seed,
                in tuning);
            if (math.abs(delta) < 0.05f)
                return;

            var spin = spins[ship];
            ShipImpactSpinLogic.AddYawRate(ref spin.YawRateDegPerSec, delta, tuning.MaxYawRateDegPerSec);
            spins[ship] = spin;
        }

        static float2 ResolveOtherLever(in ShipPhysicsContactElement pair, ComponentLookup<LocalTransform> transforms)
        {
            if (!transforms.HasComponent(pair.Ship) || !transforms.HasComponent(pair.Other))
                return -pair.ContactOffsetShipXZ;

            float3 shipPos = transforms[pair.Ship].Position;
            float3 otherPos = transforms[pair.Other].Position;
            float3 contact = shipPos + new float3(pair.ContactOffsetShipXZ.x, 0f, pair.ContactOffsetShipXZ.y);
            return new float2(contact.x - otherPos.x, contact.z - otherPos.z);
        }

        static long PackEntityPairKey(Entity a, Entity b)
        {
            int aIdx = a.Index;
            int aVer = a.Version;
            int bIdx = b.Index;
            int bVer = b.Version;
            if (aIdx > bIdx || (aIdx == bIdx && aVer > bVer))
            {
                (aIdx, bIdx) = (bIdx, aIdx);
                (aVer, bVer) = (bVer, aVer);
            }

            unchecked
            {
                long lo = ((long)aIdx << 32) | (uint)aVer;
                long hi = ((long)bIdx << 32) | (uint)bVer;
                return lo ^ (hi * 397);
            }
        }
    }
}

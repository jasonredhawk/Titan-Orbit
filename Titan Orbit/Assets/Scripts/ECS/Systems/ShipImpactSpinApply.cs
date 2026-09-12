using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Main-thread helpers for wreck enter and leftover-firepower yaw.
    /// Collision yaw is applied in <see cref="ShipCollisionSpinSystem"/> (predicted).
    /// </summary>
    public static class ShipImpactSpinApply
    {
        /// <summary>True when the ghosted wreck window is still open.</summary>
        public static bool IsWrecked(EntityManager em, Entity ship, double elapsed)
        {
            if (ship == Entity.Null || !em.Exists(ship))
                return false;
            if (em.HasComponent<ShipState>(ship))
            {
                var s = em.GetComponentData<ShipState>(ship);
                if (s.IsDead || s.AwaitingTeamSelection)
                    return false;
                if (s.Health <= ShipDamageLogic.DeathThreshold
                    && s.CurrentGems >= ShipImpactSpinLogic.DefaultMinGemSpawnValue)
                    return true;
            }

            if (!em.HasComponent<ShipImpactSpinState>(ship))
                return false;
            return ShipImpactSpinLogic.IsWreckActive(
                em.GetComponentData<ShipImpactSpinState>(ship).WreckExpiresAt, elapsed);
        }

        /// <summary>Dead, team-select, or wrecked — cannot fire, dump, tractor, or scoop.</summary>
        public static bool BlocksActions(EntityManager em, Entity ship, in ShipState shipState, double elapsed)
        {
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return true;
            return IsWrecked(em, ship, elapsed);
        }

        /// <summary>
        /// After <see cref="ShipDamageLogic.ApplyHullAndGemDamage"/>: start wreck, add firepower yaw.
        /// Callers still write Health/Gems/IsDead and spawn <c>GemsToExpel</c>.
        /// </summary>
        public static void AfterDamage(
            EntityManager em,
            Entity ship,
            in ShipDamageLogic.Result result,
            float3 hitPoint,
            float3 shipPos,
            float2 impulseXZ,
            float mapW,
            float mapH,
            double now)
        {
            if (ship == Entity.Null || !em.Exists(ship))
                return;

            if (result.BecameWrecked)
                EnterWreck(em, ship, now);

            float impact = result.LeftoverFirepower;
            if (result.AppliedHullDamage)
                impact = math.max(impact, math.abs(result.HealthDelta));
            if (impact > 0.0001f)
                AddFirepowerYaw(em, ship, hitPoint, shipPos, impulseXZ, impact, mapW, mapH, now);
        }

        /// <summary>Latches wreck until cargo is gone (no timeout death).</summary>
        public static void EnterWreck(EntityManager em, Entity ship, double now)
        {
            EnsureSpin(em, ship);
            var spin = em.GetComponentData<ShipImpactSpinState>(ship);
            // Latch wreck until cargo is gone — do not time out while gems remain.
            if (spin.WreckExpiresAt < (float)now + 1f)
                spin.WreckExpiresAt = (float)now + 1_000_000f;
            em.SetComponentData(ship, spin);
        }

        /// <summary>Adds wreck / killing-blow yaw from leftover firepower about the hit lever.</summary>
        public static void AddFirepowerYaw(
            EntityManager em,
            Entity ship,
            float3 hitPoint,
            float3 shipPos,
            float2 impulseXZ,
            float leftoverFirepower,
            float mapW,
            float mapH,
            double now)
        {
            EnsureSpin(em, ship);
            var spin = em.GetComponentData<ShipImpactSpinState>(ship);
            var tuning = ShipImpactSpinLogic.FromSettings(ShipImpactSpinSettingsCache.ResolveOrDefault());

            float2 lever = ResolveLeverXZ(em, ship, hitPoint, shipPos, mapW, mapH);
            float2 impulse = impulseXZ;
            if (math.lengthsq(impulse) < 1e-8f)
                impulse = lever;

            float health = 0f;
            float maxHealth = 1f;
            if (em.HasComponent<ShipState>(ship))
            {
                var ss = em.GetComponentData<ShipState>(ship);
                health = ss.Health;
                maxHealth = ss.MaxHealth;
            }

            uint seed = ShipImpactSpinLogic.MixSeed(
                (uint)ship.Index,
                math.asuint((float)leftoverFirepower),
                math.asuint((float)(now * 1000.0)));
            float delta = ShipImpactSpinLogic.ComputeDamageYawDelta(
                leftoverFirepower,
                lever,
                impulse,
                health,
                maxHealth,
                seed,
                in tuning);
            ShipImpactSpinLogic.AddYawRate(ref spin.YawRateDegPerSec, delta, tuning.MaxYawRateDegPerSec);
            em.SetComponentData(ship, spin);
        }

        static float2 ResolveLeverXZ(
            EntityManager em,
            Entity ship,
            float3 hitPoint,
            float3 shipPos,
            float mapW,
            float mapH)
        {
            float3 offset;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                offset = ToroidalMapEcs.ShortestOffsetXZ(shipPos, hitPoint, mapW, mapH);
            else
                offset = hitPoint - shipPos;
            float2 lever = new float2(offset.x, offset.z);
            if (math.lengthsq(lever) < 1e-6f)
            {
                float radius = ResolveHullRadius(em, ship);
                lever = new float2(radius, 0f);
            }

            return lever;
        }

        static float ResolveHullRadius(EntityManager em, Entity ship)
        {
            float scale = 1f;
            if (em.HasComponent<LocalTransform>(ship))
                scale = em.GetComponentData<LocalTransform>(ship).Scale;
            if (em.HasComponent<PhysicsCollider>(ship))
            {
                var collider = em.GetComponentData<PhysicsCollider>(ship);
                if (collider.Value.IsCreated)
                {
                    return ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(collider, scale);
                }
            }

            return BodyCollisionMath.GetShipHullRadiusWorld(scale);
        }

        static void EnsureSpin(EntityManager em, Entity ship)
        {
            if (!em.HasComponent<ShipImpactSpinState>(ship))
                em.AddComponentData(ship, new ShipImpactSpinState());
        }
    }
}

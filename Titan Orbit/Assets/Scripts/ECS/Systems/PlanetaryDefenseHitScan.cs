using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One planetary-defense hit sphere for the current server tick.
    /// Built from planet pose + ghosted slot buffer — no turret ghosts.
    /// </summary>
    public struct PlanetaryDefenseHitTarget
    {
        /// <summary>Planet entity that owns the slot buffer.</summary>
        public Entity PlanetEntity;

        /// <summary>Slot index in <see cref="PlanetaryDefenseSlotElement"/>.</summary>
        public int SlotIndex;

        /// <summary>Planar world center on FixedY.</summary>
        public float3 Position;

        /// <summary>Planet ownership team — friendly bullets pass through.</summary>
        public byte Team;

        /// <summary>Hit-sphere radius (world units) from config level stats.</summary>
        public float HitRadius;
    }

    /// <summary>
    /// Builds deterministic planetary-defense hit spheres each server tick for
    /// <see cref="BulletSimulationSystem"/> nearest-hit scans.
    /// </summary>
    public static class PlanetaryDefenseHitScan
    {
        /// <summary>
        /// Clears and fills <paramref name="targetsOut"/> with every active turret this tick.
        /// </summary>
        public static void RebuildTargets(
            EntityManager em,
            NativeArray<Entity> planets,
            float mapW,
            float mapH,
            PlanetShipFamilyConfig familyConfig,
            PlanetaryDefenseConfig defaultConfig,
            List<PlanetaryDefenseHitTarget> targetsOut)
        {
            targetsOut.Clear();
            if (!planets.IsCreated || planets.Length == 0)
                return;

            for (int p = 0; p < planets.Length; p++)
            {
                Entity planetEntity = planets[p];
                if (!em.HasComponent<PlanetState>(planetEntity) ||
                    !em.HasComponent<LocalTransform>(planetEntity) ||
                    !em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                var planet = em.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    familyConfig, planet.ShipFamilyConfigIndex);
                var xf = em.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = xf.Position;
                float planetSize = math.max(0.25f, xf.Scale);
                int slotCount = buffer.Length;
                byte team = (byte)planet.Ownership;

                for (int i = 0; i < slotCount; i++)
                {
                    var slot = buffer[i];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    var stats = config.GetLevelStats(slot.TurretLevel);
                    float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetPos, planetSize, planet.PlanetLevel, i, slotCount);
                    slotPos.y = PlanetaryDefenseMath.FixedY;

                    targetsOut.Add(new PlanetaryDefenseHitTarget
                    {
                        PlanetEntity = planetEntity,
                        SlotIndex = i,
                        Position = slotPos,
                        Team = team,
                        HitRadius = math.max(0.15f, stats.hitRadius),
                    });
                }
            }

            _ = mapW;
            _ = mapH;
        }

        /// <summary>
        /// Keeps the nearest turret hit along the segment when closer than the current best.
        /// Friendly / same-team bullets pass through.
        /// </summary>
        public static bool TryKeepNearestTurretHit(
            in BulletElement b,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            List<PlanetaryDefenseHitTarget> targets,
            ref float bestT,
            ref float3 bestHit,
            out int targetIndex)
        {
            targetIndex = -1;
            if (targets == null || targets.Count == 0)
                return false;

            bool found = false;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Team == b.OwnerTeam)
                    continue; // Friendly fire off.

                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, t.Position, t.HitRadius, mapW, mapH, out float3 hit))
                    continue;

                float tt = BulletCollision.GetSegmentHitParameter(from, to, hit);
                if (tt > bestT)
                    continue;

                bestT = tt;
                bestHit = hit;
                targetIndex = i;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Applies bullet damage to a turret slot. Resets the slot to empty when HP hits 0.
        /// </summary>
        public static void ApplyDamage(
            EntityManager em,
            Entity planetEntity,
            int slotIndex,
            float damage)
        {
            if (!em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                return;

            var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
            if (slotIndex < 0 || slotIndex >= buffer.Length)
                return;

            var slot = buffer[slotIndex];
            if (slot.TurretLevel == 0)
                return;

            slot.Health -= math.max(0f, damage);
            if (slot.Health <= 0f)
            {
                // [TITAN-ORBIT] Destroyed → empty placeholder (rebuild from gems).
                buffer[slotIndex] = PlanetaryDefenseLogic.CreateEmptySlot((byte)slotIndex);
                return;
            }

            buffer[slotIndex] = slot;
        }
    }
}

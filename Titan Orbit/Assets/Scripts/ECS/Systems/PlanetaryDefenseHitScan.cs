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
    /// <para>
    /// [TITAN-ORBIT] Hit radius must cover the hybrid turret mesh (pad-sized gun), not just the
    /// tiny authored <c>hitRadius</c> — otherwise most ship shots look like hits but die on the
    /// planet body behind the pad (nearest-t planet win after a graze miss).
    /// </para>
    /// </summary>
    public static class PlanetaryDefenseHitScan
    {
        /// <summary>
        /// Floor on turret hit-sphere radius (world). Authored level <c>hitRadius</c> alone was
        /// ~0.4 while the visible turret spans closer to a full pad — unusable for ship fire.
        /// </summary>
        public const float MinTurretHitRadiusWorld = 0.75f;

        /// <summary>Extra radius as a fraction of authored level hitRadius (forgiveness).</summary>
        public const float HitRadiusForgivenessMul = 1.75f;

        /// <summary>
        /// Collision pad from bullet <see cref="BulletElement.ScaleMultiplier"/> (same idea as
        /// ship hull pads in <see cref="BulletSimulationSystem"/>).
        /// </summary>
        public const float BulletScaleHitPad = 0.22f;

        /// <summary>Hard cap so huge tracers do not become planet-wide magnets.</summary>
        public const float MaxBulletScaleHitPad = 0.9f;

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

                // --- One sphere per live gun ---
                // [TITAN-ORBIT] Pose is derived (planet center + even-ring angle). No turret ghosts.
                // Radius is shared with client BulletCosmeticHitQuery so tracers stop on the same pad.
                for (int i = 0; i < slotCount; i++)
                {
                    var slot = buffer[i];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetPos, planetSize, planet.PlanetLevel, i, slotCount);
                    slotPos.y = PlanetaryDefenseMath.FixedY;

                    targetsOut.Add(new PlanetaryDefenseHitTarget
                    {
                        PlanetEntity = planetEntity,
                        SlotIndex = i,
                        Position = slotPos,
                        Team = team,
                        HitRadius = ComputeTurretHitRadius(config, slot.TurretLevel),
                    });
                }
            }

            _ = mapW;
            _ = mapH;
            _ = defaultConfig;
        }

        /// <summary>
        /// Keeps the nearest turret hit along the segment when closer than the current best.
        /// Friendly / same-team bullets pass through. Expands radius by bullet scale so heavy
        /// tracers connect like they do against ship hulls.
        /// </summary>
        /// <param name="b">Flying bullet (team, scale, filter already applied by caller).</param>
        /// <param name="from">Segment start this substep.</param>
        /// <param name="to">Segment end this substep.</param>
        /// <param name="mapW">Toroidal map width from MapStateSingleton.</param>
        /// <param name="mapH">Toroidal map height from MapStateSingleton.</param>
        /// <param name="targets">This tick's pad list from <see cref="RebuildTargets"/>.</param>
        /// <param name="bestT">Nearest t so far (updated on a closer turret).</param>
        /// <param name="bestHit">Contact point for that t.</param>
        /// <param name="targetIndex">Index into <paramref name="targets"/>, or -1.</param>
        /// <returns>True when at least one hostile turret intersected the segment.</returns>
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
                // [TITAN-ORBIT] Friendly fire off — same-team pads must not eat your own bolts.
                if (t.Team == b.OwnerTeam)
                    continue;

                // Heavy tracers get a small extra pad (same idea as ship hull hits).
                float radius = ExpandRadiusForBulletScale(t.HitRadius, b.ScaleMultiplier);
                if (!BulletCollision.SegmentHitsSphereToroidal(
                        from, to, t.Position, radius, mapW, mapH, out float3 hit))
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
        /// Applies bullet damage to a turret slot and stamps last-damage time for regen delay.
        /// Resets the slot to empty when HP hits 0.
        /// </summary>
        /// <param name="serverElapsed">
        /// Server <c>World.ElapsedTime</c> — same clock as ship <see cref="ShipVitalsState.LastHullDamageTime"/>.
        /// </param>
        public static void ApplyDamage(
            EntityManager em,
            Entity planetEntity,
            int slotIndex,
            float damage,
            double serverElapsed)
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
                // Clear regen clock so a rebuilt turret does not inherit the old stamp.
                if (em.HasBuffer<PlanetaryDefenseSlotRegenElement>(planetEntity))
                {
                    var regen = em.GetBuffer<PlanetaryDefenseSlotRegenElement>(planetEntity);
                    PlanetaryDefenseLogic.StampLastDamage(regen, slotIndex, 0.0);
                }

                return;
            }

            buffer[slotIndex] = slot;

            // Server-only regen buffer — stamp out-of-combat delay (not on the ghosted slot element).
            var regenBuf = PlanetaryDefenseLogic.EnsureRegenBuffer(
                em, planetEntity, buffer.Length, wipeExisting: false);
            PlanetaryDefenseLogic.StampLastDamage(regenBuf, slotIndex, serverElapsed);
        }

        /// <summary>
        /// World hit-sphere radius for one turret level. Shared by server combat and client
        /// cosmetic tracers so both stop on the same pad-sized gun.
        /// </summary>
        /// <param name="config">Family / default turret recipe (pad radius + per-level hitRadius).</param>
        /// <param name="turretLevel">Active turret level (1–7). Empty slots are skipped by callers.</param>
        /// <returns>Radius in world units, never below <see cref="MinTurretHitRadiusWorld"/>.</returns>
        public static float ComputeTurretHitRadius(PlanetaryDefenseConfig config, int turretLevel)
        {
            // Fallback when a family left the recipe empty — still a usable pad-sized sphere.
            if (config == null)
                return MinTurretHitRadiusWorld;

            // Soft deposit pad — the visible gun sits on this disc; the sphere must cover it.
            float padRadius = math.clamp(config.depositZoneRadius, 0.8f, 2.5f);
            var stats = config.GetLevelStats(turretLevel);
            return math.max(
                MinTurretHitRadiusWorld,
                math.max(stats.hitRadius * HitRadiusForgivenessMul, padRadius * 0.55f));
        }

        /// <summary>
        /// Extra radius from the flying tracer's visual scale so heavy bolts connect like
        /// they do against ship hulls. Caps so huge cosmetics do not become planet-wide magnets.
        /// </summary>
        /// <param name="hitRadius">Base turret sphere from <see cref="ComputeTurretHitRadius"/>.</param>
        /// <param name="scaleMultiplier">Bullet <c>ScaleMultiplier</c> (1 = default tracer).</param>
        /// <returns>Hit radius plus a clamped scale pad.</returns>
        public static float ExpandRadiusForBulletScale(float hitRadius, float scaleMultiplier)
        {
            float bulletPad = math.clamp(scaleMultiplier * BulletScaleHitPad, 0f, MaxBulletScaleHitPad);
            return hitRadius + bulletPad;
        }

        /// <summary>
        /// True when a same-planet turret hit should beat a slightly nearer planet-body chord.
        /// Stops “I shot the pad but the bullet died on the planet” when both spheres overlap
        /// on the segment within a small t window.
        /// </summary>
        /// <param name="defenseT">Turret contact t along the segment (0 = start, 1 = end).</param>
        /// <param name="planetBodyT">Planet-body contact t that currently leads.</param>
        /// <returns>True when the turret should win despite a slightly nearer planet chord.</returns>
        public static bool PreferDefenseOverPlanetBody(float defenseT, float planetBodyT)
        {
            // Defense must still be a real hit on this segment.
            if (defenseT < 0f || defenseT > 1.0001f)
                return false;
            // Planet was nearer — allow defense to steal if it is not far behind (≤ 18% of segment).
            return defenseT <= planetBodyT + 0.18f;
        }
    }
}

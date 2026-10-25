using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Applies one cannon-laser damage slice to a locked ship, hostile pad, moon shield,
    /// or asteroid. Hull/gem rules match <see cref="BulletSimulationSystem"/> hits.
    /// Gems smaller than <see cref="GemEconomyConstants.MinGemSpawnValue"/> are carried
    /// until a later slice can spawn — continuous DPS must not drop cargo.
    /// </summary>
    public static class CannonLaserHitApply
    {
        /// <summary>Outcome of one slice (for last-damager / gem carry).</summary>
        public struct Result
        {
            public float GemsToExpel;
            public bool AppliedHullDamage;
            public float3 HitPoint;
        }

        /// <summary>Whole-entity lock (ship or asteroid). Not a pad or moon.</summary>
        public const int FocusWholeEntity = 0;

        /// <summary>Planet lock whose burn subject is the moon shield, not a pad.</summary>
        public const int FocusMoon = -1;

        /// <summary>
        /// Which hostile pad or moon a planet lock is burning.
        /// <see cref="FocusWholeEntity"/> when this is not a planet sub-target.
        /// Pad slots are <c>slotIndex + 1</c>. <see cref="FocusMoon"/> is the shield.
        /// Nearest living subject to <paramref name="aim"/> wins, so a new turret
        /// on the same planet is a new lock even though the planet entity did not change.
        /// </summary>
        public static int ResolvePlanetFocus(
            EntityManager em,
            Entity planet,
            TeamId attackerTeam,
            float3 aim,
            float mapW,
            float mapH,
            double moonElapsed)
        {
            if (planet == Entity.Null || !em.Exists(planet)
                || !em.HasComponent<PlanetState>(planet)
                || !em.HasComponent<LocalTransform>(planet))
                return FocusWholeEntity;

            var planetState = em.GetComponentData<PlanetState>(planet);
            var planetXf = em.GetComponentData<LocalTransform>(planet);
            if (planetState.Ownership == attackerTeam || planetState.Ownership == TeamId.None)
                return FocusWholeEntity;

            float best = float.MaxValue;
            int focus = FocusWholeEntity;
            if (em.HasBuffer<PlanetaryDefenseSlotElement>(planet))
            {
                var slots = em.GetBuffer<PlanetaryDefenseSlotElement>(planet);
                int slotCount = slots.Length;
                for (int s = 0; s < slotCount; s++)
                {
                    var slot = slots[s];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    float3 pad = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetXf.Position,
                        math.max(0.25f, planetXf.Scale),
                        planetState.PlanetLevel,
                        s,
                        slotCount);
                    float d = ToroidalMapEcs.ToroidalDistance(aim, pad, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    focus = s + 1;
                }
            }

            if (em.HasComponent<PlanetGemMoonState>(planet))
            {
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                if (PlanetGemMoonCombatLogic.TryGetNonFriendlyMoonAim(
                        planetState.Ownership,
                        attackerTeam,
                        planetXf.Position,
                        planetXf.Scale,
                        planetState.PlanetLevel,
                        planetState.PlanetId,
                        planetState.IsHomePlanet,
                        moon.CurrentShield,
                        aim,
                        mapW,
                        mapH,
                        moonElapsed,
                        out float3 moonAim,
                        out _))
                {
                    float moonDist = ToroidalMapEcs.ToroidalDistance(aim, moonAim, mapW, mapH);
                    if (moonDist < best)
                        focus = FocusMoon;
                }
            }

            return focus;
        }

        /// <summary>
        /// Damages <paramref name="target"/> for <paramref name="damage"/> this tick.
        /// Planet locks re-pick the closest hostile pad or moon from the muzzle.
        /// </summary>
        public static Result Apply(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity target,
            TeamId attackerTeam,
            int attackerNetworkId,
            float3 muzzle,
            float damage,
            bool healFriendly,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            Entity gemPrefab,
            float gemSpawnServerTime,
            ref float gemCarry,
            Entity attackerEntity = default,
            int bankIndex = 0,
            int firePowerExtraLevels = 0,
            float moonGemBatchDamage = 0f)
        {
            var result = new Result { HitPoint = muzzle };
            if (damage <= 0.0001f || target == Entity.Null || !em.Exists(target))
                return result;

            if (em.HasComponent<ShipState>(target) && em.HasComponent<LocalTransform>(target))
            {
                ApplyShip(
                    em, ecb, target, attackerTeam, attackerNetworkId, muzzle, damage, healFriendly,
                    mapW, mapH, serverElapsed, gemPrefab, gemSpawnServerTime, ref gemCarry, ref result,
                    attackerEntity, bankIndex, firePowerExtraLevels);
                return result;
            }

            if (healFriendly)
                return result;

            if (em.HasComponent<PlanetState>(target) && em.HasComponent<LocalTransform>(target))
            {
                ApplyPlanet(
                    em, ecb, target, attackerTeam, attackerNetworkId, muzzle, damage, range, mapW, mapH,
                    moonElapsed, serverElapsed, gemPrefab, gemSpawnServerTime, ref result,
                    bankIndex, firePowerExtraLevels, moonGemBatchDamage);
                return result;
            }

            if (em.HasComponent<AsteroidState>(target) && em.HasComponent<LocalTransform>(target))
            {
                ApplyAsteroid(
                    em, ecb, target, attackerTeam, attackerNetworkId, muzzle, mapW, mapH,
                    ScaleFor(bankIndex, BulletBankDamageTarget.Asteroid, damage, firePowerExtraLevels),
                    ref result);
                return result;
            }

            return result;
        }

        /// <summary>
        /// Bullet-type bonus for this victim. Flat fire power is already in
        /// <paramref name="damage"/>. Heal slices must not call this — a vs-ship
        /// row would turn a heal into a bigger heal.
        /// </summary>
        static float ScaleFor(int bankIndex, BulletBankDamageTarget target, float damage, int extras)
        {
            if (damage <= 0.0001f)
                return damage;
            if (!BulletBankCombatLogic.TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return damage;
            return BulletBankCombatLogic.ResolveHitDamage(profile, target, damage, extras);
        }

        static void ApplyShip(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity target,
            TeamId attackerTeam,
            int attackerNetworkId,
            float3 muzzle,
            float damage,
            bool healFriendly,
            float mapW,
            float mapH,
            double serverElapsed,
            Entity gemPrefab,
            float gemSpawnServerTime,
            ref float gemCarry,
            ref Result result,
            Entity attackerEntity,
            int bankIndex,
            int firePowerExtraLevels)
        {
            var ship = em.GetComponentData<ShipState>(target);
            if (ship.IsDead)
                return;

            var xf = em.GetComponentData<LocalTransform>(target);
            result.HitPoint = CannonLaserSurface.TryGetHitPoint(
                    em, target, muzzle, mapW, mapH, 0.0, out float3 shipSurface)
                ? shipSurface
                : MegaShipCombatAim.GetAimPoint(em, target, xf);

            if (healFriendly && ship.Team == attackerTeam)
            {
                ship.Health = math.min(ship.MaxHealth, ship.Health + damage);
                em.SetComponentData(target, ship);
                return;
            }

            bool moonImmune = false;
            if (em.HasComponent<ShipMoonDockState>(target))
            {
                var moonDock = em.GetComponentData<ShipMoonDockState>(target);
                moonImmune = moonDock.MoonPlanetId != 0 &&
                             moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
            }

            // Hostile hulls pick up the vs-ship row. Allies above already healed
            // with the unscaled slice.
            float hullDamage = ScaleFor(
                bankIndex, BulletBankDamageTarget.ShipOrDrone, damage, firePowerExtraLevels);
            float health = ship.Health;
            float gems = ship.CurrentGems;
            bool isDead = ship.IsDead;
            var applied = ShipDamageLogic.ApplyHullAndGemDamage(
                ref health,
                ref gems,
                ref isDead,
                CardEffectQuery.ScaleIncomingDamage(em, target, hullDamage),
                ship.Team,
                attackerTeam,
                gemExpulsionPerHullDamage: ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage,
                isImmune: moonImmune);

            ship.Health = health;
            ship.CurrentGems = gems;
            ship.IsDead = isDead;
            em.SetComponentData(target, ship);

            result.AppliedHullDamage = applied.AppliedHullDamage;
            result.GemsToExpel = applied.GemsToExpel;

            if (applied.AppliedHullDamage && em.HasComponent<ShipVitalsState>(target))
            {
                var vitals = em.GetComponentData<ShipVitalsState>(target);
                vitals.LastHullDamageTime = serverElapsed;
                em.SetComponentData(target, vitals);
            }

            if (applied.AppliedHullDamage || applied.GemsToExpel > 0.0001f || applied.BecameDead)
            {
                ShipMatchStatsLogic.SetLastDamager(
                    em, target, attackerNetworkId, (float)serverElapsed,
                    sourceEntity: attackerEntity,
                    sourceKind: (byte)DeathVfxSourceKind.Ship);
            }

            gemCarry += applied.GemsToExpel;
            if (gemCarry >= GemEconomyConstants.MinGemSpawnValue && gemPrefab != Entity.Null)
            {
                int sourceNetworkId = 0;
                if (em.HasComponent<GhostOwner>(target))
                    sourceNetworkId = em.GetComponentData<GhostOwner>(target).NetworkId;

                ShipGemExpulsion.SpawnFromDamage(
                    ecb,
                    gemPrefab,
                    xf.Position,
                    gemCarry,
                    intensity: 0.5f,
                    salt: (uint)(target.Index * 19349663) ^ (uint)(serverElapsed * 1000.0),
                    gemSpawnServerTime,
                    sourceNetworkId);
                gemCarry = 0f;
            }
        }

        static void ApplyPlanet(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity planet,
            TeamId attackerTeam,
            int attackerNetworkId,
            float3 muzzle,
            float damage,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            Entity gemPrefab,
            float gemSpawnServerTime,
            ref Result result,
            int bankIndex,
            int firePowerExtraLevels,
            float moonGemBatchDamage)
        {
            var planetState = em.GetComponentData<PlanetState>(planet);
            var planetXf = em.GetComponentData<LocalTransform>(planet);
            if (planetState.Ownership == attackerTeam || planetState.Ownership == TeamId.None)
                return;

            float best = range;
            int bestPad = -1;
            bool moonWins = false;
            float3 bestPos = planetXf.Position;

            if (planetState.Ownership != TeamId.None
                && em.HasBuffer<PlanetaryDefenseSlotElement>(planet))
            {
                var slots = em.GetBuffer<PlanetaryDefenseSlotElement>(planet);
                int slotCount = slots.Length;
                for (int s = 0; s < slotCount; s++)
                {
                    var slot = slots[s];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    float3 pad = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetXf.Position,
                        math.max(0.25f, planetXf.Scale),
                        planetState.PlanetLevel,
                        s,
                        slotCount);
                    float d = ToroidalMapEcs.ToroidalDistance(muzzle, pad, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    bestPad = s;
                    bestPos = pad;
                    moonWins = false;
                }
            }

            if (em.HasComponent<PlanetGemMoonState>(planet))
            {
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                if (PlanetGemMoonCombatLogic.TryGetNonFriendlyMoonAim(
                        planetState.Ownership,
                        attackerTeam,
                        planetXf.Position,
                        planetXf.Scale,
                        planetState.PlanetLevel,
                        planetState.PlanetId,
                        planetState.IsHomePlanet,
                        moon.CurrentShield,
                        muzzle,
                        mapW,
                        mapH,
                        moonElapsed,
                        out float3 moonAim,
                        out _))
                {
                    float moonDist = ToroidalMapEcs.ToroidalDistance(muzzle, moonAim, mapW, mapH);
                    if (moonDist < best)
                    {
                        best = moonDist;
                        bestPos = moonAim;
                        moonWins = true;
                        bestPad = -1;
                    }
                }
            }

            if (!moonWins && bestPad < 0)
                return;

            if (moonWins)
            {
                result.HitPoint = bestPos;
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                float moonDamage = ScaleFor(
                    bankIndex, BulletBankDamageTarget.GemMoon, damage, firePowerExtraLevels);
                // One gem per authored shot (fire power), not one tiny gem per tick.
                float gemPacket = ScaleFor(
                    bankIndex, BulletBankDamageTarget.GemMoon, moonGemBatchDamage, firePowerExtraLevels);
                PlanetGemMoonCombatLogic.ApplyBulletDamage(
                    ref moon,
                    moonDamage,
                    attackerTeam,
                    planetState.Ownership,
                    serverElapsed,
                    ecb,
                    gemPrefab,
                    bestPos,
                    (uint)planet.Index ^ (uint)attackerNetworkId ^ (uint)(serverElapsed * 1000.0),
                    gemSpawnServerTime,
                    gemPacket);
                em.SetComponentData(planet, moon);
                return;
            }

            result.HitPoint = CannonLaserSurface.TryGetHitPoint(
                    em, planet, muzzle, mapW, mapH, moonElapsed, out float3 planetSurface)
                ? planetSurface
                : bestPos;

            if (bestPad >= 0)
            {
                // Pads share the ship row (same bucket as a bullet's planetary-defense hit).
                float padDamage = ScaleFor(
                    bankIndex, BulletBankDamageTarget.ShipOrDrone, damage, firePowerExtraLevels);
                PlanetaryDefenseHitScan.ApplyDamage(em, planet, bestPad, padDamage, serverElapsed);
            }
        }

        static void ApplyAsteroid(
            EntityManager em,
            EntityCommandBuffer ecb,
            Entity asteroid,
            TeamId attackerTeam,
            int attackerNetworkId,
            float3 muzzle,
            float mapW,
            float mapH,
            float damage,
            ref Result result)
        {
            var rock = em.GetComponentData<AsteroidState>(asteroid);
            if (rock.IsDestroyed || rock.Health <= 0.01f)
                return;

            var xf = em.GetComponentData<LocalTransform>(asteroid);
            result.HitPoint = CannonLaserSurface.TryGetHitPoint(
                    em, asteroid, muzzle, mapW, mapH, 0.0, out float3 rockSurface)
                ? rockSurface
                : xf.Position;
            float healthBefore = rock.Health;
            rock.Health -= damage;
            rock.LastInteractTeam = attackerTeam;
            rock.LastInteractNetworkId = attackerNetworkId;
            if (rock.Health <= 0f)
            {
                rock.Health = 0f;
                rock.IsDestroyed = true;
            }

            em.SetComponentData(asteroid, rock);
            AsteroidDamageCredit.Record(em, asteroid, attackerNetworkId, healthBefore - rock.Health);
            if (rock.IsDestroyed || rock.Health <= 0.01f)
                AsteroidDeathPhysics.QueueStripColliders(ecb, em, asteroid);
        }
    }
}

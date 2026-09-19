using TitanOrbit.Core;
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
            Entity attackerEntity = default)
        {
            var result = new Result { HitPoint = muzzle };
            if (damage <= 0.0001f || target == Entity.Null || !em.Exists(target))
                return result;

            if (em.HasComponent<ShipState>(target) && em.HasComponent<LocalTransform>(target))
            {
                ApplyShip(
                    em, ecb, target, attackerTeam, attackerNetworkId, muzzle, damage, healFriendly,
                    mapW, mapH, serverElapsed, gemPrefab, gemSpawnServerTime, ref gemCarry, ref result,
                    attackerEntity);
                return result;
            }

            if (healFriendly)
                return result;

            if (em.HasComponent<PlanetState>(target) && em.HasComponent<LocalTransform>(target))
            {
                ApplyPlanet(
                    em, target, attackerTeam, muzzle, damage, range, mapW, mapH,
                    moonElapsed, serverElapsed, ref result);
                return result;
            }

            if (em.HasComponent<AsteroidState>(target) && em.HasComponent<LocalTransform>(target))
            {
                ApplyAsteroid(em, ecb, target, attackerTeam, attackerNetworkId, muzzle, mapW, mapH, damage, ref result);
                return result;
            }

            return result;
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
            Entity attackerEntity)
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

            float health = ship.Health;
            float gems = ship.CurrentGems;
            bool isDead = ship.IsDead;
            var applied = ShipDamageLogic.ApplyHullAndGemDamage(
                ref health,
                ref gems,
                ref isDead,
                CardEffectQuery.ScaleIncomingDamage(em, target, damage),
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
            Entity planet,
            TeamId attackerTeam,
            float3 muzzle,
            float damage,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            ref Result result)
        {
            var planetState = em.GetComponentData<PlanetState>(planet);
            var planetXf = em.GetComponentData<LocalTransform>(planet);
            if (planetState.Ownership == TeamId.None || planetState.Ownership == attackerTeam)
                return;

            float best = range;
            int bestPad = -1;
            bool moonWins = false;
            float3 bestPos = planetXf.Position;

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
                    float d = ToroidalMapEcs.ToroidalDistance(muzzle, pad, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    bestPad = s;
                    bestPos = pad;
                    moonWins = false;
                }
            }

            if (em.HasComponent<PlanetGemMoonState>(planet)
                && !PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(planetState.Ownership, attackerTeam))
            {
                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetXf.Position,
                    math.max(0.25f, planetXf.Scale),
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    moonElapsed,
                    planetState.IsHomePlanet);
                float moonDist = ToroidalMapEcs.ToroidalDistance(muzzle, moonPos, mapW, mapH);
                if (moonDist < best)
                {
                    best = moonDist;
                    bestPos = moonPos;
                    moonWins = true;
                    bestPad = -1;
                }
            }

            result.HitPoint = CannonLaserSurface.TryGetHitPoint(
                    em, planet, muzzle, mapW, mapH, moonElapsed, out float3 planetSurface)
                ? planetSurface
                : bestPos;
            if (moonWins)
            {
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                PlanetGemMoonCombatLogic.ApplyBulletDamage(
                    ref moon, damage, attackerTeam, planetState.Ownership, serverElapsed);
                em.SetComponentData(planet, moon);
                return;
            }

            if (bestPad >= 0)
                PlanetaryDefenseHitScan.ApplyDamage(em, planet, bestPad, damage, serverElapsed);
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
            rock.Health -= damage;
            rock.LastInteractTeam = attackerTeam;
            rock.LastInteractNetworkId = attackerNetworkId;
            if (rock.Health <= 0f)
            {
                rock.Health = 0f;
                rock.IsDestroyed = true;
            }

            em.SetComponentData(asteroid, rock);
            if (rock.IsDestroyed || rock.Health <= 0.01f)
                AsteroidDeathPhysics.QueueStripColliders(ecb, em, asteroid);
        }
    }
}

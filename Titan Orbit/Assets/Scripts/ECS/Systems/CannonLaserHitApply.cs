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
        /// One hostile pad or the moon on a planet. <see cref="Focus"/> is
        /// <c>slotIndex + 1</c> or <see cref="FocusMoon"/>. <see cref="Aim"/> is the
        /// pad, or the near side of the moon along <c>from</c> → moon center.
        /// </summary>
        public struct PlanetSubjectAim
        {
            public int Focus;
            public float3 Aim;
            public float3 Velocity;
            public float Distance;
            public float3 Center;
            public float Radius;
        }

        /// <summary>
        /// Closest living hostile pad or moon within <paramref name="maxDistance"/>.
        /// Friendly worlds return false. Unowned moons compete. Map size from the caller.
        /// Pass <paramref name="coneLock"/> to keep only subjects inside the barrel cone.
        /// </summary>
        public static bool TryPickClosestSubject(
            EntityManager em,
            Entity planet,
            TeamId attackerTeam,
            float3 from,
            float maxDistance,
            float mapW,
            float mapH,
            double moonElapsed,
            out PlanetSubjectAim aim,
            bool coneLock = false,
            float3 coneFwd = default)
        {
            return TrySelectSubject(
                em, planet, attackerTeam, from, maxDistance, mapW, mapH, moonElapsed,
                requiredFocus: int.MinValue, coneLock, coneFwd, out aim);
        }

        /// <summary>
        /// Aim for one committed pad or moon. Does not switch to a closer neighbor.
        /// Moon aim is the near-side shield (body when the shield is down).
        /// </summary>
        public static bool TryGetSubjectAim(
            EntityManager em,
            Entity planet,
            TeamId attackerTeam,
            float3 from,
            int focus,
            float mapW,
            float mapH,
            double moonElapsed,
            out PlanetSubjectAim aim)
        {
            aim = default;
            if (focus == FocusWholeEntity)
                return false;
            return TrySelectSubject(
                em, planet, attackerTeam, from, float.MaxValue, mapW, mapH, moonElapsed,
                focus, coneLock: false, default, out aim);
        }

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
            if (!TryPickClosestSubject(
                    em, planet, attackerTeam, aim, float.MaxValue, mapW, mapH, moonElapsed, out PlanetSubjectAim subject))
                return FocusWholeEntity;
            return subject.Focus;
        }

        /// <summary>
        /// Shared pad / moon pick. <paramref name="requiredFocus"/> <see cref="int.MinValue"/>
        /// means the closest subject inside <paramref name="maxDistance"/>. Any other
        /// value returns that pad or the moon only.
        /// </summary>
        static bool TrySelectSubject(
            EntityManager em,
            Entity planet,
            TeamId attackerTeam,
            float3 from,
            float maxDistance,
            float mapW,
            float mapH,
            double moonElapsed,
            int requiredFocus,
            bool coneLock,
            float3 coneFwd,
            out PlanetSubjectAim aim)
        {
            aim = default;
            if (planet == Entity.Null || !em.Exists(planet)
                || !em.HasComponent<PlanetState>(planet)
                || !em.HasComponent<LocalTransform>(planet))
                return false;

            var planetState = em.GetComponentData<PlanetState>(planet);
            var planetXf = em.GetComponentData<LocalTransform>(planet);
            // TeamId.None means "no friendly gate" (surface fallback). A real team
            // cannot lock its own world.
            if (attackerTeam != TeamId.None && planetState.Ownership == attackerTeam)
                return false;

            bool any = requiredFocus == int.MinValue;
            float best = maxDistance;
            bool found = false;
            if ((any || requiredFocus > 0)
                && planetState.Ownership != TeamId.None
                && em.HasBuffer<PlanetaryDefenseSlotElement>(planet))
            {
                var slots = em.GetBuffer<PlanetaryDefenseSlotElement>(planet);
                int slotCount = slots.Length;
                for (int s = 0; s < slotCount; s++)
                {
                    int focus = s + 1;
                    if (!any && focus != requiredFocus)
                        continue;

                    var slot = slots[s];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    float3 pad = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetXf.Position,
                        math.max(0.25f, planetXf.Scale),
                        planetState.PlanetLevel,
                        s,
                        slotCount);
                    if (!Consider(
                            from, pad, pad, 0f, float3.zero, focus, best, any,
                            coneLock, coneFwd, mapW, mapH, ref aim, ref found))
                        continue;
                    if (!any)
                        return true;
                    best = aim.Distance;
                }
            }

            if ((any || requiredFocus == FocusMoon)
                && em.HasComponent<PlanetGemMoonState>(planet))
            {
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                float scale = math.max(0.25f, planetXf.Scale);
                if (PlanetGemMoonCombatLogic.TryGetNonFriendlyMoonAim(
                        planetState.Ownership,
                        attackerTeam,
                        planetXf.Position,
                        planetXf.Scale,
                        planetState.PlanetLevel,
                        planetState.PlanetId,
                        planetState.IsHomePlanet,
                        moon.CurrentShield,
                        from,
                        mapW,
                        mapH,
                        moonElapsed,
                        out float3 moonAim,
                        out float3 moonVel))
                {
                    float3 moonCenter = PlanetOrbitMath.GetMoonWorldPosition(
                        planetXf.Position,
                        scale,
                        planetState.PlanetLevel,
                        planetState.PlanetId,
                        moonElapsed,
                        planetState.IsHomePlanet);
                    float radius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                        scale, planetState.IsHomePlanet, moon.CurrentShield);
                    if (Consider(
                            from, moonAim, moonCenter, radius, moonVel, FocusMoon, best, any,
                            coneLock, coneFwd, mapW, mapH, ref aim, ref found)
                        && !any)
                        return true;
                }
            }

            return found;
        }

        /// <summary>
        /// Keeps <paramref name="aim"/> when <paramref name="point"/> is nearer than
        /// <paramref name="best"/> (or is the one requested subject). Cone rejects
        /// sit outside the barrel's forward arc.
        /// </summary>
        static bool Consider(
            float3 from,
            float3 point,
            float3 center,
            float radius,
            float3 velocity,
            int focus,
            float best,
            bool any,
            bool coneLock,
            float3 coneFwd,
            float mapW,
            float mapH,
            ref PlanetSubjectAim aim,
            ref bool found)
        {
            if (coneLock
                && !CannonLaserMath.IsInRangeAndCone(
                    from, coneFwd, point, float.MaxValue, mapW, mapH, out _))
                return false;

            float d = ToroidalMapEcs.ToroidalDistance(from, point, mapW, mapH);
            if (any && d >= best)
                return false;

            aim = new PlanetSubjectAim
            {
                Focus = focus,
                Aim = point,
                Velocity = velocity,
                Distance = d,
                Center = center,
                Radius = radius,
            };
            found = true;
            return true;
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
            int planetFocus = FocusWholeEntity)
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
                    bankIndex, firePowerExtraLevels, planetFocus);
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
            int focus)
        {
            var planetState = em.GetComponentData<PlanetState>(planet);
            if (planetState.Ownership == attackerTeam)
                return;

            PlanetSubjectAim subject = default;
            bool committed = focus != FocusWholeEntity
                && TryGetSubjectAim(
                    em, planet, attackerTeam, muzzle, focus, mapW, mapH, moonElapsed, out subject);
            if (!committed
                && !TryPickClosestSubject(
                    em, planet, attackerTeam, muzzle, range, mapW, mapH, moonElapsed, out subject))
                return;

            if (subject.Focus == FocusMoon)
            {
                result.HitPoint = subject.Aim;
                var moon = em.GetComponentData<PlanetGemMoonState>(planet);
                float moonDamage = ScaleFor(
                    bankIndex, BulletBankDamageTarget.GemMoon, damage, firePowerExtraLevels);
                float planetGems = planetState.CurrentGems;
                PlanetGemMoonCombatLogic.ApplyBulletDamage(
                    ref moon,
                    ref planetGems,
                    moonDamage,
                    attackerTeam,
                    planetState.Ownership,
                    serverElapsed,
                    ecb,
                    gemPrefab,
                    subject.Aim,
                    (uint)planet.Index ^ (uint)attackerNetworkId ^ (uint)(serverElapsed * 1000.0),
                    gemSpawnServerTime);
                planetState.CurrentGems = planetGems;
                em.SetComponentData(planet, planetState);
                em.SetComponentData(planet, moon);
                return;
            }

            int pad = subject.Focus - 1;
            if (pad < 0)
                return;

            result.HitPoint = subject.Aim;
            // Pads share the ship row (same bucket as a bullet's planetary-defense hit).
            float padDamage = ScaleFor(
                bankIndex, BulletBankDamageTarget.ShipOrDrone, damage, firePowerExtraLevels);
            PlanetaryDefenseHitScan.ApplyDamage(em, planet, pad, padDamage, serverElapsed);
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

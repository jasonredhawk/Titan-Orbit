using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Server-only hit dispatch for the lightweight bullet simulation in <see cref="CombatSystem"/>.
    /// Mirrors the original Bullet.TryHit / TryToroidalAsteroidFallbackHit logic so server-side
    /// struct bullets can apply damage, trigger floating-text VFX, and despawn without ever needing
    /// a per-bullet NetworkObject.
    /// </summary>
    public static class BulletHitResolver
    {
        public const float FixedY = 0f;

        /// <summary>Damage popup metadata for a bullet hit, spawned client-side at impact VFX.</summary>
        public readonly struct BulletHitPopupInfo
        {
            public readonly bool Show;
            public readonly FloatingCountChannel Channel;
            public readonly float Damage;
            public readonly bool IsAsteroidHit;
            public readonly float AsteroidRemainingHealth;
            public readonly float AsteroidRemainingGems;

            public BulletHitPopupInfo(
                bool show,
                FloatingCountChannel channel,
                float damage,
                bool isAsteroidHit = false,
                float asteroidRemainingHealth = -1f,
                float asteroidRemainingGems = -1f)
            {
                Show = show;
                Channel = channel;
                Damage = damage;
                IsAsteroidHit = isAsteroidHit;
                AsteroidRemainingHealth = asteroidRemainingHealth;
                AsteroidRemainingGems = asteroidRemainingGems;
            }

            public static BulletHitPopupInfo None => default;
            public bool HasPopup => Show && Damage > 0.0001f;
            public bool HasAsteroidFeedback =>
                IsAsteroidHit && (HasPopup || AsteroidRemainingHealth >= 0f || AsteroidRemainingGems >= 0f);
        }

        private static float GetAppliedBulletDamage(float damage)
        {
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                return 999999f;
            return damage;
        }

        /// <summary>Which floating-count channel to use for a bullet-damage popup on this collider, if any.</summary>
        public static bool TryGetBulletDamageChannel(Collider other, TeamManager.Team ownerTeam, out FloatingCountChannel channel)
        {
            channel = default;
            if (other == null) return false;

            if (other.GetComponentInParent<Asteroid>() is Asteroid asteroid && !asteroid.IsDestroyed)
            {
                channel = FloatingCountChannel.DamageAsteroid;
                return true;
            }

            PlanetGemMoon moon = other.GetComponentInParent<PlanetGemMoon>();
            if (moon != null)
            {
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam)) return false;
                channel = FloatingCountChannel.DamageMoon;
                return true;
            }

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                channel = FloatingCountChannel.DamageShipOrDrone;
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                channel = FloatingCountChannel.DamageShipOrDrone;
                return true;
            }

            return false;
        }

        public static void SpawnBulletHitFeedbackLocal(Vector3 position, BulletHitPopupInfo popup, TeamManager.Team ownerTeam)
        {
            if (VisualEffectsManager.Instance == null) return;
            Vector3 pos = position;
            pos.y = 0f;

            if (popup.HasAsteroidFeedback)
            {
                VisualEffectsManager.Instance.SpawnAsteroidFeedbackLocal(pos, new AsteroidFloatingFeedback
                {
                    Team = ownerTeam,
                    Damage = popup.HasPopup ? popup.Damage : null,
                    RemainingHealth = popup.AsteroidRemainingHealth >= 0f ? popup.AsteroidRemainingHealth : null,
                    RemainingGems = popup.AsteroidRemainingGems >= 0f ? popup.AsteroidRemainingGems : null,
                });
                return;
            }

            if (popup.HasPopup)
                VisualEffectsManager.Instance.SpawnFloatingCountLocal(pos, popup.Channel, popup.Damage, ownerTeam);
        }

        /// <summary>True when the collider belongs to the firing ship's NetworkObject hierarchy.</summary>
        public static bool IsColliderOnFiringShipNetworkObject(Collider col, ulong ownerShipNetworkId)
        {
            if (col == null || ownerShipNetworkId == 0) return false;
            NetworkObject hitNo = col.GetComponentInParent<NetworkObject>();
            return hitNo != null && hitNo.NetworkObjectId == ownerShipNetworkId;
        }

        /// <summary>
        /// Try applying a hit against <paramref name="other"/> as either an asteroid, gem moon,
        /// debris shield, enemy ship, or enemy drone. Returns true if a valid target was hit and
        /// the bullet should despawn at <paramref name="impactWorldPos"/>.
        /// </summary>
        public static bool TryHit(
            Collider other,
            float damage,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId,
            Vector3 impactWorldPos,
            out Vector3 finalImpactPos,
            out BulletHitPopupInfo popupInfo,
            int bulletBankIndex = -1)
        {
            finalImpactPos = impactWorldPos;
            popupInfo = BulletHitPopupInfo.None;
            if (other == null) return false;

            Asteroid asteroid = other.GetComponentInParent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                    damage, bulletBankIndex, BulletBankDamageTarget.Asteroid);
                ApplyAsteroidHit(asteroid, resolved, ownerTeam, ownerShipNetworkId, impactWorldPos);
                float applied = GetAppliedBulletDamage(resolved);
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, other, impactWorldPos, ownerTeam, ownerShipNetworkId, applied, targetWasHealed: false);
                popupInfo = new BulletHitPopupInfo(
                    true,
                    FloatingCountChannel.DamageAsteroid,
                    applied,
                    isAsteroidHit: true,
                    asteroidRemainingHealth: asteroid.RemainingHealth,
                    asteroidRemainingGems: asteroid.RemainingGems);
                return true;
            }

            PlanetGemMoon moon = other.GetComponentInParent<PlanetGemMoon>();
            if (moon != null)
            {
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam)) return false;
                float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                    damage, bulletBankIndex, BulletBankDamageTarget.GemMoon);
                ApplyMoonHit(moon, resolved, ownerTeam, impactWorldPos);
                float applied = GetAppliedBulletDamage(resolved);
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, other, impactWorldPos, ownerTeam, ownerShipNetworkId, applied, targetWasHealed: false);
                popupInfo = new BulletHitPopupInfo(true, FloatingCountChannel.DamageMoon, applied);
                return true;
            }

            ShipDeathDebris debrisShield = other.GetComponentInParent<ShipDeathDebris>();
            if (debrisShield != null && debrisShield.TryAbsorbBullet(ownerTeam))
                return true;

            Gem gem = other.GetComponentInParent<Gem>();
            if (gem != null && !gem.IsInPool)
            {
                float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                    damage, bulletBankIndex, BulletBankDamageTarget.Gem);
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, other, impactWorldPos, ownerTeam, ownerShipNetworkId, resolved, targetWasHealed: false);
                finalImpactPos = impactWorldPos;
                return true;
            }

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead)
            {
                if (ship.ShipTeam == ownerTeam)
                {
                    if (BulletBankProfileUtility.TryHealFriendlyShip(ship, bulletBankIndex, damage, ownerTeam, out float heal))
                    {
                        BulletBankProfileUtility.ApplyOnHitEffects(
                            bulletBankIndex, other, impactWorldPos, ownerTeam, ownerShipNetworkId, heal, targetWasHealed: true);
                        popupInfo = new BulletHitPopupInfo(true, FloatingCountChannel.Healing, heal);
                        return true;
                    }
                    return false;
                }

                float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                    damage, bulletBankIndex, BulletBankDamageTarget.ShipOrDrone);
                ApplyShipHit(ship, resolved, ownerTeam, ownerShipNetworkId, impactWorldPos);
                float applied = GetAppliedBulletDamage(resolved);
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, other, impactWorldPos, ownerTeam, ownerShipNetworkId, applied, targetWasHealed: false);
                popupInfo = new BulletHitPopupInfo(true, FloatingCountChannel.DamageShipOrDrone, applied);
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                    damage, bulletBankIndex, BulletBankDamageTarget.ShipOrDrone);
                ApplyDroneHit(drone, resolved, ownerTeam, ownerShipNetworkId, impactWorldPos);
                float applied = GetAppliedBulletDamage(resolved);
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, other, impactWorldPos, ownerTeam, ownerShipNetworkId, applied, targetWasHealed: false);
                popupInfo = new BulletHitPopupInfo(true, FloatingCountChannel.DamageShipOrDrone, applied);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Client-only: same target types as <see cref="TryHit"/> with no damage or RPCs. Used so owner-predicted
        /// bullet visuals stop on asteroids, ships, moons, etc. without waiting for the server impact message.
        /// </summary>
        public static bool IsCosmeticBulletImpactTarget(Collider other, TeamManager.Team ownerTeam, int bulletBankIndex = -1)
        {
            if (other == null) return false;

            Asteroid asteroid = other.GetComponentInParent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
                return true;

            PlanetGemMoon moon = other.GetComponentInParent<PlanetGemMoon>();
            if (moon != null)
                return !moon.IsTeamFriendlyToThisMoon(ownerTeam);

            ShipDeathDebris debrisShield = other.GetComponentInParent<ShipDeathDebris>();
            if (debrisShield != null)
                return debrisShield.WouldAbsorbEnemyBulletCosmetic(ownerTeam);

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead)
            {
                if (ship.ShipTeam == ownerTeam)
                {
                    return BulletBankProfileUtility.TryGetProfile(bulletBankIndex, out BulletBankProfile profile)
                           && profile != null
                           && profile.HasAbility(BulletBankAbilityType.HealFriendly);
                }
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
                return true;

            Gem gem = other.GetComponentInParent<Gem>();
            if (gem != null && !gem.IsInPool)
                return true;

            return false;
        }

        /// <summary>
        /// Toroidal asteroid segment test without applying damage (owner-predicted client tracer only).
        /// </summary>
        public static bool TryToroidalAsteroidSegmentCosmeticOnly(
            Vector3 from,
            Vector3 to,
            float bulletRadius,
            out Vector3 impactPos)
        {
            impactPos = to;
            if (Asteroid.AllAsteroids == null || Asteroid.AllAsteroids.Count == 0)
                return false;

            float mapW = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float mapH = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            float bestDistSq = float.MaxValue;
            Asteroid bestAsteroid = null;
            Vector3 bestImpact = to;

            float radiusPad = Mathf.Max(0.01f, bulletRadius);

            for (int i = 0; i < Asteroid.AllAsteroids.Count; i++)
            {
                Asteroid asteroid = Asteroid.AllAsteroids[i];
                if (asteroid == null || asteroid.IsDestroyed) continue;

                float combinedRadius = asteroid.GetCollisionRadiusWorld() + radiusPad;
                Vector3 center = asteroid.transform.position;

                Vector3 fromLocal = ToroidalMap.ShortestWorldOffsetXZ(center, from);
                Vector3 toLocal = ToroidalMap.ShortestWorldOffsetXZ(center, to);

                Vector3 seg = toLocal - fromLocal;
                if (seg.x > halfW) seg.x -= mapW;
                else if (seg.x < -halfW) seg.x += mapW;
                if (seg.z > halfH) seg.z -= mapH;
                else if (seg.z < -halfH) seg.z += mapH;
                Vector3 toLocalUnwrapped = fromLocal + seg;

                Vector3 closest = ClosestPointOnSegment(fromLocal, toLocalUnwrapped, Vector3.zero);
                float distSq = closest.sqrMagnitude;
                if (distSq > combinedRadius * combinedRadius) continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestAsteroid = asteroid;
                    bestImpact = new Vector3(center.x + closest.x, FixedY, center.z + closest.z);
                }
            }

            if (bestAsteroid == null) return false;

            impactPos = bestImpact;
            return true;
        }

        /// <summary>Client-only toroidal moon impact test (no damage).</summary>
        public static bool TryToroidalGemMoonSegmentCosmeticOnly(
            Vector3 from,
            Vector3 to,
            float bulletRadius,
            TeamManager.Team ownerTeam,
            out Vector3 impactPos)
        {
            impactPos = to;
            int moonCount = PlanetGemMoon.ActiveMoonCount;
            if (moonCount == 0) return false;

            float mapW = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float mapH = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            float bestDistSq = float.MaxValue;
            bool found = false;
            Vector3 bestImpact = to;
            float radiusPad = Mathf.Max(0.01f, bulletRadius);

            for (int i = 0; i < moonCount; i++)
            {
                PlanetGemMoon moon = PlanetGemMoon.GetActiveMoonAt(i);
                if (moon == null || !moon.isActiveAndEnabled) continue;
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam)) continue;

                float combinedRadius = moon.GetMoonBulletHitRadiusWorld() + radiusPad;
                Vector3 center = moon.transform.position;
                center.y = 0f;

                Vector3 fromLocal = ToroidalMap.ShortestWorldOffsetXZ(center, from);
                Vector3 toLocal = ToroidalMap.ShortestWorldOffsetXZ(center, to);

                Vector3 seg = toLocal - fromLocal;
                if (seg.x > halfW) seg.x -= mapW;
                else if (seg.x < -halfW) seg.x += mapW;
                if (seg.z > halfH) seg.z -= mapH;
                else if (seg.z < -halfH) seg.z += mapH;
                Vector3 toLocalUnwrapped = fromLocal + seg;

                Vector3 closest = ClosestPointOnSegment(fromLocal, toLocalUnwrapped, Vector3.zero);
                float distSq = closest.sqrMagnitude;
                if (distSq > combinedRadius * combinedRadius) continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    found = true;
                    bestImpact = new Vector3(center.x + closest.x, FixedY, center.z + closest.z);
                }
            }

            if (!found) return false;

            impactPos = bestImpact;
            return true;
        }

        public static void ApplyAsteroidHit(Asteroid asteroid, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, Vector3 impactWorldPos)
        {
            float appliedDamage = GetAppliedBulletDamage(damage);
            asteroid.ApplyDamageFromBulletServer(appliedDamage, ownerShipNetworkId);
        }

        public static void ApplyMoonHit(PlanetGemMoon moon, float damage, TeamManager.Team ownerTeam, Vector3 impactWorldPos)
        {
            float appliedDamage = GetAppliedBulletDamage(damage);
            moon.TakeDamageServer(appliedDamage, ownerTeam);
        }

        public static void ApplyShipHit(Starship ship, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, Vector3 impactWorldPos)
        {
            ship.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);
        }

        public static void ApplyDroneHit(DroneBase drone, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, Vector3 impactWorldPos)
        {
            drone.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);
        }

        /// <summary>
        /// Sweeps a bullet segment through toroidal space against every active asteroid. World-space
        /// physics queries miss when the bullet and asteroid sit in different toroidal tiles
        /// (ships fly arbitrarily far across the wrap), so we evaluate distance between the unwrapped
        /// segment and each asteroid center. Returns true and applies damage on the closest hit.
        /// </summary>
        public static bool TryToroidalAsteroidSegmentHit(
            Vector3 from,
            Vector3 to,
            float bulletRadius,
            float damage,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId,
            out Vector3 impactPos,
            out BulletHitPopupInfo popupInfo,
            int bulletBankIndex = -1)
        {
            popupInfo = BulletHitPopupInfo.None;
            impactPos = to;
            if (Asteroid.AllAsteroids == null || Asteroid.AllAsteroids.Count == 0)
                return false;

            float mapW = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float mapH = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            float bestDistSq = float.MaxValue;
            Asteroid bestAsteroid = null;
            Vector3 bestImpact = to;

            float radiusPad = Mathf.Max(0.01f, bulletRadius);

            for (int i = 0; i < Asteroid.AllAsteroids.Count; i++)
            {
                Asteroid asteroid = Asteroid.AllAsteroids[i];
                if (asteroid == null || asteroid.IsDestroyed) continue;

                float combinedRadius = asteroid.GetCollisionRadiusWorld() + radiusPad;
                Vector3 center = asteroid.transform.position;

                Vector3 fromLocal = ToroidalMap.ShortestWorldOffsetXZ(center, from);
                Vector3 toLocal = ToroidalMap.ShortestWorldOffsetXZ(center, to);

                Vector3 seg = toLocal - fromLocal;
                if (seg.x > halfW) seg.x -= mapW;
                else if (seg.x < -halfW) seg.x += mapW;
                if (seg.z > halfH) seg.z -= mapH;
                else if (seg.z < -halfH) seg.z += mapH;
                Vector3 toLocalUnwrapped = fromLocal + seg;

                Vector3 closest = ClosestPointOnSegment(fromLocal, toLocalUnwrapped, Vector3.zero);
                float distSq = closest.sqrMagnitude;
                if (distSq > combinedRadius * combinedRadius) continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestAsteroid = asteroid;
                    bestImpact = new Vector3(center.x + closest.x, FixedY, center.z + closest.z);
                }
            }

            if (bestAsteroid == null) return false;

            float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                damage, bulletBankIndex, BulletBankDamageTarget.Asteroid);
            ApplyAsteroidHit(bestAsteroid, resolved, ownerTeam, ownerShipNetworkId, bestImpact);
            float applied = GetAppliedBulletDamage(resolved);
            Collider asteroidCol = bestAsteroid.GetComponentInChildren<Collider>();
            if (asteroidCol != null)
            {
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, asteroidCol, bestImpact, ownerTeam, ownerShipNetworkId, applied, targetWasHealed: false);
            }
            popupInfo = new BulletHitPopupInfo(
                true,
                FloatingCountChannel.DamageAsteroid,
                applied,
                isAsteroidHit: true,
                asteroidRemainingHealth: bestAsteroid.RemainingHealth,
                asteroidRemainingGems: bestAsteroid.RemainingGems);
            impactPos = bestImpact;
            return true;
        }

        /// <summary>
        /// Toroidal gem-moon segment test (same motivation as <see cref="TryToroidalAsteroidSegmentHit"/>).
        /// Uses shield outer radius while the shield has points, otherwise the moon body radius.
        /// </summary>
        public static bool TryToroidalGemMoonSegmentHit(
            Vector3 from,
            Vector3 to,
            float bulletRadius,
            float damage,
            TeamManager.Team ownerTeam,
            ulong ownerShipNetworkId,
            out Vector3 impactPos,
            out BulletHitPopupInfo popupInfo,
            int bulletBankIndex = -1)
        {
            popupInfo = BulletHitPopupInfo.None;
            impactPos = to;
            int moonCount = PlanetGemMoon.ActiveMoonCount;
            if (moonCount == 0) return false;

            float mapW = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float mapH = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            float halfW = mapW * 0.5f;
            float halfH = mapH * 0.5f;
            float bestDistSq = float.MaxValue;
            PlanetGemMoon bestMoon = null;
            Vector3 bestImpact = to;
            float radiusPad = Mathf.Max(0.01f, bulletRadius);

            for (int i = 0; i < moonCount; i++)
            {
                PlanetGemMoon moon = PlanetGemMoon.GetActiveMoonAt(i);
                if (moon == null || !moon.isActiveAndEnabled) continue;
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam)) continue;

                float combinedRadius = moon.GetMoonBulletHitRadiusWorld() + radiusPad;
                Vector3 center = moon.transform.position;
                center.y = 0f;

                Vector3 fromLocal = ToroidalMap.ShortestWorldOffsetXZ(center, from);
                Vector3 toLocal = ToroidalMap.ShortestWorldOffsetXZ(center, to);

                Vector3 seg = toLocal - fromLocal;
                if (seg.x > halfW) seg.x -= mapW;
                else if (seg.x < -halfW) seg.x += mapW;
                if (seg.z > halfH) seg.z -= mapH;
                else if (seg.z < -halfH) seg.z += mapH;
                Vector3 toLocalUnwrapped = fromLocal + seg;

                Vector3 closest = ClosestPointOnSegment(fromLocal, toLocalUnwrapped, Vector3.zero);
                float distSq = closest.sqrMagnitude;
                if (distSq > combinedRadius * combinedRadius) continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestMoon = moon;
                    bestImpact = new Vector3(center.x + closest.x, FixedY, center.z + closest.z);
                }
            }

            if (bestMoon == null) return false;

            float resolved = BulletBankProfileUtility.ResolveDamageForTarget(
                damage, bulletBankIndex, BulletBankDamageTarget.GemMoon);
            ApplyMoonHit(bestMoon, resolved, ownerTeam, bestImpact);
            float applied = GetAppliedBulletDamage(resolved);
            if (bestMoon.TryGetComponent<Collider>(out Collider moonCol))
            {
                BulletBankProfileUtility.ApplyOnHitEffects(
                    bulletBankIndex, moonCol, bestImpact, ownerTeam, ownerShipNetworkId, applied, targetWasHealed: false);
            }
            popupInfo = new BulletHitPopupInfo(true, FloatingCountChannel.DamageMoon, applied);
            impactPos = bestImpact;
            return true;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float denom = Vector3.Dot(ab, ab);
            if (denom <= 1e-6f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / denom);
            return a + ab * t;
        }

        /// <summary>Stronger projectile damage = lower impact pitch. Mirrors Bullet.GetImpactSoundPitch.</summary>
        public static float GetImpactSoundPitch(float damage)
        {
            float firePower = Mathf.Max(0.1f, damage);
            const float minFirePower = 1f;
            const float maxFirePower = 80f;
            const float highPitch = 2.4f;
            const float lowPitch = 0.35f;

            float clampedPower = Mathf.Clamp(firePower, minFirePower, maxFirePower);
            float minLog = Mathf.Log10(minFirePower);
            float maxLog = Mathf.Log10(maxFirePower);
            float powerLog = Mathf.Log10(clampedPower);
            float normalized = Mathf.InverseLerp(minLog, maxLog, powerLog);
            return Mathf.Lerp(highPitch, lowPitch, normalized);
        }
    }
}

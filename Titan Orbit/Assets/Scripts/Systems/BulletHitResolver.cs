using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
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
            out Vector3 finalImpactPos)
        {
            finalImpactPos = impactWorldPos;
            if (other == null) return false;

            Asteroid asteroid = other.GetComponentInParent<Asteroid>();
            if (asteroid != null && !asteroid.IsDestroyed)
            {
                ApplyAsteroidHit(asteroid, damage, ownerTeam, ownerShipNetworkId, impactWorldPos);
                return true;
            }

            PlanetGemMoon moon = other.GetComponentInParent<PlanetGemMoon>();
            if (moon != null)
            {
                if (moon.IsTeamFriendlyToThisMoon(ownerTeam)) return false;
                ApplyMoonHit(moon, damage, ownerTeam, impactWorldPos);
                return true;
            }

            ShipDeathDebris debrisShield = other.GetComponentInParent<ShipDeathDebris>();
            if (debrisShield != null && debrisShield.TryAbsorbBullet(ownerTeam))
                return true;

            Starship ship = other.GetComponentInParent<Starship>();
            if (ship != null && !ship.IsDead && ship.ShipTeam != ownerTeam)
            {
                ApplyShipHit(ship, damage, ownerTeam, ownerShipNetworkId, impactWorldPos);
                return true;
            }

            DroneBase drone = other.GetComponentInParent<DroneBase>();
            if (drone != null && !drone.IsDestroyed && drone.IsEnemyTeam(ownerTeam))
            {
                ApplyDroneHit(drone, damage, ownerTeam, ownerShipNetworkId, impactWorldPos);
                return true;
            }

            return false;
        }

        public static void ApplyAsteroidHit(Asteroid asteroid, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, Vector3 impactWorldPos)
        {
            float appliedDamage = damage;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                appliedDamage = 999999f;
            asteroid.ApplyDamageFromBulletServer(appliedDamage, ownerShipNetworkId);

            if (VisualEffectsManager.Instance != null)
            {
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    impactWorldPos,
                    (int)FloatingCountChannel.DamageAsteroid,
                    appliedDamage,
                    (int)ownerTeam);
                VisualEffectsManager.Instance.SpawnAsteroidStatsFloatingTextServerRpc(
                    impactWorldPos,
                    asteroid.RemainingHealth,
                    asteroid.RemainingGems,
                    (int)ownerTeam);
            }
        }

        public static void ApplyMoonHit(PlanetGemMoon moon, float damage, TeamManager.Team ownerTeam, Vector3 impactWorldPos)
        {
            float appliedDamage = damage;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                appliedDamage = 999999f;
            moon.TakeDamageServer(appliedDamage, ownerTeam);

            if (VisualEffectsManager.Instance != null)
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    impactWorldPos,
                    (int)FloatingCountChannel.DamageMoon,
                    appliedDamage,
                    (int)ownerTeam);
        }

        public static void ApplyShipHit(Starship ship, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, Vector3 impactWorldPos)
        {
            ship.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);
            if (VisualEffectsManager.Instance != null)
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    impactWorldPos,
                    (int)FloatingCountChannel.DamageShipOrDrone,
                    damage,
                    (int)ownerTeam);
        }

        public static void ApplyDroneHit(DroneBase drone, float damage, TeamManager.Team ownerTeam, ulong ownerShipNetworkId, Vector3 impactWorldPos)
        {
            drone.TakeDamageServerRpc(damage, ownerTeam, ownerShipNetworkId);
            if (VisualEffectsManager.Instance != null)
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    impactWorldPos,
                    (int)FloatingCountChannel.DamageShipOrDrone,
                    damage,
                    (int)ownerTeam);
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

            ApplyAsteroidHit(bestAsteroid, damage, ownerTeam, ownerShipNetworkId, bestImpact);
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

using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>Drone hits without physics colliders — sphere tests against swarm / loot positions.</summary>
    public static class DroneSwarmHitScan
    {
        public static bool TrySegmentHit(
            Vector3 from,
            Vector3 to,
            float bulletRadius,
            TeamManager.Team ownerTeam,
            out DroneBody hitDrone,
            out Vector3 impactPos)
        {
            DroneBody bestDrone = null;
            Vector3 bestImpact = to;
            float bestDistSq = float.MaxValue;
            float bulletPad = Mathf.Max(0.01f, bulletRadius);

            foreach (var ship in Starship.AllStarships)
            {
                if (ship == null) continue;
                DroneSwarmController swarm = ship.DroneSwarm;
                if (swarm == null) continue;
                swarm.EnumerateDroneHitTargets((body, worldPos) =>
                {
                    if (body == null || body.IsDestroyed || !body.IsEnemyTeam(ownerTeam)) return;
                    float hitRadius = body.HitSphereRadius + bulletPad;
                    if (!DroneSwarmPositioning.SegmentIntersectsSphere(from, to, worldPos, hitRadius, out Vector3 closest))
                        return;
                    float dSq = (closest - from).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestDrone = body;
                        bestImpact = closest;
                    }
                });
            }

            for (int i = 0; i < LootableDrone.AllLootableDrones.Count; i++)
            {
                LootableDrone loot = LootableDrone.AllLootableDrones[i];
                if (loot == null || !loot.IsSpawned || loot.IsDestroyed) continue;
                if (!loot.IsEnemyTeam(ownerTeam)) continue;
                Vector3 pos = loot.transform.position;
                pos.y = DroneSwarmLogic.FixedY;
                DroneBody body = loot.GetComponent<DroneBody>();
                if (body == null) continue;
                float hitRadius = body.HitSphereRadius + bulletPad;
                if (!DroneSwarmPositioning.SegmentIntersectsSphere(from, to, pos, hitRadius, out Vector3 closest))
                    continue;
                float dSq = (closest - from).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    if (body != null)
                    {
                        bestDistSq = dSq;
                        bestDrone = body;
                        bestImpact = closest;
                    }
                }
            }

            hitDrone = bestDrone;
            impactPos = bestImpact;
            return hitDrone != null;
        }

        public static bool TryOverlapHit(
            Vector3 position,
            float bulletRadius,
            TeamManager.Team ownerTeam,
            out DroneBody hitDrone,
            out Vector3 impactPos)
        {
            DroneBody bestDrone = null;
            Vector3 bestImpact = position;
            float bulletPad = Mathf.Max(0.01f, bulletRadius);
            float bestDistSq = float.MaxValue;

            foreach (var ship in Starship.AllStarships)
            {
                if (ship == null) continue;
                DroneSwarmController swarm = ship.DroneSwarm;
                if (swarm == null) continue;
                swarm.EnumerateDroneHitTargets((body, worldPos) =>
                {
                    if (body == null || body.IsDestroyed || !body.IsEnemyTeam(ownerTeam)) return;
                    float hitRadius = body.HitSphereRadius + bulletPad;
                    float dSq = ToroidalMap.WrapPosition(worldPos - position).sqrMagnitude;
                    if (dSq > hitRadius * hitRadius || dSq >= bestDistSq) return;
                    bestDistSq = dSq;
                    bestDrone = body;
                    bestImpact = worldPos;
                });
            }

            for (int i = 0; i < LootableDrone.AllLootableDrones.Count; i++)
            {
                LootableDrone loot = LootableDrone.AllLootableDrones[i];
                if (loot == null || !loot.IsSpawned || loot.IsDestroyed) continue;
                if (!loot.IsEnemyTeam(ownerTeam)) continue;
                Vector3 pos = loot.transform.position;
                pos.y = DroneSwarmLogic.FixedY;
                DroneBody body = loot.GetComponent<DroneBody>();
                if (body == null) continue;
                float hitRadius = body.HitSphereRadius + bulletPad;
                float dSq = ToroidalMap.WrapPosition(pos - position).sqrMagnitude;
                if (dSq > hitRadius * hitRadius || dSq >= bestDistSq) continue;
                if (body != null)
                {
                    bestDistSq = dSq;
                    bestDrone = body;
                    bestImpact = pos;
                }
            }

            hitDrone = bestDrone;
            impactPos = bestImpact;
            return hitDrone != null;
        }
    }
}

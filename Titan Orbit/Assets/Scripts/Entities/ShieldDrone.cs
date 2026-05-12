using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>Drone that rotates around the starship and moves to block incoming enemy bullets.
    /// Threat data comes from <see cref="DroneTargetCache"/> snapshots of the server bullet
    /// simulation; bullets are no longer per-NetworkObject so we cannot inspect transforms.</summary>
    public class ShieldDrone : DroneBase
    {
        [Header("Shield Drone")]
        [SerializeField] private float bulletDetectRadius = 12f;
        [SerializeField] private float interceptSpeedMultiplier = 1.5f;

        protected override void DroneBehaviourServer()
        {
            if (TryFindIncomingBulletTowardShip(out Vector3 bulletPos, out Vector3 _))
            {
                Vector3 shipPos = ownerShip.transform.position;
                shipPos.y = 0f;
                bulletPos.y = 0f;
                Vector3 toShip = shipPos - bulletPos;
                toShip.y = 0f;
                if (toShip.sqrMagnitude > 0.01f)
                {
                    float distToShip = toShip.magnitude;
                    Vector3 bulletDir = toShip / distToShip;
                    float interceptDist = Mathf.Max(1.5f, distToShip * 0.4f);
                    Vector3 idealPos = bulletPos + bulletDir * (distToShip - interceptDist);
                    Vector3 myPos = transform.position;
                    myPos.y = 0f;
                    Vector3 toIdeal = idealPos - myPos;
                    toIdeal.y = 0f;
                    if (toIdeal.sqrMagnitude > 0.1f)
                    {
                        float speed = moveSpeed * interceptSpeedMultiplier;
                        Vector3 vel = toIdeal.normalized * Mathf.Min(speed, toIdeal.magnitude / Time.fixedDeltaTime);
                        if (rb != null) rb.linearVelocity = vel;
                        transform.position = myPos + vel * Time.fixedDeltaTime;
                        return;
                    }
                }
            }
            UpdateOrbitPosition();
        }

        private bool TryFindIncomingBulletTowardShip(out Vector3 bulletPos, out Vector3 bulletVelocity)
        {
            bulletPos = Vector3.zero;
            bulletVelocity = Vector3.zero;
            if (ownerShip == null) return false;
            DroneTargetCache.RefreshIfNeeded();

            Vector3 shipPos = ownerShip.transform.position;
            shipPos.y = 0f;
            int n = DroneTargetCache.BulletSnapshotCount;
            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                ServerBulletSnapshot snap = DroneTargetCache.GetBulletSnapshot(i);
                if (snap.OwnerTeam == ownerShip.ShipTeam) continue;

                Vector3 bp = snap.Position;
                bp.y = 0f;
                float dist = Vector3.Distance(bp, shipPos);
                if (dist > bulletDetectRadius) continue;

                Vector3 toShip = shipPos - bp;
                toShip.y = 0f;
                if (toShip.sqrMagnitude < 0.01f) continue;
                toShip.Normalize();

                Vector3 vel = snap.Velocity;
                vel.y = 0f;
                if (vel.sqrMagnitude < 0.01f) continue;
                Vector3 velNormalized = vel.normalized;

                float dot = Vector3.Dot(velNormalized, toShip);
                if (dot < 0.5f) continue;

                float score = dist * (1f - dot);
                if (score < bestScore)
                {
                    bestScore = score;
                    bulletPos = bp;
                    bulletVelocity = vel;
                    found = true;
                }
            }
            return found;
        }
    }
}

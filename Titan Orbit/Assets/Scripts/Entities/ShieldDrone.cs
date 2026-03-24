using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>Drone that rotates around the starship and moves to block incoming enemy bullets.</summary>
    public class ShieldDrone : DroneBase
    {
        [Header("Shield Drone")]
        [SerializeField] private float bulletDetectRadius = 12f;
        [SerializeField] private float interceptSpeedMultiplier = 1.5f;

        protected override void DroneBehaviourServer()
        {
            Bullet threat = FindIncomingBulletTowardShip();
            if (threat != null)
            {
                Vector3 shipPos = ownerShip.transform.position;
                shipPos.y = 0f;
                Vector3 bulletPos = threat.transform.position;
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

        private Bullet FindIncomingBulletTowardShip()
        {
            if (ownerShip == null) return null;
            DroneTargetCache.RefreshIfNeeded();
            Vector3 shipPos = ownerShip.transform.position;
            shipPos.y = 0f;
            Bullet[] bullets = DroneTargetCache.Bullets;
            Bullet best = null;
            float bestScore = float.MaxValue;
            foreach (var b in bullets)
            {
                if (b.OwnerTeam == ownerShip.ShipTeam) continue;
                Vector3 bp = b.transform.position;
                bp.y = 0f;
                float dist = Vector3.Distance(bp, shipPos);
                if (dist > bulletDetectRadius) continue;
                Vector3 toShip = shipPos - bp;
                toShip.y = 0f;
                if (toShip.sqrMagnitude < 0.01f) continue;
                toShip.Normalize();
                Rigidbody brb = b.GetComponent<Rigidbody>();
                Vector3 bulletVel = brb != null ? brb.linearVelocity : Vector3.forward;
                bulletVel.y = 0f;
                if (bulletVel.sqrMagnitude < 0.01f) continue;
                bulletVel.Normalize();
                float dot = Vector3.Dot(bulletVel, toShip);
                if (dot < 0.5f) continue;
                float score = dist * (1f - dot);
                if (score < bestScore) { bestScore = score; best = b; }
            }
            return best;
        }
    }
}

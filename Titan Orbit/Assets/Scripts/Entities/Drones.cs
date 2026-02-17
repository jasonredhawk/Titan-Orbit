using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Base for drones that swarm around the player's starship. Has HP, can be destroyed by enemy fire.
    /// Subclasses: Fighter (attack enemy ships), Shield (block bullets), Mining (shoot asteroids).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public abstract class DroneBase : NetworkBehaviour
    {
        [Header("Drone Base")]
        [SerializeField] protected float maxHp = 30f;
        [SerializeField] protected float orbitRadius = 3f;
        [SerializeField] protected float orbitSpeed = 90f;
        [SerializeField] protected float moveSpeed = 8f;
        [SerializeField] protected float swarmSpreadDegrees = 15f;

        protected NetworkVariable<float> currentHp = new NetworkVariable<float>(30f);
        protected Starship ownerShip;
        protected float orbitAngle;
        protected Rigidbody rb;
        private const float FIXED_Y = 0f;

        public float CurrentHp => currentHp.Value;
        public float MaxHp => maxHp;
        public Starship OwnerShip => ownerShip;
        public bool IsDestroyed => currentHp.Value <= 0f;

        public virtual void SetOwnerShip(Starship ship)
        {
            ownerShip = ship;
            orbitAngle = Random.Range(0f, 360f);
        }

        protected void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
        }

        public override void OnNetworkSpawn()
        {
            Vector3 p = transform.position;
            p.y = FIXED_Y;
            transform.position = p;
            if (IsServer)
                currentHp.Value = maxHp;
        }

        protected void FixedUpdate()
        {
            Vector3 p = transform.position;
            p.y = FIXED_Y;
            transform.position = p;
            if (rb != null && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                var v = rb.linearVelocity;
                v.y = 0f;
                rb.linearVelocity = v;
            }

            if (ownerShip == null || ownerShip.IsDead)
            {
                if (IsServer)
                {
                    var no = GetComponent<NetworkObject>();
                    if (no != null && no.IsSpawned) no.Despawn();
                }
                return;
            }

            if (IsServer)
            {
                if (currentHp.Value <= 0f)
                {
                    var no = GetComponent<NetworkObject>();
                    if (no != null && no.IsSpawned) no.Despawn();
                    return;
                }
                DroneBehaviourServer();
            }
        }

        protected virtual void DroneBehaviourServer()
        {
            UpdateOrbitPosition();
        }

        protected void UpdateOrbitPosition()
        {
            if (ownerShip == null) return;
            orbitAngle += orbitSpeed * Mathf.Deg2Rad * Time.fixedDeltaTime;
            Vector3 shipPos = ownerShip.transform.position;
            shipPos.y = FIXED_Y;
            Vector3 offset = new Vector3(Mathf.Cos(orbitAngle), 0f, Mathf.Sin(orbitAngle)) * orbitRadius;
            Vector3 targetPos = ToroidalMap.WrapPosition(shipPos + offset);
            Vector3 currentPos = transform.position;
            currentPos.y = FIXED_Y;
            Vector3 toTarget = ToroidalMap.WrapPosition(targetPos - currentPos);
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.01f)
            {
                Vector3 vel = toTarget.normalized * Mathf.Min(moveSpeed, toTarget.magnitude / Time.fixedDeltaTime);
                if (rb != null) rb.linearVelocity = vel;
                transform.position = ToroidalMap.WrapPosition(currentPos + vel * Time.fixedDeltaTime);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam)
        {
            if (ownerShip != null && attackerTeam == ownerShip.ShipTeam) return;
            currentHp.Value = Mathf.Max(0f, currentHp.Value - damage);
        }

        public bool IsEnemyTeam(TeamManager.Team team)
        {
            return ownerShip != null && team != TeamManager.Team.None && team != ownerShip.ShipTeam;
        }
    }

    /// <summary>Drone that attacks only enemy ships.</summary>
    public class FighterDrone : DroneBase
    {
        [Header("Fighter Drone")]
        [SerializeField] private float fireRate = 1.2f;
        [SerializeField] private float firePower = 6f;
        [SerializeField] private float bulletSpeed = 18f;
        [SerializeField] private float targetRange = 25f;
        [SerializeField] private Transform firePoint;
        private float lastFireTime;

        protected override void DroneBehaviourServer()
        {
            UpdateOrbitPosition();
            if (ownerShip == null || TeamManager.Instance == null) return;
            if (firePoint == null) firePoint = transform;
            Starship target = FindNearestEnemyShip();
            if (target != null && Time.time - lastFireTime >= 1f / fireRate)
            {
                Vector3 dir = (target.transform.position - firePoint.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                {
                    dir.Normalize();
                    if (CombatSystem.Instance != null)
                    {
                        CombatSystem.Instance.SpawnBulletServerRpc(firePoint.position, dir, bulletSpeed, firePower, ownerShip.ShipTeam);
                        lastFireTime = Time.time;
                    }
                }
            }
        }

        private Starship FindNearestEnemyShip()
        {
            if (ownerShip == null) return null;
            TeamManager.Team myTeam = ownerShip.ShipTeam;
            Vector3 myPos = transform.position;
            Starship nearest = null;
            float nearestSq = targetRange * targetRange;
            foreach (var ship in Object.FindObjectsByType<Starship>(FindObjectsSortMode.None))
            {
                if (ship.IsDead || ship.ShipTeam == myTeam) continue;
                float sq = (ToroidalMap.WrapPosition(ship.transform.position - myPos)).sqrMagnitude;
                if (sq < nearestSq) { nearestSq = sq; nearest = ship; }
            }
            return nearest;
        }
    }

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
                    idealPos = ToroidalMap.WrapPosition(idealPos);
                    Vector3 myPos = transform.position;
                    myPos.y = 0f;
                    Vector3 toIdeal = ToroidalMap.WrapPosition(idealPos - myPos);
                    toIdeal.y = 0f;
                    if (toIdeal.sqrMagnitude > 0.1f)
                    {
                        float speed = moveSpeed * interceptSpeedMultiplier;
                        Vector3 vel = toIdeal.normalized * Mathf.Min(speed, toIdeal.magnitude / Time.fixedDeltaTime);
                        if (rb != null) rb.linearVelocity = vel;
                        transform.position = ToroidalMap.WrapPosition(myPos + vel * Time.fixedDeltaTime);
                        return;
                    }
                }
            }
            UpdateOrbitPosition();
        }

        private Bullet FindIncomingBulletTowardShip()
        {
            if (ownerShip == null) return null;
            Vector3 shipPos = ownerShip.transform.position;
            shipPos.y = 0f;
            Bullet[] bullets = Object.FindObjectsByType<Bullet>(FindObjectsSortMode.None);
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

    /// <summary>Drone that shoots at asteroids to mine them.</summary>
    public class MiningDrone : DroneBase
    {
        [Header("Mining Drone")]
        [SerializeField] private float fireRate = 1f;
        [SerializeField] private float firePower = 8f;
        [SerializeField] private float bulletSpeed = 16f;
        [SerializeField] private float targetRange = 15f;
        [SerializeField] private Transform firePoint;
        private float lastFireTime;

        protected override void DroneBehaviourServer()
        {
            UpdateOrbitPosition();
            if (ownerShip == null) return;
            if (firePoint == null) firePoint = transform;
            Asteroid target = FindNearestAsteroid();
            if (target != null && !target.IsDestroyed && Time.time - lastFireTime >= 1f / fireRate)
            {
                Vector3 dir = (target.transform.position - firePoint.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                {
                    dir.Normalize();
                    if (CombatSystem.Instance != null)
                    {
                        CombatSystem.Instance.SpawnBulletServerRpc(firePoint.position, dir, bulletSpeed, firePower, ownerShip.ShipTeam);
                        lastFireTime = Time.time;
                    }
                }
            }
        }

        private Asteroid FindNearestAsteroid()
        {
            Vector3 myPos = transform.position;
            Asteroid nearest = null;
            float nearestSq = targetRange * targetRange;
            foreach (var ast in Object.FindObjectsByType<Asteroid>(FindObjectsSortMode.None))
            {
                if (ast.IsDestroyed) continue;
                float sq = (ToroidalMap.WrapPosition(ast.transform.position - myPos)).sqrMagnitude;
                if (sq < nearestSq) { nearestSq = sq; nearest = ast; }
            }
            return nearest;
        }
    }
}

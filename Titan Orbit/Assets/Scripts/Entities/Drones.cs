using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Systems;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Shared cache for drone target lookups. Refreshes periodically to avoid FindObjectsOfType every FixedUpdate per drone.
    /// Bullet threats are sourced from <see cref="CombatSystem.CopyActiveBulletSnapshots"/> (struct-based simulation),
    /// not from per-bullet NetworkObjects, since the server-authoritative bullet path has no GameObject per bullet.
    /// </summary>
    public static class DroneTargetCache
    {
        private const int MaxBulletSnapshots = 512;

        private static Starship[] cachedShips = new Starship[0];
        private static Asteroid[] cachedAsteroids = new Asteroid[0];
        private static readonly ServerBulletSnapshot[] bulletScratch = new ServerBulletSnapshot[MaxBulletSnapshots];
        private static int bulletSnapshotCount;
        private static float lastRefreshTime = -999f;
        private const float RefreshInterval = 0.25f;

        public static void RefreshIfNeeded()
        {
            if (Time.time - lastRefreshTime < RefreshInterval) return;
            lastRefreshTime = Time.time;
            cachedShips = Object.FindObjectsByType<Starship>(FindObjectsSortMode.None);
            cachedAsteroids = Object.FindObjectsByType<Asteroid>(FindObjectsSortMode.None);
            bulletSnapshotCount = CombatSystem.Instance != null
                ? CombatSystem.Instance.CopyActiveBulletSnapshots(bulletScratch)
                : 0;
        }

        public static Starship[] Ships => cachedShips;
        public static Asteroid[] Asteroids => cachedAsteroids;
        public static int BulletSnapshotCount => bulletSnapshotCount;
        public static ServerBulletSnapshot GetBulletSnapshot(int index) => bulletScratch[index];
    }

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
        private int equipmentSlotIndex = -1;
        private const float FIXED_Y = 0f;

        public float CurrentHp => currentHp.Value;
        public float MaxHp => maxHp;
        public Starship OwnerShip => ownerShip;
        public int EquipmentSlotIndex => equipmentSlotIndex;
        public bool IsDestroyed => currentHp.Value <= 0f;

        public virtual void SetOwnerShip(Starship ship)
        {
            ownerShip = ship;
            orbitAngle = Random.Range(0f, 360f);
        }

        public void SetEquipmentSlotIndex(int slotIndex)
        {
            equipmentSlotIndex = slotIndex;
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
                    NotifyOwnerEquipmentDroneLost();
                    var no = GetComponent<NetworkObject>();
                    if (no != null && no.IsSpawned) no.Despawn();
                }
                return;
            }

            if (IsServer)
            {
                if (currentHp.Value <= 0f)
                {
                    NotifyOwnerEquipmentDroneLost();
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
            Vector3 targetPos = shipPos + offset;
            Vector3 currentPos = transform.position;
            currentPos.y = FIXED_Y;
            Vector3 toTarget = targetPos - currentPos;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.01f)
            {
                Vector3 vel = toTarget.normalized * Mathf.Min(moveSpeed, toTarget.magnitude / Time.fixedDeltaTime);
                if (rb != null) rb.linearVelocity = vel;
                transform.position = currentPos + vel * Time.fixedDeltaTime;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam, ulong attackerShipNetworkId = 0)
        {
            if (ownerShip != null && attackerTeam == ownerShip.ShipTeam) return;
            float previousHp = currentHp.Value;
            currentHp.Value = Mathf.Max(0f, currentHp.Value - damage);
            if (previousHp > 0f && currentHp.Value <= 0f && attackerShipNetworkId != 0 && ScoreSystem.Instance != null)
            {
                var spawnManager = NetworkManager.Singleton != null ? NetworkManager.Singleton.SpawnManager : null;
                if (spawnManager != null && spawnManager.SpawnedObjects.TryGetValue(attackerShipNetworkId, out NetworkObject attackerObj))
                {
                    Starship attackerShip = attackerObj != null ? attackerObj.GetComponent<Starship>() : null;
                    if (attackerShip != null)
                        ScoreSystem.Instance.AwardEnemyKill(attackerShip);
                }
            }
        }

        public bool IsEnemyTeam(TeamManager.Team team)
        {
            return ownerShip != null && team != TeamManager.Team.None && team != ownerShip.ShipTeam;
        }

        /// <summary>Server: impulse away from or toward <paramref name="impactWorldPos"/>.</summary>
        public void ApplyBulletKnockbackOnServer(Vector3 impactWorldPos, float force, bool pull)
        {
            if (!IsServer || IsDestroyed || rb == null || force <= 0f) return;
            Vector3 dir = rb.position - impactWorldPos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                dir = transform.forward;
            dir.Normalize();
            if (!pull)
                dir = -dir;
            rb.AddForce(dir * force, ForceMode.Impulse);
        }

        private void NotifyOwnerEquipmentDroneLost()
        {
            if (!IsServer || ownerShip == null || equipmentSlotIndex < 0)
                return;
            ownerShip.NotifyEquipmentDroneDestroyed(equipmentSlotIndex);
            equipmentSlotIndex = -1;
        }
    }

}

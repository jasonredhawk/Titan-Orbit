using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Deterministic drone visuals under a counter-rotating hub on the ship. No per-drone NetworkObjects.
    /// The hub cancels ship yaw each frame so drones stay world-oriented while sliding to orbit slots.
    /// Server runs fire/damage; all peers integrate formation from synced equipment + ship pose.
    /// </summary>
    [RequireComponent(typeof(Starship))]
    public class DroneSwarmController : MonoBehaviour
    {
        public const float DroneBulletVisualScale = 0.58f;

        [System.Serializable]
        private struct DroneCombatTuning
        {
            [Tooltip("CombatSystem BulletBankCategories categoryName (e.g. Bullets, Laserbolt).")]
            public string bulletBankCategoryName;
            public float fireRate;
            public float bulletSpeed;
            [Tooltip("Max toroidal distance from owner ship to target before this drone may fire.")]
            public float engageRangeFromShip;
            public float bulletDetectRadius;
            public float interceptSpeedMultiplier;
        }

        [Header("Visual Prefabs")]
        [SerializeField] private GameObject fighterDronePrefab;
        [SerializeField] private GameObject shieldDronePrefab;
        [SerializeField] private GameObject miningDronePrefab;

        [Header("Movement")]
        [SerializeField] private float moveSpeed = DroneSwarmLogic.DefaultMoveSpeed;

        [Header("Orbit lag (decouple from ship rotation)")]
        [SerializeField] private float orbitCatchUpDegPerSec = 80f;
        [SerializeField] private float orbitRadiusCatchUpSpeed = 2f;

        [Header("Fighter / Mining / Shield — shared buzz")]
        [SerializeField] private float droneMarginBeyondHull = 0.7f;
        [SerializeField] private float droneOrbitRadiusMultiplier = 2f;
        [SerializeField] private float rearLateralSpread = 0.75f;
        [SerializeField] private float rearBuzzAmplitude = 0.28f;
        [SerializeField] private float rearBuzzSpeed = 3.2f;
        [SerializeField] private float fighterFacingTurnSpeed = 10f;

        [Header("Shield — side orbit & block")]
        [SerializeField] private float shieldFormationSpacing = 0.75f;
        [SerializeField] private float shieldFacingTurnSpeed = 14f;
        [Tooltip("Mesh correction if the flat face rest normal is not world up (usually 0,0,0).")]
        [SerializeField] private Vector3 shieldMeshFlatRestEuler = Vector3.zero;
        [Tooltip("Which local axis is the flat shield face normal at rest (usually 0,1,0 or 0,0,1 for GenericSpaceship).")]
        [SerializeField] private Vector3 shieldFlatFaceRestNormal = Vector3.up;

        [Header("Combat Tuning")]
        [SerializeField] private DroneCombatTuning fighterTuning = new DroneCombatTuning
        {
            bulletBankCategoryName = "Bullets",
            fireRate = 1.2f,
            bulletSpeed = 18f,
            engageRangeFromShip = 6f
        };
        [SerializeField] private DroneCombatTuning miningTuning = new DroneCombatTuning
        {
            bulletBankCategoryName = "Laserbolt",
            fireRate = 1f,
            bulletSpeed = 16f,
            engageRangeFromShip = 11f
        };
        [SerializeField] private DroneCombatTuning shieldTuning = new DroneCombatTuning
        {
            engageRangeFromShip = 16f,
            bulletDetectRadius = 12f,
            interceptSpeedMultiplier = 1.5f
        };

        private sealed class SlotVisual
        {
            public int slotIndex;
            public StoreItemType itemType;
            public GameObject instance;
            public DroneBody body;
            public Transform firePoint;
            public float lastFireTime;
            public Vector3 knockbackOffset;
            public float buzzPhase;
            public float orbitAngleDeg;
            public float orbitRadius;
            public bool orbitInitialized;
        }

        private readonly List<SlotVisual> visuals = new List<SlotVisual>(8);
        private readonly List<int> shieldSlotScratch = new List<int>(8);
        private readonly Dictionary<int, DroneSwarmPositioning.ShieldAssignment> shieldAssignments =
            new Dictionary<int, DroneSwarmPositioning.ShieldAssignment>(8);
        private Starship ownerShip;
        /// <summary>Child of ship; world rotation locked so drones do not inherit ship spin.</summary>
        private Transform droneWorldHub;
        private bool subscribed;

        public Starship OwnerShip => ownerShip;

        private void Awake()
        {
            ownerShip = GetComponent<Starship>();
            ResolvePrefabsFromStoreIfNeeded();
        }

        private void OnDestroy()
        {
            UnsubscribeEquipmentList();
            ClearVisuals();
        }

        public void OnStarshipNetworkSpawn()
        {
            EnsureDroneWorldHub();
            SubscribeEquipmentList();
            RebuildVisualsFromEquipment();
        }

        public void OnStarshipNetworkDespawn()
        {
            UnsubscribeEquipmentList();
            ClearVisuals();
        }

        private void ResolvePrefabsFromStoreIfNeeded()
        {
            if (fighterDronePrefab != null && shieldDronePrefab != null && miningDronePrefab != null)
                return;
            var store = HomePlanetStoreSystem.Instance;
            if (store == null) return;
            if (fighterDronePrefab == null) fighterDronePrefab = store.FighterDronePrefab;
            if (shieldDronePrefab == null) shieldDronePrefab = store.ShieldDronePrefab;
            if (miningDronePrefab == null) miningDronePrefab = store.MiningDronePrefab;
        }

        private void EnsureDroneWorldHub()
        {
            if (droneWorldHub != null) return;
            var existing = transform.Find("DroneSwarmWorldHub");
            if (existing == null)
                existing = transform.Find("DroneSwarmAnchor");
            if (existing != null)
            {
                existing.name = "DroneSwarmWorldHub";
                droneWorldHub = existing;
                return;
            }
            var go = new GameObject("DroneSwarmWorldHub");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            droneWorldHub = go.transform;
        }

        /// <summary>Cancel ship yaw on the hub so child drones keep world orientation.</summary>
        private void ApplyCounterRotateHub()
        {
            if (ownerShip == null || droneWorldHub == null) return;
            Vector3 hubPos = ownerShip.transform.position;
            hubPos.y = DroneSwarmLogic.FixedY;
            droneWorldHub.position = hubPos;
            droneWorldHub.rotation = Quaternion.identity;
        }

        private void SubscribeEquipmentList()
        {
            if (subscribed || ownerShip == null) return;
            var list = ownerShip.EquippedEquipmentNetworkList;
            if (list == null) return;
            list.OnListChanged += OnEquipmentListChanged;
            subscribed = true;
        }

        private void UnsubscribeEquipmentList()
        {
            if (!subscribed || ownerShip == null) return;
            var list = ownerShip.EquippedEquipmentNetworkList;
            if (list != null)
                list.OnListChanged -= OnEquipmentListChanged;
            subscribed = false;
        }

        private void OnEquipmentListChanged(NetworkListEvent<EquippedEquipmentEntry> changeEvent)
        {
            // HP updates only change remainingCharges — keep visuals alive.
            if (changeEvent.Type == NetworkListEvent<EquippedEquipmentEntry>.EventType.Value)
                return;
            RebuildVisualsFromEquipment();
        }

        private void RebuildVisualsFromEquipment()
        {
            ClearVisuals();
            if (ownerShip == null) return;
            var equipment = ownerShip.EquippedEquipment;
            if (equipment == null) return;

            EnsureDroneWorldHub();
            for (int i = 0; i < equipment.Count; i++)
            {
                var entry = equipment[i];
                if (!StoreItemData.IsDrone(entry.ItemType)) continue;
                if (entry.remainingCharges <= 0) continue;
                SpawnVisualForSlot(i, entry.ItemType);
            }
        }

        private void SpawnVisualForSlot(int slotIndex, StoreItemType itemType)
        {
            GameObject prefab = GetPrefab(itemType);
            if (prefab == null || droneWorldHub == null) return;

            EnsureDroneWorldHub();
            ApplyCounterRotateHub();

            GameObject instance = Instantiate(prefab, droneWorldHub);
            instance.name = $"{itemType}_Slot{slotIndex}";
            SanitizeDroneInstance(instance);

            Transform firePoint = instance.transform.Find("FirePoint");
            if (firePoint == null)
                firePoint = instance.transform;

            var body = instance.GetComponent<DroneBody>();
            if (body == null)
                body = instance.AddComponent<DroneBody>();
            body.Initialize(this, slotIndex);

            visuals.Add(new SlotVisual
            {
                slotIndex = slotIndex,
                itemType = itemType,
                instance = instance,
                body = body,
                firePoint = firePoint,
                lastFireTime = -999f,
                knockbackOffset = Vector3.zero,
                buzzPhase = DroneSwarmPositioning.PerDroneBuzzPhase(ownerShip.NetworkObjectId, slotIndex, itemType),
                orbitInitialized = false
            });

            if (instance != null)
            {
                DroneSwarmPositioning.GetShipBasis(ownerShip, out Vector3 shipPos, out Vector3 forward, out Vector3 right);
                float orbitRadius = GetDroneOrbitRadiusFromHull();
                float worldAngle = DroneSwarmPositioning.ShipLocalSlotToWorldAngleDeg(forward, right, 180f, orbitRadius);
                instance.transform.position = DroneSwarmPositioning.WorldPolarToWorld(shipPos, worldAngle, orbitRadius);
            }
        }

        /// <summary>World positions for colliderless bullet hit tests.</summary>
        public void EnumerateDroneHitTargets(System.Action<DroneBody, Vector3> visit)
        {
            if (visit == null || ownerShip == null || ownerShip.IsDead) return;
            for (int i = 0; i < visuals.Count; i++)
            {
                SlotVisual v = visuals[i];
                if (v.body == null || v.instance == null || v.body.IsDestroyed) continue;
                visit(v.body, v.instance.transform.position);
            }
        }

        /// <summary>Server: release equipped drones as loot in the debris field, then strip drone equipment rows.</summary>
        public void ServerDetachDronesAsLootOnDeath()
        {
            if (!IsServer || ownerShip == null) return;

            var equipment = ownerShip.EquippedEquipment;
            if (equipment == null || equipment.Count == 0) return;

            GameObject lootPrefab = HomePlanetStoreSystem.Instance != null
                ? HomePlanetStoreSystem.Instance.LootableDroneNetworkPrefab
                : null;

            CombatSystem combat = CombatSystem.Instance;
            float debrisLifetime = combat != null ? combat.DeathDebrisLifetime : 5f;
            Vector3 explosionCenter = ownerShip.transform.position;
            explosionCenter.y = DroneSwarmLogic.FixedY;

            var snapshots = new List<DetachSnapshot>(visuals.Count + 2);
            for (int i = 0; i < visuals.Count; i++)
            {
                SlotVisual v = visuals[i];
                if (v.instance == null || v.slotIndex < 0 || v.slotIndex >= equipment.Count) continue;
                var entry = equipment[v.slotIndex];
                if (!StoreItemData.IsDrone(entry.ItemType) || entry.remainingCharges <= 0) continue;
                snapshots.Add(new DetachSnapshot(
                    v.instance.transform.position,
                    entry.ItemType,
                    entry.remainingCharges));
            }

            for (int i = 0; i < equipment.Count; i++)
            {
                if (!StoreItemData.IsDrone(equipment[i].ItemType) || equipment[i].remainingCharges <= 0) continue;
                bool hasVisual = false;
                for (int v = 0; v < visuals.Count; v++)
                {
                    if (visuals[v].slotIndex == i)
                    {
                        hasVisual = true;
                        break;
                    }
                }
                if (hasVisual) continue;

                snapshots.Add(new DetachSnapshot(
                    ownerShip.transform.position - ownerShip.transform.forward * GetDroneOrbitRadiusFromHull(),
                    equipment[i].ItemType,
                    equipment[i].remainingCharges));
            }

            ownerShip.StripDroneEquipmentFromServer();
            ClearVisuals();

            if (lootPrefab == null || snapshots.Count == 0)
                return;

            for (int i = 0; i < snapshots.Count; i++)
            {
                DetachSnapshot snap = snapshots[i];
                LootableDrone.SpawnFromShipDeath(
                    lootPrefab,
                    snap.itemType,
                    snap.remainingHp,
                    ownerShip.ShipTeam,
                    ownerShip.NetworkObjectId,
                    snap.worldPosition,
                    explosionCenter,
                    debrisLifetime);
            }
        }

        public static void SanitizeLootVisualInstance(GameObject instance) => SanitizeDroneInstance(instance);

        private readonly struct DetachSnapshot
        {
            public readonly Vector3 worldPosition;
            public readonly StoreItemType itemType;
            public readonly int remainingHp;

            public DetachSnapshot(Vector3 worldPosition, StoreItemType itemType, int remainingHp)
            {
                this.worldPosition = worldPosition;
                this.itemType = itemType;
                this.remainingHp = remainingHp;
            }
        }

        private static void SanitizeDroneInstance(GameObject instance)
        {
            var networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject != null)
                Destroy(networkObject);

            var networkBehaviours = instance.GetComponentsInChildren<NetworkBehaviour>(true);
            for (int i = 0; i < networkBehaviours.Length; i++)
            {
                if (networkBehaviours[i] != null)
                    Destroy(networkBehaviours[i]);
            }

            var rigidbodies = instance.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                if (rigidbodies[i] != null)
                    Destroy(rigidbodies[i]);
            }

            var colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    Destroy(colliders[i]);
            }
        }

        private void ClearVisuals()
        {
            for (int i = 0; i < visuals.Count; i++)
            {
                if (visuals[i].instance != null)
                    Destroy(visuals[i].instance);
            }
            visuals.Clear();
        }

        private GameObject GetPrefab(StoreItemType itemType)
        {
            switch (itemType)
            {
                case StoreItemType.FighterDrone: return fighterDronePrefab;
                case StoreItemType.ShieldDrone: return shieldDronePrefab;
                case StoreItemType.MiningDrone: return miningDronePrefab;
                default: return null;
            }
        }

        private void FixedUpdate()
        {
            if (ownerShip == null || ownerShip.IsDead)
                return;

            var equipment = ownerShip.EquippedEquipment;
            if (equipment == null) return;

            ApplyCounterRotateHub();
            UpdateDroneTransforms(Time.fixedDeltaTime, applyOrbitLag: true, runCombat: true, equipment);
        }

        private void LateUpdate()
        {
            if (ownerShip == null || ownerShip.IsDead || visuals.Count == 0)
                return;

            var equipment = ownerShip.EquippedEquipment;
            if (equipment == null) return;

            ApplyCounterRotateHub();
            UpdateDroneTransforms(Time.deltaTime, applyOrbitLag: false, runCombat: false, equipment);
        }

        private void UpdateDroneTransforms(float dt, bool applyOrbitLag, bool runCombat, IReadOnlyList<EquippedEquipmentEntry> equipment)
        {
            double serverTime = GetSimulationTimeSeconds();
            if (applyOrbitLag)
                RefreshShieldAssignments(equipment);

            for (int i = visuals.Count - 1; i >= 0; i--)
            {
                SlotVisual v = visuals[i];
                if (v.slotIndex < 0 || v.slotIndex >= equipment.Count
                    || !StoreItemData.IsDrone(equipment[v.slotIndex].ItemType)
                    || equipment[v.slotIndex].remainingCharges <= 0)
                {
                    if (v.instance != null) Destroy(v.instance);
                    visuals.RemoveAt(i);
                    continue;
                }

                DroneSwarmPositioning.OrbitSlotTarget slot = ComputeOrbitSlotTarget(v, equipment, serverTime);
                DroneSwarmPositioning.GetShipBasis(ownerShip, out Vector3 shipPos, out Vector3 forward, out Vector3 right);

                if (applyOrbitLag)
                {
                    float desiredWorldAngle = DroneSwarmPositioning.ShipLocalSlotToWorldAngleDeg(forward, right, slot.angleDeg, slot.radius);

                    if (!v.orbitInitialized)
                    {
                        v.orbitAngleDeg = desiredWorldAngle;
                        v.orbitRadius = slot.radius;
                        v.orbitInitialized = true;
                    }
                    else
                    {
                        v.orbitAngleDeg = Mathf.MoveTowardsAngle(v.orbitAngleDeg, desiredWorldAngle, orbitCatchUpDegPerSec * dt);
                        v.orbitRadius = Mathf.MoveTowards(v.orbitRadius, slot.radius, orbitRadiusCatchUpSpeed * dt);
                    }

                    DecayKnockback(ref v.knockbackOffset);
                }

                Vector3 targetWorld = DroneSwarmPositioning.WorldPolarToWorld(shipPos, v.orbitAngleDeg, v.orbitRadius);
                targetWorld += slot.buzz;
                targetWorld += v.knockbackOffset;
                targetWorld.y = DroneSwarmLogic.FixedY;

                if (v.instance != null)
                {
                    v.instance.transform.position = targetWorld;
                    ApplyFacing(v, targetWorld, dt);
                }

                if (runCombat && IsServer && v.instance != null)
                    RunServerCombat(v, targetWorld, equipment);
            }
        }

        private void RefreshShieldAssignments(IReadOnlyList<EquippedEquipmentEntry> equipment)
        {
            shieldSlotScratch.Clear();
            for (int i = 0; i < visuals.Count; i++)
            {
                if (visuals[i].itemType == StoreItemType.ShieldDrone)
                    shieldSlotScratch.Add(visuals[i].slotIndex);
            }
            DroneSwarmPositioning.BuildShieldAssignments(
                equipment,
                shieldSlotScratch,
                ownerShip,
                shieldTuning.engageRangeFromShip,
                shieldAssignments);
        }

        private float GetDroneOrbitRadiusFromHull()
        {
            float mul = Mathf.Max(0.1f, droneOrbitRadiusMultiplier);
            if (ownerShip == null) return (2.5f + droneMarginBeyondHull) * mul;
            return (ownerShip.GetShipMoonDockRadiusXZ() + droneMarginBeyondHull) * mul;
        }

        private float GetDroneFormationSpacingScale()
        {
            return ownerShip != null ? ownerShip.LevelScaleFactor : 1f;
        }

        private DroneSwarmPositioning.OrbitSlotTarget ComputeOrbitSlotTarget(SlotVisual v, IReadOnlyList<EquippedEquipmentEntry> equipment, double serverTime)
        {
            int rearClusterCount = CountRearClusterDrones(equipment);
            int rearClusterOrdinal = RearClusterOrdinalAtSlot(equipment, v.slotIndex);
            float orbitRadius = GetDroneOrbitRadiusFromHull();
            float formationScale = GetDroneFormationSpacingScale();

            switch (v.itemType)
            {
                case StoreItemType.FighterDrone:
                case StoreItemType.MiningDrone:
                    return DroneSwarmPositioning.ComputeRearEscortOrbitSlot(
                        ownerShip,
                        v.slotIndex,
                        rearClusterOrdinal,
                        rearClusterCount,
                        orbitRadius,
                        rearLateralSpread * formationScale,
                        rearBuzzAmplitude,
                        rearBuzzSpeed,
                        serverTime,
                        v.buzzPhase);

                case StoreItemType.ShieldDrone:
                {
                    int shieldOrd = OrdinalOfTypeAtSlot(equipment, v.slotIndex, StoreItemType.ShieldDrone);
                    int shieldCount = CountDronesOfType(equipment, StoreItemType.ShieldDrone);

                    if (shieldAssignments.TryGetValue(v.slotIndex, out DroneSwarmPositioning.ShieldAssignment assign))
                    {
                        Starship enemy = DroneSwarmPositioning.FindShipByInstanceId(assign.enemyInstanceId);
                        if (enemy != null && !enemy.IsDead)
                        {
                            return DroneSwarmPositioning.ComputeShieldBlockOrbitSlot(
                                ownerShip,
                                enemy,
                                v.slotIndex,
                                assign.indexOnEnemy,
                                assign.countOnEnemy,
                                orbitRadius,
                                shieldFormationSpacing * formationScale,
                                rearBuzzAmplitude,
                                rearBuzzSpeed,
                                serverTime,
                                v.buzzPhase);
                        }
                    }

                    return DroneSwarmPositioning.ComputeShieldSideOrbitSlot(
                        ownerShip,
                        v.slotIndex,
                        shieldOrd,
                        shieldCount,
                        orbitRadius,
                        rearBuzzAmplitude,
                        rearBuzzSpeed,
                        serverTime,
                        v.buzzPhase);
                }
            }

            return new DroneSwarmPositioning.OrbitSlotTarget { angleDeg = 180f, radius = orbitRadius, buzz = Vector3.zero };
        }

        private Vector3 ComputeTargetWorldPosition(SlotVisual v, IReadOnlyList<EquippedEquipmentEntry> equipment, double serverTime)
        {
            DroneSwarmPositioning.OrbitSlotTarget slot = ComputeOrbitSlotTarget(v, equipment, serverTime);
            DroneSwarmPositioning.GetShipBasis(ownerShip, out Vector3 shipPos, out Vector3 forward, out Vector3 right);
            float angle = v.orbitInitialized ? v.orbitAngleDeg : DroneSwarmPositioning.ShipLocalSlotToWorldAngleDeg(forward, right, slot.angleDeg, slot.radius);
            float radius = v.orbitInitialized ? v.orbitRadius : slot.radius;
            return DroneSwarmPositioning.WorldPolarToWorld(shipPos, angle, radius) + slot.buzz;
        }

        private static int CountRearClusterDrones(IReadOnlyList<EquippedEquipmentEntry> equipment)
        {
            int n = 0;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (equipment[i].remainingCharges <= 0) continue;
                StoreItemType t = equipment[i].ItemType;
                if (t == StoreItemType.FighterDrone || t == StoreItemType.MiningDrone)
                    n++;
            }
            return n;
        }

        private static int RearClusterOrdinalAtSlot(IReadOnlyList<EquippedEquipmentEntry> equipment, int slotIndex)
        {
            int ord = 0;
            for (int i = 0; i <= slotIndex && i < equipment.Count; i++)
            {
                if (equipment[i].remainingCharges <= 0) continue;
                StoreItemType t = equipment[i].ItemType;
                if (t != StoreItemType.FighterDrone && t != StoreItemType.MiningDrone) continue;
                if (i == slotIndex) return ord;
                ord++;
            }
            return 0;
        }

        private static int CountDronesOfType(IReadOnlyList<EquippedEquipmentEntry> equipment, StoreItemType type)
        {
            int n = 0;
            for (int i = 0; i < equipment.Count; i++)
            {
                if (equipment[i].ItemType == type && equipment[i].remainingCharges > 0)
                    n++;
            }
            return n;
        }

        private static int OrdinalOfTypeAtSlot(IReadOnlyList<EquippedEquipmentEntry> equipment, int slotIndex, StoreItemType type)
        {
            int ord = 0;
            for (int i = 0; i <= slotIndex && i < equipment.Count; i++)
            {
                if (equipment[i].ItemType != type || equipment[i].remainingCharges <= 0) continue;
                if (i == slotIndex) return ord;
                ord++;
            }
            return 0;
        }

        private static void DecayKnockback(ref Vector3 knockback)
        {
            if (knockback.sqrMagnitude < 0.0001f) return;
            knockback = Vector3.Lerp(knockback, Vector3.zero, Time.fixedDeltaTime * 4f);
            knockback.y = 0f;
        }

        private void ApplyFacing(SlotVisual v, Vector3 worldPos, float deltaTime)
        {
            if (v.instance == null) return;

            Quaternion targetRot;
            float turnSpeed = 12f;

            if (v.itemType == StoreItemType.ShieldDrone)
            {
                Quaternion meshRest = Quaternion.Euler(shieldMeshFlatRestEuler);
                Vector3 flatNormal = shieldFlatFaceRestNormal;
                Starship threat = ResolveShieldFacingThreat(v.slotIndex);
                if (threat != null)
                {
                    targetRot = DroneSwarmPositioning.ComputeShieldFaceEnemyRotation(worldPos, threat.transform.position, flatNormal) * meshRest;
                    turnSpeed = shieldFacingTurnSpeed;
                    v.instance.transform.rotation = targetRot;
                    return;
                }

                targetRot = DroneSwarmPositioning.ComputeShieldFaceOutwardRotation(ownerShip.transform.position, worldPos, flatNormal) * meshRest;
                turnSpeed = shieldFacingTurnSpeed * 0.75f;
            }
            else
            {
                Vector3 lookDir = ownerShip.transform.forward;
                lookDir.y = 0f;
                if (v.itemType == StoreItemType.FighterDrone)
                {
                    Starship target = DroneSwarmLogic.FindNearestEnemyShipNearOwner(ownerShip, fighterTuning.engageRangeFromShip);
                    if (target != null)
                    {
                        lookDir = target.transform.position - worldPos;
                        lookDir.y = 0f;
                    }
                    turnSpeed = fighterFacingTurnSpeed;
                }
                else if (v.itemType == StoreItemType.MiningDrone)
                {
                    Asteroid target = DroneSwarmLogic.FindNearestAsteroidNearOwner(ownerShip, miningTuning.engageRangeFromShip);
                    if (target != null)
                    {
                        lookDir = target.transform.position - worldPos;
                        lookDir.y = 0f;
                    }
                }

                if (lookDir.sqrMagnitude < 0.01f)
                    return;
                targetRot = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
            }

            v.instance.transform.rotation = Quaternion.Slerp(
                v.instance.transform.rotation,
                targetRot,
                Mathf.Clamp01(turnSpeed * deltaTime));
        }

        private Starship ResolveShieldFacingThreat(int shieldSlotIndex)
        {
            if (!shieldAssignments.TryGetValue(shieldSlotIndex, out DroneSwarmPositioning.ShieldAssignment assign))
                return null;
            Starship enemy = DroneSwarmPositioning.FindShipByInstanceId(assign.enemyInstanceId);
            if (enemy != null && !enemy.IsDead)
                return enemy;
            return null;
        }

        private void RunServerCombat(SlotVisual v, Vector3 worldPos, IReadOnlyList<EquippedEquipmentEntry> equipment)
        {
            if (ownerShip == null || CombatSystem.Instance == null || v.instance == null) return;
            Vector3 firePos = worldPos;
            firePos.y = DroneSwarmLogic.FixedY;

            switch (v.itemType)
            {
                case StoreItemType.FighterDrone:
                    TryFireAtShip(v, firePos, fighterTuning);
                    break;
                case StoreItemType.MiningDrone:
                    TryFireAtAsteroid(v, firePos, miningTuning);
                    break;
            }
        }

        private void TryFireAtShip(SlotVisual v, Vector3 firePos, DroneCombatTuning tuning)
        {
            Starship target = DroneSwarmLogic.FindNearestEnemyShipNearOwner(ownerShip, tuning.engageRangeFromShip);
            if (target == null || Time.time - v.lastFireTime < 1f / tuning.fireRate) return;

            Vector3 dir = target.transform.position - firePos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();
            TrySpawnDroneBullet(firePos, dir, tuning);
            v.lastFireTime = Time.time;
        }

        private void TryFireAtAsteroid(SlotVisual v, Vector3 firePos, DroneCombatTuning tuning)
        {
            Asteroid target = DroneSwarmLogic.FindNearestAsteroidNearOwner(ownerShip, tuning.engageRangeFromShip);
            if (target == null || target.IsDestroyed || Time.time - v.lastFireTime < 1f / tuning.fireRate) return;

            Vector3 dir = target.transform.position - firePos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();
            TrySpawnDroneBullet(firePos, dir, tuning);
            v.lastFireTime = Time.time;
        }

        private void TrySpawnDroneBullet(Vector3 firePos, Vector3 dir, DroneCombatTuning tuning)
        {
            CombatSystem combat = CombatSystem.Instance;
            if (combat == null || ownerShip == null) return;

            int bankIndex = ResolveDroneBulletBankIndex(combat, tuning.bulletBankCategoryName);
            float damage = ownerShip.GetDroneBulletDamage(bankIndex);

            Vector3 shipVel = ownerShip.GetComponent<Rigidbody>() != null
                ? ownerShip.GetComponent<Rigidbody>().linearVelocity
                : Vector3.zero;
            shipVel.y = 0f;

            combat.TrySpawnBulletOnServer(
                firePos,
                dir,
                tuning.bulletSpeed,
                damage,
                ownerShip.ShipTeam,
                ownerShip.NetworkObjectId,
                DroneBulletVisualScale,
                bulletShapeIndex: 0,
                shipVel,
                bankIndex,
                BulletSpawnPayload.BulletSpawnFlagDrone);

            ownerShip.ServerPlayDroneMuzzleVfx(firePos, dir, bankIndex, damage);
        }

        private static int ResolveDroneBulletBankIndex(CombatSystem combat, string categoryName)
        {
            if (combat == null) return 0;
            if (!string.IsNullOrWhiteSpace(categoryName)
                && combat.TryGetBulletBankIndexByCategoryName(categoryName, out int idx))
                return idx;
            return combat.BulletPrefabBankCount > 0 ? 0 : -1;
        }

        private double GetSimulationTimeSeconds()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return nm.ServerTime.Time;
            return Time.timeAsDouble;
        }

        private bool IsServer =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        public bool IsSlotDestroyed(int slotIndex)
        {
            if (ownerShip == null) return true;
            var equipment = ownerShip.EquippedEquipment;
            if (equipment == null || slotIndex < 0 || slotIndex >= equipment.Count) return true;
            if (!StoreItemData.IsDrone(equipment[slotIndex].ItemType)) return true;
            return equipment[slotIndex].remainingCharges <= 0;
        }

        public bool IsEnemyTeam(TeamManager.Team team)
        {
            return ownerShip != null && team != TeamManager.Team.None && team != ownerShip.ShipTeam;
        }

        public void ApplyDamageFromBullet(int slotIndex, float damage, TeamManager.Team attackerTeam, ulong attackerShipNetworkId, Vector3 impactWorldPos)
        {
            if (!IsServer || ownerShip == null) return;
            if (!IsEnemyTeam(attackerTeam)) return;
            ownerShip.ApplyDroneSlotDamage(slotIndex, damage, attackerTeam, attackerShipNetworkId);

            for (int i = 0; i < visuals.Count; i++)
            {
                if (visuals[i].slotIndex != slotIndex) continue;
                Vector3 dir = visuals[i].instance != null
                    ? visuals[i].instance.transform.position - impactWorldPos
                    : Vector3.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
                visuals[i].knockbackOffset += dir.normalized * 0.35f;
                break;
            }
        }

        public void ApplyKnockbackFromBullet(int slotIndex, Vector3 impactWorldPos, float force, bool pull)
        {
            if (!IsServer) return;
            for (int i = 0; i < visuals.Count; i++)
            {
                if (visuals[i].slotIndex != slotIndex) continue;
                Vector3 dir = visuals[i].instance != null
                    ? visuals[i].instance.transform.position - impactWorldPos
                    : Vector3.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
                dir.Normalize();
                if (!pull) dir = -dir;
                visuals[i].knockbackOffset += dir * Mathf.Clamp(force * 0.02f, 0.05f, 1.2f);
                break;
            }
        }
    }
}

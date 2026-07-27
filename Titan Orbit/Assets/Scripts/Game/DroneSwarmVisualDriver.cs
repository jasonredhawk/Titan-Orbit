using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only hybrid: watches the local ship's <see cref="EquippedEquipmentElement"/> buffer
    /// and Instantiates Fighter/Shield/Mining drone prefabs that orbit the ship presentation pose.
    /// Replaces the deleted NGO <c>DroneSwarmController</c> visual path (combat/loot not restored).
    /// Prefers <see cref="ShipDisplayPose"/> so drones follow smoothed presentation, not raw sim.
    /// <para>
    /// [HYBRID] Mesh Y uses <see cref="DroneSwarmLogic.PresentationLiftY"/> so escorts clear the hull,
    /// but any future combat / shield math must stay on <see cref="DroneSwarmLogic.FixedY"/> (0).
    /// </para>
    /// </summary>
    public sealed class DroneSwarmVisualDriver : MonoBehaviour
    {
        const string FighterPath = "Assets/Prefabs/FighterDrone.prefab";
        const string ShieldPath = "Assets/Prefabs/ShieldDrone.prefab";
        const string MiningPath = "Assets/Prefabs/MiningDrone.prefab";

        /// <summary>
        /// Margin beyond hull radius before applying orbit multiplier.
        /// Legacy NGO used the same idea via moon-dock / collider radius + margin.
        /// </summary>
        const float DroneMarginBeyondHull = 1.0f;

        /// <summary>
        /// Multiplies (hull radius + margin) for escort ring size.
        /// Presentation-scaled hulls are tiny (~0.13), so we also floor with
        /// <see cref="DroneSwarmLogic.DefaultOrbitRadius"/> so drones leave the mesh.
        /// </summary>
        const float DroneOrbitRadiusMultiplier = 2.75f;

        /// <summary>Degrees/sec catch-up so drones lag ship yaw slightly (legacy feel).</summary>
        const float OrbitCatchUpDegPerSec = 80f;

        /// <summary>How fast radius eases when hull scale changes.</summary>
        const float OrbitRadiusCatchUpSpeed = 2f;

        /// <summary>Buzz wobble amplitude in world units (XZ + light Y for presentation).</summary>
        const float BuzzAmplitude = 0.28f;

        /// <summary>Vertical share of buzz — cosmetic only; never used as combat height.</summary>
        const float BuzzVerticalFraction = 0.4f;

        /// <summary>Buzz wobble frequency.</summary>
        const float BuzzSpeed = 3.2f;

        struct SlotVisual
        {
            public int SlotIndex;
            public StoreItemType ItemType;
            public GameObject Instance;
            public float OrbitAngleDeg;
            public float OrbitRadius;
            public bool OrbitInitialized;
            public float BuzzPhase;
        }

        static DroneSwarmVisualDriver s_instance;

        GameObject _fighterPrefab;
        GameObject _shieldPrefab;
        GameObject _miningPrefab;
        Transform _hub;
        readonly List<SlotVisual> _visuals = new List<SlotVisual>(8);
        readonly List<int> _droneSlotsScratch = new List<int>(8);
        int _lastEquipmentFingerprint = int.MinValue;
        int _lastShipNetworkId;

        /// <summary>
        /// [UNITY] After scene load — spawn a DontDestroyOnLoad driver so drones work without scene wiring.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (TitanOrbit.NetCode.TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation() == false)
                return;

            if (s_instance != null)
                return;

            var go = new GameObject("DroneSwarmVisualDriver");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<DroneSwarmVisualDriver>();
#endif
        }

        void Awake()
        {
            // --- Prefab resolve ---
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            _fighterPrefab = LoadPrefab(FighterPath, "FighterDrone");
            _shieldPrefab = LoadPrefab(ShieldPath, "ShieldDrone");
            _miningPrefab = LoadPrefab(MiningPath, "MiningDrone");
            EnsureHub();
        }

        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            ClearVisuals();
        }

        void LateUpdate()
        {
            // --- Presentation-frame orbit (after ShipDisplayPose publish) ---
            // [TITAN-ORBIT] Skip during TeamChoice Instantiates — no ship entity queries.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            if (!TryGetLocalShipPose(out Vector3 shipPos, out Quaternion shipRot, out float shipScale, out int networkId))
            {
                if (_visuals.Count > 0)
                    ClearVisuals();
                return;
            }

            _lastShipNetworkId = networkId;
            SyncVisualsToEquipment();
            if (_visuals.Count == 0)
                return;

            // Hub follows ship on the sim plane; identity rotation so drones keep world orientation.
            // [TITAN-ORBIT] Hub stays at FixedY — each drone mesh adds PresentationLiftY itself.
            if (_hub != null)
            {
                shipPos.y = DroneSwarmLogic.FixedY;
                _hub.position = shipPos;
                _hub.rotation = Quaternion.identity;
            }

            UpdateOrbitTransforms(shipPos, shipRot, shipScale, Time.deltaTime);
        }

        /// <summary>Loads drone mesh prefab (Editor AssetDatabase path, else Resources name).</summary>
        static GameObject LoadPrefab(string assetPath, string resourcesName)
        {
#if UNITY_EDITOR
            var fromEditor = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (fromEditor != null)
                return fromEditor;
#endif
            var fromResources = Resources.Load<GameObject>(resourcesName);
            if (fromResources != null)
                return fromResources;
            return Resources.Load<GameObject>("Prefabs/" + resourcesName);
        }

        void EnsureHub()
        {
            if (_hub != null)
                return;
            var go = new GameObject("DroneSwarmWorldHub");
            go.transform.SetParent(transform, false);
            _hub = go.transform;
        }

        bool TryGetLocalShipPose(out Vector3 position, out Quaternion rotation, out float scale, out int networkId)
        {
            position = default;
            rotation = Quaternion.identity;
            scale = 1f;
            networkId = EcsGameBridge.GetLocalNetworkId();

            // Prefer smoothed presentation pose for visuals.
            if (ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                rotation = ShipDisplayPose.LocalRotation;
            }
            else if (!EcsGameBridge.TryGetLocalShipPresentationPosition(out position))
            {
                return false;
            }

            if (EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                scale = BodyCollisionMath.GetShipTierScale(Mathf.Max(1, ship.ShipLevel));
                if (ship.IsDead || ship.AwaitingTeamSelection)
                    return false;
            }

            return networkId > 0 || ShipDisplayPose.HasLocalPose;
        }

        /// <summary>
        /// Diffs equipped drone slots against live visuals. Rebuilds only when the equipment fingerprint changes.
        /// </summary>
        void SyncVisualsToEquipment()
        {
            // --- Read equipment buffer (Local Host prefers ServerWorld for instant post-buy spawn) ---
            if (!TryReadDroneSlots(out int fingerprint))
            {
                if (_visuals.Count > 0)
                    ClearVisuals();
                _lastEquipmentFingerprint = int.MinValue;
                return;
            }

            if (fingerprint == _lastEquipmentFingerprint)
                return;

            _lastEquipmentFingerprint = fingerprint;
            RebuildVisualsFromScratch();
        }

        bool TryReadDroneSlots(out int fingerprint)
        {
            fingerprint = 0;
            _droneSlotsScratch.Clear();

            World world = null;
            if (EcsGameBridge.IsLocalHost() &&
                EcsGameBridge.ServerWorld != null &&
                EcsGameBridge.ServerWorld.IsCreated)
            {
                world = EcsGameBridge.ServerWorld;
            }
            else
            {
                world = EcsGameBridge.GetLocalPlayerShipWorld() ?? EcsGameBridge.ClientWorld;
            }

            if (world == null || !world.IsCreated)
                return false;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out var shipEntity))
                return false;

            var em = world.EntityManager;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return false;

            var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            unchecked
            {
                fingerprint = 17 + buf.Length;
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    fingerprint = fingerprint * 31 + e.ItemType;
                    fingerprint = fingerprint * 31 + e.RemainingCharges;
                    if (!StoreItemData.IsDrone((StoreItemType)e.ItemType))
                        continue;
                    if (e.RemainingCharges <= 0)
                        continue;
                    _droneSlotsScratch.Add(i);
                }
            }

            return _droneSlotsScratch.Count > 0;
        }

        void RebuildVisualsFromScratch()
        {
            ClearVisuals();
            EnsureHub();

            for (int i = 0; i < _droneSlotsScratch.Count; i++)
            {
                int slotIndex = _droneSlotsScratch[i];
                // Re-read type from buffer — scratch only stores indices.
                if (!TryGetEquipmentAt(slotIndex, out StoreItemType itemType, out int charges))
                    continue;
                if (!StoreItemData.IsDrone(itemType) || charges <= 0)
                    continue;

                SpawnVisual(slotIndex, itemType);
            }
        }

        bool TryGetEquipmentAt(int slotIndex, out StoreItemType itemType, out int charges)
        {
            itemType = default;
            charges = 0;

            World world = EcsGameBridge.IsLocalHost() && EcsGameBridge.ServerWorld != null && EcsGameBridge.ServerWorld.IsCreated
                ? EcsGameBridge.ServerWorld
                : EcsGameBridge.GetLocalPlayerShipWorld() ?? EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out var shipEntity))
                return false;

            var em = world.EntityManager;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return false;
            var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            if (slotIndex < 0 || slotIndex >= buf.Length)
                return false;

            itemType = (StoreItemType)buf[slotIndex].ItemType;
            charges = buf[slotIndex].RemainingCharges;
            return true;
        }

        void SpawnVisual(int slotIndex, StoreItemType itemType)
        {
            GameObject prefab = GetPrefab(itemType);
            if (prefab == null || _hub == null)
                return;

            var instance = Instantiate(prefab, _hub);
            instance.name = $"{itemType}_Slot{slotIndex}";
            StripPhysicsAndNetwork(instance);

            var visual = new SlotVisual
            {
                SlotIndex = slotIndex,
                ItemType = itemType,
                Instance = instance,
                OrbitInitialized = false,
                BuzzPhase = DroneSwarmLogic.DeterministicBasePhaseRad(_lastShipNetworkId, slotIndex, itemType),
            };
            _visuals.Add(visual);
        }

        GameObject GetPrefab(StoreItemType itemType)
        {
            switch (itemType)
            {
                case StoreItemType.FighterDrone: return _fighterPrefab;
                case StoreItemType.ShieldDrone: return _shieldPrefab;
                case StoreItemType.MiningDrone: return _miningPrefab;
                default: return null;
            }
        }

        static void StripPhysicsAndNetwork(GameObject instance)
        {
            // --- Cosmetic-only proxy ---
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

        void ClearVisuals()
        {
            for (int i = 0; i < _visuals.Count; i++)
            {
                if (_visuals[i].Instance != null)
                    Destroy(_visuals[i].Instance);
            }
            _visuals.Clear();
        }

        /// <summary>
        /// Places each drone on a rear-biased escort ring around the ship, with planar buzz
        /// and a lifted presentation Y. Combat height stays <see cref="DroneSwarmLogic.FixedY"/>.
        /// </summary>
        /// <param name="shipPos">Ship presentation position (Y forced to FixedY by caller).</param>
        /// <param name="shipRot">Ship presentation yaw used for ring facing.</param>
        /// <param name="shipScale">ECS tier scale (feeds hull radius).</param>
        /// <param name="dt">Frame delta for orbit catch-up lerps.</param>
        void UpdateOrbitTransforms(Vector3 shipPos, Quaternion shipRot, float shipScale, float dt)
        {
            // --- Ship basis on the XZ plane ---
            // [TITAN-ORBIT] Ignore pitch/roll so escorts stay planar even if presentation leans.
            Vector3 forward = shipRot * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            // --- Escort ring radius ---
            // Hull radius from BodyCollisionMath is presentation-scaled and often tiny (~0.13).
            // Floor with DefaultOrbitRadius so drones clear the visible mesh (legacy ring was ~3).
            float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipScale);
            float targetRadius = Mathf.Max(
                DroneSwarmLogic.DefaultOrbitRadius,
                (hullRadius + DroneMarginBeyondHull) * DroneOrbitRadiusMultiplier);
            float time = Time.time;

            int droneCount = _visuals.Count;
            for (int i = 0; i < _visuals.Count; i++)
            {
                var v = _visuals[i];
                if (v.Instance == null)
                    continue;

                // --- Slot angle (rear cluster, legacy escort feel) ---
                // 180° = behind the ship; neighbors fan ±70° across the cluster.
                float localAngleDeg = 180f + (droneCount > 1
                    ? (i - (droneCount - 1) * 0.5f) * (70f / Mathf.Max(1, droneCount - 1))
                    : 0f);
                float desiredWorldAngle = LocalSlotToWorldAngleDeg(forward, right, localAngleDeg);

                if (!v.OrbitInitialized)
                {
                    // Snap on first frame so drones do not crawl out from the hull center.
                    v.OrbitAngleDeg = desiredWorldAngle;
                    v.OrbitRadius = targetRadius;
                    v.OrbitInitialized = true;
                }
                else
                {
                    v.OrbitAngleDeg = Mathf.MoveTowardsAngle(
                        v.OrbitAngleDeg, desiredWorldAngle, OrbitCatchUpDegPerSec * dt);
                    v.OrbitRadius = Mathf.MoveTowards(
                        v.OrbitRadius, targetRadius, OrbitRadiusCatchUpSpeed * dt);
                }

                // --- Polar offset on FixedY plane (sim-correct XZ) ---
                float rad = v.OrbitAngleDeg * Mathf.Deg2Rad;
                Vector3 world = shipPos
                    + new Vector3(Mathf.Sin(rad) * v.OrbitRadius, 0f, Mathf.Cos(rad) * v.OrbitRadius);

                // --- Planar buzz so drones feel alive ---
                float buzz = v.BuzzPhase + i * 0.37f;
                world += forward * (Mathf.Sin(time * BuzzSpeed + buzz) * BuzzAmplitude)
                    + right * (Mathf.Cos(time * BuzzSpeed * 1.17f + buzz * 1.3f) * BuzzAmplitude * 0.55f);

                // --- Presentation height only ---
                // [HYBRID] Lift + light vertical buzz for camera readability.
                // When combat returns: muzzle / hit sphere / shield center = FixedY, not this Y.
                float buzzY = Mathf.Sin(time * BuzzSpeed * 0.91f + buzz * 1.1f)
                    * BuzzAmplitude * BuzzVerticalFraction;
                world.y = DroneSwarmLogic.PresentationWorldY(buzzY);

                v.Instance.transform.position = world;

                // --- Facing ---
                // Fighters/miners look along ship forward; shields face outward from the hull.
                Vector3 face = forward;
                if (v.ItemType == StoreItemType.ShieldDrone)
                {
                    face = world - shipPos;
                    face.y = 0f;
                    if (face.sqrMagnitude > 0.01f)
                        face.Normalize();
                }
                if (face.sqrMagnitude > 0.01f)
                {
                    var targetRot = Quaternion.LookRotation(face, Vector3.up);
                    v.Instance.transform.rotation = Quaternion.Slerp(
                        v.Instance.transform.rotation, targetRot, 1f - Mathf.Exp(-10f * dt));
                }

                _visuals[i] = v;
            }
        }

        /// <summary>
        /// Converts a ship-local slot angle (0 = forward, 180 = aft) into a world XZ polar angle
        /// suitable for <c>atan2(x,z)</c> placement around the ship.
        /// </summary>
        static float LocalSlotToWorldAngleDeg(Vector3 forward, Vector3 right, float localAngleDeg)
        {
            float rad = localAngleDeg * Mathf.Deg2Rad;
            Vector3 offset = forward * Mathf.Cos(rad) + right * Mathf.Sin(rad);
            return Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        }
    }
}

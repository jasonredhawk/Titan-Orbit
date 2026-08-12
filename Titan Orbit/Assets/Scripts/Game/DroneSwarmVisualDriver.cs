using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only hybrid: Instantiates Fighter/Shield/Mining drone prefabs for <b>all visible
    /// ships</b> from replicated ship pose + <see cref="EquippedEquipmentElement"/> — no drone
    /// ghosts (bandwidth-safe). Poses come from <see cref="DroneSwarmPositioning.EvaluateSlotPose"/>
    /// on the shared <see cref="DroneSwarmSimTime"/> clock so meshes match server muzzle origins.
    /// <para>
    /// [HYBRID] Mesh Y uses <see cref="DroneSwarmLogic.PresentationLiftY"/> above the ship hub.
    /// Fire / hit math stays on <see cref="DroneSwarmLogic.FixedY"/>.
    /// </para>
    /// <para>
    /// Combat fire is server-only (<c>DroneSwarmCombatSystem</c>). Shield block walls use the same
    /// sorted-enemy assignment as server hit-scan. Mesh scale =
    /// prefab localScale × <see cref="StoreItemData.GetDroneVisualScale"/> (L6 mul = 1.0).
    /// </para>
    /// </summary>
    public sealed class DroneSwarmVisualDriver : MonoBehaviour
    {
        const string FighterPath = "Assets/Prefabs/FighterDrone.prefab";
        const string ShieldPath = "Assets/Prefabs/ShieldDrone.prefab";
        const string MiningPath = "Assets/Prefabs/MiningDrone.prefab";

        /// <summary>Vertical share of buzz — cosmetic only (never fed to combat).</summary>
        const float BuzzVerticalFraction = 0.35f;

        /// <summary>Facing turn speed for fighters / miners when idle (no target).</summary>
        const float FighterFacingTurnSpeed = 10f;

        /// <summary>Facing turn speed for shields.</summary>
        const float ShieldFacingTurnSpeed = 14f;

        /// <summary>Only draw remote swarms within this toroidal distance of the local ship.</summary>
        const float RemoteVisualRange = 48f;

        /// <summary>Refresh mining asteroid aim this often (frames) — full rock gathers are expensive.</summary>
        const int AsteroidAimRefreshFrames = 20;

        /// <summary>One spawned mesh under a ship hub.</summary>
        struct SlotVisual
        {
            public int SlotIndex;
            public StoreItemType ItemType;
            /// <summary>Purchase level — drives <see cref="StoreItemData.GetDroneVisualScale"/>.</summary>
            public int ItemLevel;
            /// <summary>
            /// Prefab root localScale captured at spawn (before level mul).
            /// [TITAN-ORBIT] Level mul is applied on top of this — never replace with Vector3.one.
            /// </summary>
            public Vector3 PrefabLocalScale;
            public GameObject Instance;
            public float BuzzPhase;
        }

        /// <summary>
        /// Per-ship drone group: hub + meshes derived from that ship's equipment layout.
        /// Layout fingerprint ignores HP magnitude so charge ticks do not Destroy/Instantiate.
        /// </summary>
        sealed class ShipDroneGroup
        {
            public int NetworkId;
            public Transform Hub;
            public readonly List<SlotVisual> Visuals = new List<SlotVisual>(8);
            /// <summary>Types + which slots are alive (charges &gt; 0) — not HP values.</summary>
            public int LayoutFingerprint = int.MinValue;
        }

        static DroneSwarmVisualDriver s_instance;

        GameObject _fighterPrefab;
        GameObject _shieldPrefab;
        GameObject _miningPrefab;

        readonly Dictionary<int, ShipDroneGroup> _groupsByNetId = new Dictionary<int, ShipDroneGroup>(8);
        readonly List<int> _aliveNetIdsScratch = new List<int>(8);
        readonly List<int> _removeNetIdsScratch = new List<int>(8);

        readonly List<int> _rearSlotsScratch = new List<int>(8);
        readonly List<int> _shieldSlotsScratch = new List<int>(8);
        readonly List<int> _enemyNetIdsScratch = new List<int>(16);
        readonly Dictionary<int, Vector3> _enemyPosByNetId = new Dictionary<int, Vector3>(16);
        readonly Dictionary<int, DroneSwarmPositioning.ShieldAssignment> _shieldAssignments =
            new Dictionary<int, DroneSwarmPositioning.ShieldAssignment>(8);
        readonly Dictionary<int, TeamId> _enemyTeamByNetId = new Dictionary<int, TeamId>(16);

        /// <summary>Scratch for hybrid asteroid proxy keys (quarantine-safe mining aim).</summary>
        readonly List<Entity> _asteroidProxyScratch = new List<Entity>(512);

        /// <summary>Nearest asteroid planar pos cached for local mining aim (throttled).</summary>
        Vector3 _cachedNearestAsteroid;
        bool _hasCachedNearestAsteroid;
        int _asteroidCacheFrame = -999;

        World _cachedQueryWorld;
        EntityQuery _shipQuery;
        EntityQuery _asteroidQuery;
        bool _queriesCreated;

        // [TITAN-ORBIT] 0 = unset until session meta / MapState arrives — never invent 1000×1000.
        float _mapW;
        float _mapH;

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
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            ResolvePrefabs();
        }

        /// <summary>
        /// Prefers <see cref="DroneSwarmPrefabCatalog"/> (Resources) so Windows players get meshes.
        /// Falls back to Resources names / Editor AssetDatabase paths.
        /// </summary>
        void ResolvePrefabs()
        {
            var catalog = DroneSwarmPrefabCatalog.LoadDefault();
            if (catalog != null)
            {
                _fighterPrefab = catalog.FighterDrone;
                _shieldPrefab = catalog.ShieldDrone;
                _miningPrefab = catalog.MiningDrone;
            }

            if (_fighterPrefab == null)
                _fighterPrefab = LoadPrefab(FighterPath, "FighterDrone");
            if (_shieldPrefab == null)
                _shieldPrefab = LoadPrefab(ShieldPath, "ShieldDrone");
            if (_miningPrefab == null)
                _miningPrefab = LoadPrefab(MiningPath, "MiningDrone");
        }

        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            DisposeQueries();
            ClearAllGroups();
        }

        void DisposeQueries()
        {
            if (_queriesCreated)
            {
                if (_shipQuery != default)
                    _shipQuery.Dispose();
                if (_asteroidQuery != default)
                    _asteroidQuery.Dispose();
                _queriesCreated = false;
            }
            _cachedQueryWorld = null;
        }

        void EnsureQueries(World world)
        {
            if (world == null || !world.IsCreated)
                return;
            if (_queriesCreated && _cachedQueryWorld == world)
                return;

            DisposeQueries();
            var em = world.EntityManager;
            _shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            _asteroidQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _cachedQueryWorld = world;
            _queriesCreated = true;
        }

        void LateUpdate()
        {
            // [TITAN-ORBIT] Skip entity buffer reads during TeamChoice Instantiates.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            RefreshMapSize();
            if (_mapW < 100f || _mapH < 100f)
                return;

            PublishSimTimeFromNetwork();
            double timeSeconds = DroneSwarmSimTime.ResolveOrFallback(Time.timeAsDouble);

            World world = ResolveShipWorld();
            if (world == null || !world.IsCreated)
            {
                ClearAllGroups();
                return;
            }

            EnsureQueries(world);
            if (!_queriesCreated)
                return;

            var em = world.EntityManager;
            using var shipEntities = _shipQuery.ToEntityArray(Allocator.Temp);

            // Local ship pose for remote distance cull + throttled mining aim.
            Vector3 localPos = default;
            bool hasLocalPos = ShipDisplayPose.HasLocalPose;
            if (hasLocalPos)
                localPos = ShipDisplayPose.LocalPosition;
            else
                hasLocalPos = EcsGameBridge.TryGetLocalShipPresentationPosition(out localPos);
            int localNetId = EcsGameBridge.GetLocalNetworkId();

            // Build global enemy cache once (shield assign + fighter facing).
            RefreshGlobalEnemyCache(em, shipEntities);

            // Throttled asteroid aim for local mining facing only (never per-drone).
            bool anyMiningVisible = false;
            // (filled while iterating — refresh after first pass flag; do before orbit update)

            _aliveNetIdsScratch.Clear();
            float dt = Time.deltaTime;

            // Detect if we need asteroid aim this frame.
            for (int i = 0; i < shipEntities.Length && !anyMiningVisible; i++)
            {
                Entity shipEntity = shipEntities[i];
                if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                    continue;
                var ghost = em.GetComponentData<GhostOwner>(shipEntity);
                if (localNetId > 0 && ghost.NetworkId != localNetId)
                    continue; // only care about local mining for facing cache
                var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                for (int b = 0; b < buf.Length; b++)
                {
                    if ((StoreItemType)buf[b].ItemType == StoreItemType.MiningDrone &&
                        buf[b].RemainingCharges > 0)
                    {
                        anyMiningVisible = true;
                        break;
                    }
                }
            }

            if (anyMiningVisible && hasLocalPos)
                RefreshNearestAsteroidCacheThrottled(em, localPos);

            for (int i = 0; i < shipEntities.Length; i++)
            {
                Entity shipEntity = shipEntities[i];
                var shipState = em.GetComponentData<ShipState>(shipEntity);
                if (shipState.IsDead || shipState.AwaitingTeamSelection)
                    continue;
                if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                    continue;

                var ghost = em.GetComponentData<GhostOwner>(shipEntity);
                int netId = ghost.NetworkId;
                if (netId <= 0)
                    continue;

                // --- Hide drones while the owner is stowed in a planetary defense turret ---
                // [TITAN-ORBIT] Same possession mode that hides the hull — swarm GOs must not
                // keep orbiting an invisible pad-parked ship.
                if (em.HasComponent<ShipTurretControlState>(shipEntity) &&
                    em.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling)
                {
                    if (_groupsByNetId.TryGetValue(netId, out var stowedGroup))
                    {
                        DestroyGroup(stowedGroup);
                        _groupsByNetId.Remove(netId);
                    }
                    continue;
                }

                if (!TryGetShipPresentationPose(em, shipEntity, netId, out Vector3 shipPos, out Quaternion shipRot, out float shipScale))
                    continue;

                // Remote cull — skip far swarms (still bandwidth-free; just less GO work).
                bool isLocal = localNetId > 0 && netId == localNetId;
                if (!isLocal && hasLocalPos)
                {
                    float d = DroneSwarmLogic.ToroidalDistanceXZ(
                        localPos.x, localPos.z, shipPos.x, shipPos.z, _mapW, _mapH);
                    if (d > RemoteVisualRange)
                    {
                        if (_groupsByNetId.TryGetValue(netId, out var farGroup))
                        {
                            DestroyGroup(farGroup);
                            _groupsByNetId.Remove(netId);
                        }
                        continue;
                    }
                }

                var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
                int layoutFp = ComputeLayoutFingerprint(buf);
                if (layoutFp == 0)
                {
                    if (_groupsByNetId.TryGetValue(netId, out var emptyGroup))
                    {
                        DestroyGroup(emptyGroup);
                        _groupsByNetId.Remove(netId);
                    }
                    continue;
                }

                _aliveNetIdsScratch.Add(netId);
                if (!_groupsByNetId.TryGetValue(netId, out var group))
                {
                    group = CreateGroup(netId);
                    _groupsByNetId[netId] = group;
                }

                if (layoutFp != group.LayoutFingerprint)
                {
                    RebuildGroupVisuals(group, buf, netId);
                    group.LayoutFingerprint = layoutFp;
                }

                UpdateGroupOrbit(group, shipPos, shipRot, shipScale, shipState.Team, netId, timeSeconds, dt, isLocal);
            }

            // --- Cull groups for ships that left / died ---
            _removeNetIdsScratch.Clear();
            foreach (var kv in _groupsByNetId)
            {
                if (!_aliveNetIdsScratch.Contains(kv.Key))
                    _removeNetIdsScratch.Add(kv.Key);
            }
            for (int i = 0; i < _removeNetIdsScratch.Count; i++)
            {
                int id = _removeNetIdsScratch[i];
                if (_groupsByNetId.TryGetValue(id, out var g))
                {
                    DestroyGroup(g);
                    _groupsByNetId.Remove(id);
                }
            }
        
}

        /// <summary>
        /// Loads drone mesh prefab. Player builds use Resources copies under Assets/Resources/;
        /// Editor also accepts the Prefabs/ path.
        /// </summary>
        static GameObject LoadPrefab(string assetPath, string resourcesName)
        {
            var fromResources = Resources.Load<GameObject>(resourcesName);
            if (fromResources != null)
                return fromResources;
            fromResources = Resources.Load<GameObject>("Prefabs/" + resourcesName);
            if (fromResources != null)
                return fromResources;
#if UNITY_EDITOR
            var fromEditor = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (fromEditor != null)
                return fromEditor;
#endif
            return null;
        }

        /// <summary>World used for ship ghosts + equipment (prefer ClientWorld presentation).</summary>
        static World ResolveShipWorld()
        {
            // Local host: ServerWorld has authoritative equipment; ClientWorld has remotes.
            // Prefer visualization / client for presentation poses; equipment is ghosted either way.
            World viz = EcsGameBridge.GetVisualizationWorld();
            if (viz != null && viz.IsCreated)
                return viz;
            if (EcsGameBridge.IsLocalHost() && EcsGameBridge.ServerWorld != null && EcsGameBridge.ServerWorld.IsCreated)
                return EcsGameBridge.ServerWorld;
            return EcsGameBridge.ClientWorld;
        }

        void RefreshMapSize()
        {
            if (TitanOrbit.NetCode.MapSessionMetaCache.HasMapSize)
            {
                _mapW = TitanOrbit.NetCode.MapSessionMetaCache.MapWidth;
                _mapH = TitanOrbit.NetCode.MapSessionMetaCache.MapHeight;
                return;
            }

            World world = EcsGameBridge.ClientWorld ?? EcsGameBridge.ServerWorld;
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            using var q = em.CreateEntityQuery(ComponentType.ReadOnly<MapStateSingleton>());
            if (q.IsEmptyIgnoreFilter)
                return;
            var map = q.GetSingleton<MapStateSingleton>();
            if (map.MapWidth >= 100f && map.MapHeight >= 100f)
            {
                _mapW = map.MapWidth;
                _mapH = map.MapHeight;
            }
        }

        /// <summary>
        /// Publishes fractional ServerTick seconds so buzz matches server combat (fixed tick).
        /// </summary>
        void PublishSimTimeFromNetwork()
        {
            // [NETCODE] Same timeline as moons / DroneSwarmCombatSystem (not Time.time).
            if (PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double elapsed, includeTickFraction: true))
                DroneSwarmSimTime.Publish(elapsed);
        }

        /// <summary>
        /// Presentation pose for local ship prefers <see cref="ShipDisplayPose"/>; remotes use
        /// <see cref="GhostPresentationTransformCache"/> then LocalTransform fallback.
        /// </summary>
        bool TryGetShipPresentationPose(
            EntityManager em,
            Entity shipEntity,
            int networkId,
            out Vector3 position,
            out Quaternion rotation,
            out float scale)
        {
            position = default;
            rotation = Quaternion.identity;
            scale = 1f;

            int localId = EcsGameBridge.GetLocalNetworkId();
            bool isLocal = localId > 0 && networkId == localId;

            if (isLocal && ShipDisplayPose.HasLocalPose)
            {
                position = ShipDisplayPose.LocalPosition;
                rotation = ShipDisplayPose.LocalRotation;
            }
            else if (GhostPresentationTransformCache.TryGetShip(shipEntity, out var snap))
            {
                position = (Vector3)snap.Position;
                rotation = (Quaternion)snap.Rotation;
            }
            else if (em.HasComponent<LocalTransform>(shipEntity))
            {
                var lt = em.GetComponentData<LocalTransform>(shipEntity);
                position = (Vector3)lt.Position;
                rotation = (Quaternion)lt.Rotation;
                scale = lt.Scale > 0.01f ? lt.Scale : 1f;
            }
            else
            {
                return false;
            }

            if (em.HasComponent<ShipState>(shipEntity))
            {
                var st = em.GetComponentData<ShipState>(shipEntity);
                scale = BodyCollisionMath.GetShipTierScale(Mathf.Max(1, st.ShipLevel));
            }
            else if (em.HasComponent<LocalTransform>(shipEntity))
            {
                float s = em.GetComponentData<LocalTransform>(shipEntity).Scale;
                if (s > 0.01f)
                    scale = s;
            }

            return true;
        }

        /// <summary>
        /// Layout hash: item types + purchase levels for living drones. HP ticks do not rebuild.
        /// </summary>
        static int ComputeLayoutFingerprint(DynamicBuffer<EquippedEquipmentElement> buf)
        {
            unchecked
            {
                int fp = 17;
                int living = 0;
                for (int i = 0; i < buf.Length; i++)
                {
                    var e = buf[i];
                    var type = (StoreItemType)e.ItemType;
                    if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                        continue;
                    living++;
                    fp = fp * 31 + i;
                    fp = fp * 31 + e.ItemType;
                    fp = fp * 31 + e.ItemLevel;
                }

                return living == 0 ? 0 : fp;
            }
        }

        ShipDroneGroup CreateGroup(int networkId)
        {
            var hubGo = new GameObject($"DroneSwarmHub_Net{networkId}");
            hubGo.transform.SetParent(transform, false);
            return new ShipDroneGroup
            {
                NetworkId = networkId,
                Hub = hubGo.transform,
            };
        }

        void RebuildGroupVisuals(ShipDroneGroup group, DynamicBuffer<EquippedEquipmentElement> buf, int networkId)
        {
            ClearGroupVisuals(group);
            for (int i = 0; i < buf.Length; i++)
            {
                var e = buf[i];
                var type = (StoreItemType)e.ItemType;
                if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                    continue;
                SpawnVisual(group, i, type, networkId, e.ItemLevel);
            }
        }

        void SpawnVisual(ShipDroneGroup group, int slotIndex, StoreItemType itemType, int networkId, int itemLevel)
        {
            GameObject prefab = GetPrefab(itemType);
            if (prefab == null || group.Hub == null)
            {
                Debug.LogWarning(
                    $"[DroneSwarm] Spawn skipped — prefab missing for {itemType}. " +
                    "Windows builds need Assets/Resources/{Fighter,Shield,Mining}Drone.prefab.");
                return;
            }

            var instance = Instantiate(prefab, group.Hub);
            instance.name = $"{itemType}_Slot{slotIndex}";
            StripPhysicsAndNetwork(instance);

            // --- Level-based size ---
            // [TITAN-ORBIT] Prefab localScale is the authored max-level size. Multiply by
            // GetDroneVisualScale (1.0 at L6, smaller at L1) — do NOT force Vector3.one.
            // ItemLevel 0 = legacy equipment — keep full prefab size.
            int level = itemLevel > 0 ? itemLevel : StoreItemData.DroneReferenceMaxLevel;
            Vector3 prefabScale = instance.transform.localScale;
            float levelMul = StoreItemData.GetDroneVisualScale(level);
            instance.transform.localScale = prefabScale * levelMul;

            group.Visuals.Add(new SlotVisual
            {
                SlotIndex = slotIndex,
                ItemType = itemType,
                ItemLevel = level,
                PrefabLocalScale = prefabScale,
                Instance = instance,
                BuzzPhase = DroneSwarmLogic.DeterministicBasePhaseRad(networkId, slotIndex, itemType),
            });
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

        void ClearGroupVisuals(ShipDroneGroup group)
        {
            for (int i = 0; i < group.Visuals.Count; i++)
            {
                if (group.Visuals[i].Instance != null)
                    Destroy(group.Visuals[i].Instance);
            }
            group.Visuals.Clear();
        }

        void DestroyGroup(ShipDroneGroup group)
        {
            ClearGroupVisuals(group);
            if (group.Hub != null)
                Destroy(group.Hub.gameObject);
        }

        void ClearAllGroups()
        {
            foreach (var kv in _groupsByNetId)
                DestroyGroup(kv.Value);
            _groupsByNetId.Clear();
        }

        /// <summary>
        /// Places one ship's drones with EvaluateSlotPose (no orbit catch-up — matches server fire).
        /// </summary>
        void UpdateGroupOrbit(
            ShipDroneGroup group,
            Vector3 shipPos,
            Quaternion shipRot,
            float shipScale,
            TeamId ownerTeam,
            int networkId,
            double timeSeconds,
            float dt,
            bool isLocalOwner)
        {
            if (group.Hub == null || group.Visuals.Count == 0)
                return;

            group.Hub.position = shipPos;
            group.Hub.rotation = Quaternion.identity;

            DroneSwarmPositioning.GetShipBasis(shipPos, shipRot, out Vector3 basisPos, out Vector3 forward, out Vector3 right);
            float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipScale);
            float orbitRadius = DroneSwarmPositioning.GetDroneOrbitRadiusFromHull(hullRadius);

            _rearSlotsScratch.Clear();
            _shieldSlotsScratch.Clear();
            for (int i = 0; i < group.Visuals.Count; i++)
            {
                var t = group.Visuals[i].ItemType;
                if (t == StoreItemType.FighterDrone || t == StoreItemType.MiningDrone)
                    _rearSlotsScratch.Add(group.Visuals[i].SlotIndex);
                else if (t == StoreItemType.ShieldDrone)
                    _shieldSlotsScratch.Add(group.Visuals[i].SlotIndex);
            }

            // Per-ship shield assignment from the global enemy cache (filtered by engage range).
            BuildShieldAssignmentsForShip(basisPos, ownerTeam, networkId);

            int rearCount = Mathf.Max(1, _rearSlotsScratch.Count);
            int shieldCount = Mathf.Max(1, _shieldSlotsScratch.Count);

            for (int i = 0; i < group.Visuals.Count; i++)
            {
                var v = group.Visuals[i];
                if (v.Instance == null)
                    continue;

                int rearOrd = IndexInList(_rearSlotsScratch, v.SlotIndex);
                int shieldOrd = IndexInList(_shieldSlotsScratch, v.SlotIndex);
                bool hasShieldTarget = false;
                Vector3 enemyPos = default;
                int indexOnEnemy = 0;
                int countOnEnemy = 1;
                if (v.ItemType == StoreItemType.ShieldDrone &&
                    _shieldAssignments.TryGetValue(v.SlotIndex, out var assign) &&
                    assign.EnemyNetworkId > 0 &&
                    _enemyPosByNetId.TryGetValue(assign.EnemyNetworkId, out enemyPos))
                {
                    hasShieldTarget = true;
                    indexOnEnemy = assign.IndexOnEnemy;
                    countOnEnemy = Mathf.Max(1, assign.CountOnEnemy);
                }

                var ctx = new DroneSwarmPositioning.SlotEvaluationContext
                {
                    ShipPos = basisPos,
                    Forward = forward,
                    Right = right,
                    OrbitRadius = orbitRadius,
                    TimeSeconds = timeSeconds,
                    ShipNetworkId = networkId,
                    MapW = _mapW,
                    MapH = _mapH,
                    RearOrdinal = rearOrd,
                    RearCount = rearCount,
                    ShieldOrdinal = shieldOrd,
                    ShieldCount = shieldCount,
                    HasShieldTarget = hasShieldTarget,
                    EnemyPos = enemyPos,
                    IndexOnEnemy = indexOnEnemy,
                    CountOnEnemy = countOnEnemy,
                };
                var pose = DroneSwarmPositioning.EvaluateSlotPose(v.ItemType, v.SlotIndex, in ctx);

                // Hub-local: planar offset from ship + presentation Y lift (combat stays FixedY).
                Vector3 local = pose.WorldPosition - basisPos;
                float buzzY = Mathf.Sin((float)timeSeconds * DroneSwarmLogic.BuzzSpeed * 0.91f + v.BuzzPhase)
                    * DroneSwarmLogic.BuzzAmplitude * BuzzVerticalFraction;
                local.y = DroneSwarmLogic.PresentationLiftY + buzzY;
                v.Instance.transform.localPosition = local;

                // Prefab authored size × level mul (L6 = 1.0 → same as pre-leveling drones).
                float levelMul = StoreItemData.GetDroneVisualScale(Mathf.Max(1, v.ItemLevel));
                v.Instance.transform.localScale = v.PrefabLocalScale * levelMul;

                ApplyFacing(v, pose.WorldPosition, basisPos, forward, ownerTeam, networkId, dt, isLocalOwner);
                group.Visuals[i] = v;
            }
        }

        /// <summary>
        /// Collects all living ship planar poses once per frame (keyed by NetworkId + team).
        /// </summary>
        void RefreshGlobalEnemyCache(EntityManager em, NativeArray<Entity> shipEntities)
        {
            _enemyPosByNetId.Clear();
            _enemyTeamByNetId.Clear();

            for (int i = 0; i < shipEntities.Length; i++)
            {
                Entity e = shipEntities[i];
                var st = em.GetComponentData<ShipState>(e);
                if (st.IsDead)
                    continue;
                var ghost = em.GetComponentData<GhostOwner>(e);
                if (ghost.NetworkId <= 0)
                    continue;

                float3 pos = em.GetComponentData<LocalTransform>(e).Position;
                if (GhostPresentationTransformCache.TryGetShip(e, out var snap))
                    pos = snap.Position;
                pos.y = 0f;
                _enemyPosByNetId[ghost.NetworkId] = new Vector3(pos.x, 0f, pos.z);
                _enemyTeamByNetId[ghost.NetworkId] = st.Team;
            }
        }

        void BuildShieldAssignmentsForShip(Vector3 ownerPos, TeamId ownerTeam, int ownerNetId)
        {
            _shieldAssignments.Clear();
            _enemyNetIdsScratch.Clear();
            if (_shieldSlotsScratch.Count == 0)
                return;

            float range = DroneSwarmLogic.ShieldEngageRange;
            float rangeSq = range * range;
            foreach (var kv in _enemyPosByNetId)
            {
                if (ownerNetId > 0 && kv.Key == ownerNetId)
                    continue;
                if (_enemyTeamByNetId.TryGetValue(kv.Key, out var team) &&
                    ownerTeam != TeamId.None && team == ownerTeam)
                    continue;

                float d = DroneSwarmLogic.ToroidalDistanceXZ(
                    ownerPos.x, ownerPos.z, kv.Value.x, kv.Value.z, _mapW, _mapH);
                if (d * d > rangeSq)
                    continue;
                _enemyNetIdsScratch.Add(kv.Key);
            }

            DroneSwarmPositioning.BuildShieldAssignments(
                _shieldSlotsScratch, _enemyNetIdsScratch, _shieldAssignments);
        }

        /// <summary>
        /// Throttled nearest-asteroid aim for local mining facing.
        /// Under TransformQuarantine we must NOT <c>ToEntityArray</c> asteroids — walk hybrid
        /// proxies from <see cref="EcsWorldVisualizer"/> instead (same pattern as floating counts).
        /// </summary>
        void RefreshNearestAsteroidCacheThrottled(EntityManager em, Vector3 from)
        {
            if (Time.frameCount - _asteroidCacheFrame < AsteroidAimRefreshFrames && _asteroidCacheFrame >= 0)
                return;
            _asteroidCacheFrame = Time.frameCount;

            _hasCachedNearestAsteroid = false;
            _cachedNearestAsteroid = default;

            float3 owner = new float3(from.x, 0f, from.z);
            float bestSq = DroneSwarmLogic.MiningEngageRange * DroneSwarmLogic.MiningEngageRange;

            // --- Preferred under quarantine: hybrid GO proxies (no ECS map-body gather) ---
            var viz = EcsWorldVisualizer.Active;
            if (viz != null)
            {
                _asteroidProxyScratch.Clear();
                viz.CopyAsteroidProxyEntitiesTo(_asteroidProxyScratch);
                for (int i = 0; i < _asteroidProxyScratch.Count; i++)
                {
                    Entity e = _asteroidProxyScratch[i];
                    if (!viz.TryGetProxy(e, out GameObject proxy) || proxy == null)
                        continue;
                    // Skip destroyed rocks when ECS state is readable without a full gather.
                    if (em.Exists(e) && em.HasComponent<AsteroidState>(e))
                    {
                        var st = em.GetComponentData<AsteroidState>(e);
                        if (st.IsDestroyed || st.Health <= 0f)
                            continue;
                    }

                    Vector3 wp = proxy.transform.position;
                    float d = DroneSwarmLogic.ToroidalDistanceXZ(owner.x, owner.z, wp.x, wp.z, _mapW, _mapH);
                    float sq = d * d;
                    if (sq >= bestSq)
                        continue;
                    bestSq = sq;
                    _cachedNearestAsteroid = new Vector3(wp.x, 0f, wp.z);
                    _hasCachedNearestAsteroid = true;
                }
            }
            else if (!ClientJoinSettleCache.ShouldSkipMapBodyQueries && _queriesCreated)
            {
                // Editor / rare path when quarantine is off and visualizer missing.
                using var entities = _asteroidQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    var a = em.GetComponentData<AsteroidState>(entities[i]);
                    if (a.IsDestroyed || a.Health <= 0f)
                        continue;
                    float3 p = em.GetComponentData<LocalTransform>(entities[i]).Position;
                    p.y = 0f;
                    float d = DroneSwarmLogic.ToroidalDistanceXZ(owner.x, owner.z, p.x, p.z, _mapW, _mapH);
                    float sq = d * d;
                    if (sq >= bestSq)
                        continue;
                    bestSq = sq;
                    _cachedNearestAsteroid = new Vector3(p.x, 0f, p.z);
                    _hasCachedNearestAsteroid = true;
                }
            }
        }

        void ApplyFacing(
            SlotVisual v,
            Vector3 planarWorldPos,
            Vector3 shipPos,
            Vector3 shipForward,
            TeamId ownerTeam,
            int ownerNetId,
            float dt,
            bool isLocalOwner)
        {
            if (v.ItemType == StoreItemType.ShieldDrone)
            {
                if (_shieldAssignments.TryGetValue(v.SlotIndex, out var assign) &&
                    assign.EnemyNetworkId > 0 &&
                    _enemyPosByNetId.TryGetValue(assign.EnemyNetworkId, out Vector3 enemyPos))
                {
                    v.Instance.transform.rotation = DroneSwarmPositioning.ComputeShieldFaceEnemyRotation(
                        planarWorldPos, enemyPos, Vector3.up, _mapW, _mapH);
                    return;
                }

                Quaternion outward = DroneSwarmPositioning.ComputeShieldFaceOutwardRotation(
                    shipPos, planarWorldPos, Vector3.up);
                v.Instance.transform.rotation = Quaternion.Slerp(
                    v.Instance.transform.rotation, outward, Mathf.Clamp01(ShieldFacingTurnSpeed * 0.75f * dt));
                return;
            }

            // --- Fighter / mining: face engage target (toroidal); snap when locked ---
            Vector3 lookDir = shipForward;
            lookDir.y = 0f;
            bool hasTarget = false;

            if (v.ItemType == StoreItemType.FighterDrone &&
                TryGetNearestEnemyPos(
                    planarWorldPos, DroneSwarmLogic.FighterEngageRange,
                    ownerTeam, ownerNetId, out Vector3 enemyAim))
            {
                lookDir = DroneSwarmLogic.ToroidalOffsetXZ(planarWorldPos, enemyAim, _mapW, _mapH);
                hasTarget = lookDir.sqrMagnitude > 0.0001f;
            }
            else if (v.ItemType == StoreItemType.MiningDrone &&
                     isLocalOwner &&
                     _hasCachedNearestAsteroid)
            {
                lookDir = DroneSwarmLogic.ToroidalOffsetXZ(
                    planarWorldPos, _cachedNearestAsteroid, _mapW, _mapH);
                hasTarget = lookDir.sqrMagnitude > 0.0001f;
            }

            if (lookDir.sqrMagnitude < 0.01f)
                return;

            Quaternion targetRot = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
            if (hasTarget)
            {
                // Snap toward fire direction so muzzle and mesh agree when shooting.
                v.Instance.transform.rotation = targetRot;
            }
            else
            {
                v.Instance.transform.rotation = Quaternion.Slerp(
                    v.Instance.transform.rotation,
                    targetRot,
                    Mathf.Clamp01(FighterFacingTurnSpeed * dt));
            }
        }

        bool TryGetNearestEnemyPos(
            Vector3 from,
            float range,
            TeamId ownerTeam,
            int ownerNetId,
            out Vector3 pos)
        {
            pos = default;
            float bestSq = range * range;
            bool found = false;
            foreach (var kv in _enemyPosByNetId)
            {
                if (ownerNetId > 0 && kv.Key == ownerNetId)
                    continue;
                if (_enemyTeamByNetId.TryGetValue(kv.Key, out var team) &&
                    ownerTeam != TeamId.None && team == ownerTeam)
                    continue;

                float d = DroneSwarmLogic.ToroidalDistanceXZ(
                    from.x, from.z, kv.Value.x, kv.Value.z, _mapW, _mapH);
                float sq = d * d;
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                pos = kv.Value;
                found = true;
            }

            return found;
        }

        static int IndexInList(List<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                    return i;
            }
            return 0;
        }
    }

    /// <summary>
    /// Runtime prefab refs for fighter / shield / mining drones.
    /// Lives under <c>Resources/DroneSwarmPrefabCatalog</c> so Windows player builds can load
    /// without Editor <c>AssetDatabase</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "DroneSwarmPrefabCatalog", menuName = "Titan Orbit/Drone Swarm Prefab Catalog")]
    public class DroneSwarmPrefabCatalog : ScriptableObject
    {
        /// <summary>[UNITY] Resources.Load name (no folder / extension).</summary>
        public const string ResourcesLoadName = "DroneSwarmPrefabCatalog";

        /// <summary>Fighter drone mesh prefab.</summary>
        public GameObject FighterDrone;

        /// <summary>Shield drone mesh prefab.</summary>
        public GameObject ShieldDrone;

        /// <summary>Mining drone mesh prefab.</summary>
        public GameObject MiningDrone;

        static DroneSwarmPrefabCatalog s_cached;

        /// <summary>Loads the Resources catalog (Editor + player).</summary>
        public static DroneSwarmPrefabCatalog LoadDefault()
        {
            if (s_cached != null)
                return s_cached;
            s_cached = Resources.Load<DroneSwarmPrefabCatalog>(ResourcesLoadName);
            return s_cached;
        }
    }
}

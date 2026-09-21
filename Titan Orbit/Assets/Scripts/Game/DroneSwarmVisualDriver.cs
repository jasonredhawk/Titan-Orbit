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
    /// Combat fire is server-only (<c>DroneSwarmCombatSystem</c>). Each fighter / miner slides
    /// its own idle slot toward its closest in-range target; shield walls stay on the hull
    /// (farther out) and still pick per-drone.
    /// Mesh scale =
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

        /// <summary>Oriented covering hull (or sphere fallback) for per-drone standoff.</summary>
        struct CachedAimBody
        {
            public Vector3 Pos;
            public Vector3 Forward;
            public Vector3 Right;
            public float CoveringExtentX;
            public float CoveringExtentZ;
            public float CoveringCenterX;
            public float CoveringCenterZ;
            public float TransformScale;
            public float FallbackRadius;
            public bool HasOrientedHull;
        }

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
        readonly List<Vector3> _shieldIdlePosScratch = new List<Vector3>(8);
        readonly Dictionary<int, Vector3> _enemyPosByNetId = new Dictionary<int, Vector3>(16);
        readonly Dictionary<int, CachedAimBody> _enemyHullByNetId = new Dictionary<int, CachedAimBody>(16);
        readonly Dictionary<int, Vector3> _enemyAssignPosScratch = new Dictionary<int, Vector3>(16);
        readonly Dictionary<int, DroneSwarmPositioning.ShieldAssignment> _shieldAssignments =
            new Dictionary<int, DroneSwarmPositioning.ShieldAssignment>(8);
        readonly Dictionary<int, TeamId> _enemyTeamByNetId = new Dictionary<int, TeamId>(16);
        readonly Dictionary<long, DroneSwarmLogic.FormationAnchorState> _formationOffsetBySlot =
            new Dictionary<long, DroneSwarmLogic.FormationAnchorState>(32);
        readonly Dictionary<int, double> _anchorSimTimeByNetId = new Dictionary<int, double>(8);
        readonly List<long> _anchorKeysScratch = new List<long>(16);

        /// <summary>Scratch for hybrid asteroid proxy keys (quarantine-safe mining swarm).</summary>
        readonly List<Entity> _asteroidProxyScratch = new List<Entity>(512);

        /// <summary>Scratch for hybrid planet proxy keys (quarantine-safe turret aim).</summary>
        readonly List<Entity> _planetProxyScratch = new List<Entity>(64);

        /// <summary>Live asteroid planar poses + body radii for mining swarm pose (every frame).</summary>
        readonly List<(Entity entity, Vector3 pos, float radius)> _cachedAsteroidAims =
            new List<(Entity, Vector3, float)>(256);

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

        /// <summary>
        /// Releases cached queries when the world is still alive.
        /// After world teardown they are already gone — <c>Dispose()</c> NREs in EntityQueryImpl.
        /// </summary>
        void DisposeQueries()
        {
            if (_queriesCreated && _cachedQueryWorld != null && _cachedQueryWorld.IsCreated)
            {
                if (_shipQuery != default)
                    _shipQuery.Dispose();
                if (_asteroidQuery != default)
                    _asteroidQuery.Dispose();
            }

            _shipQuery = default;
            _asteroidQuery = default;
            _queriesCreated = false;
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
            // [TITAN-ORBIT] Skip entity buffer reads during TeamChoice / moon-dock Instantiates.
            // Fully landed: tear down the local swarm (orbit menu owns that state).
            // Still approaching: carry hubs onto ShipDisplayPose so they do not freeze in space.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (IsLocalShipFullyMoonLanded())
                    HideLocalGroup();
                else
                    CarryExistingGroupsWithLocalHull();
                return;
            }

            RefreshMapSize();
            if (_mapW < 100f || _mapH < 100f)
                return;

            PublishSimTimeFromNetwork();
            double timeSeconds = DroneSwarmSimTime.ResolveOrFallback(Time.timeAsDouble);

            World world = ResolveShipWorld();
            if (world == null || !world.IsCreated)
            {
                ClearAllGroups();
                DisposeQueries();
                return;
            }

            EnsureQueries(world);
            if (!_queriesCreated)
                return;

            var em = world.EntityManager;
            using var shipEntities = _shipQuery.ToEntityArray(Allocator.Temp);

            // Local ship pose for remote distance cull + mining swarm gather.
            Vector3 localPos = default;
            bool hasLocalPos = ShipDisplayPose.HasLocalPose;
            if (hasLocalPos)
                localPos = ShipDisplayPose.LocalPosition;
            else
                hasLocalPos = EcsGameBridge.TryGetLocalShipPresentationPosition(out localPos);
            int localNetId = EcsGameBridge.GetLocalNetworkId();

            // Build global enemy cache once (shield assign + fighter facing).
            RefreshGlobalEnemyCache(em, shipEntities);
            RefreshEnemyTurretAimCache(em);

            // Asteroid poses drive mining swarm position — must match server every frame,
            // including remotes inside RemoteVisualRange (do not throttle).
            bool anyMiningVisible = false;
            for (int i = 0; i < shipEntities.Length && !anyMiningVisible; i++)
            {
                Entity shipEntity = shipEntities[i];
                if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                    continue;
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

            if (anyMiningVisible)
                RefreshAsteroidSwarmCache(em, localPos, hasLocalPos);

            _aliveNetIdsScratch.Clear();
            float dt = Time.deltaTime;

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

                // --- Hide drones while the owner is stowed (turret pad or fully moon-docked) ---
                // [TITAN-ORBIT] Same possession mode that hides the hull / nameplate — swarm GOs
                // must not keep orbiting a pad-parked or moon-landed ship. Other ships stay up.
                if (IsOwnerSwarmHidden(em, shipEntity))
                {
                    HideGroup(netId);
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
                    RebuildGroupVisuals(group, buf, netId, shipState.Team);
                    group.LayoutFingerprint = layoutFp;
                }

                ReadCoveringPresentation(em, shipEntity, out float coverEx, out float coverEz, out float coverCx, out float coverCz);
                UpdateGroupOrbit(
                    group, buf, shipPos, shipRot, shipScale,
                    coverEx, coverEz, coverCx, coverCz,
                    shipState.Team, netId, timeSeconds, dt);
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

            // World hull scale is LocalTransform.Scale — regular ships store tier (+10%/level);
            // MEGA / Titan hulls store tier-7 × catalog family scale (often ~0.2).
            // GetShipTierScale(ShipLevel) is wrong for Titans: they stamp ShipLevel 7 (1.6)
            // while the drawn collider is 1.6 × catalogScale, so drones sat many hull-lengths out.
            if (em.HasComponent<LocalTransform>(shipEntity))
            {
                float s = em.GetComponentData<LocalTransform>(shipEntity).Scale;
                if (s > 0.01f)
                    scale = s;
            }
            else if (em.HasComponent<ShipState>(shipEntity))
            {
                var st = em.GetComponentData<ShipState>(shipEntity);
                scale = BodyCollisionMath.GetShipTierScale(Mathf.Max(1, st.ShipLevel));
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

        void RebuildGroupVisuals(
            ShipDroneGroup group,
            DynamicBuffer<EquippedEquipmentElement> buf,
            int networkId,
            TeamId team)
        {
            ClearGroupVisuals(group);
            for (int i = 0; i < buf.Length; i++)
            {
                var e = buf[i];
                var type = (StoreItemType)e.ItemType;
                if (!StoreItemData.IsDrone(type) || e.RemainingCharges <= 0)
                    continue;
                SpawnVisual(group, i, type, networkId, e.ItemLevel, team);
            }
        }

        void SpawnVisual(
            ShipDroneGroup group,
            int slotIndex,
            StoreItemType itemType,
            int networkId,
            int itemLevel,
            TeamId team)
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
            DroneTeamVisualApplier.Apply(instance, team);
            DroneSwarmNameplate.Ensure(instance);

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

        void RemoveFormationAnchorsForShip(int networkId)
        {
            _anchorKeysScratch.Clear();
            foreach (var key in _formationOffsetBySlot.Keys)
            {
                if ((int)(key >> 32) == networkId)
                    _anchorKeysScratch.Add(key);
            }
            for (int i = 0; i < _anchorKeysScratch.Count; i++)
                _formationOffsetBySlot.Remove(_anchorKeysScratch[i]);
        }

        void DestroyGroup(ShipDroneGroup group)
        {
            ClearGroupVisuals(group);
            if (group.Hub != null)
                Destroy(group.Hub.gameObject);
            RemoveFormationAnchorsForShip(group.NetworkId);
            _anchorSimTimeByNetId.Remove(group.NetworkId);
        }

        void ClearAllGroups()
        {
            foreach (var kv in _groupsByNetId)
                DestroyGroup(kv.Value);
            _groupsByNetId.Clear();
            _formationOffsetBySlot.Clear();
            _anchorSimTimeByNetId.Clear();
        }

        /// <summary>
        /// No ship gather. Pins the local swarm hub to <see cref="ShipDisplayPose"/> so moon-dock
        /// Instantiates cannot leave fighter / shield / mining meshes at last orbit pose.
        /// </summary>
        void CarryExistingGroupsWithLocalHull()
        {
            if (!ShipDisplayPose.HasLocalPose)
                return;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId <= 0 || !_groupsByNetId.TryGetValue(localId, out var group) || group.Hub == null)
                return;

            group.Hub.position = ShipDisplayPose.LocalPosition;
        }

        /// <summary>
        /// True when this hull is turret-stowed or fully landed on a gem moon.
        /// Per-ship — one dock must not hide another ship's swarm.
        /// </summary>
        static bool IsOwnerSwarmHidden(EntityManager em, Entity shipEntity)
        {
            if (em.HasComponent<ShipTurretControlState>(shipEntity) &&
                em.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling)
                return true;
            return ShipMoonDockState.IsFullyLandedOnMoon(em, shipEntity);
        }

        static bool IsLocalShipFullyMoonLanded()
        {
            return EcsGameBridge.TryGetLocalShipMoonDockState(out var dock) && dock.IsFullyLanded;
        }

        void HideLocalGroup()
        {
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0)
                HideGroup(localId);
        }

        void HideGroup(int netId)
        {
            if (!_groupsByNetId.TryGetValue(netId, out var group))
                return;
            DestroyGroup(group);
            _groupsByNetId.Remove(netId);
        }

        /// <summary>
        /// Presentation-space covering box from <see cref="ShipHullColliderState"/> (zeros if missing).
        /// </summary>
        static void ReadCoveringPresentation(
            EntityManager em,
            Entity shipEntity,
            out float extentX,
            out float extentZ,
            out float centerX,
            out float centerZ)
        {
            extentX = extentZ = centerX = centerZ = 0f;
            if (!em.HasComponent<ShipHullColliderState>(shipEntity))
                return;
            var hull = em.GetComponentData<ShipHullColliderState>(shipEntity);
            extentX = hull.AppliedCoveringExtentX;
            extentZ = hull.AppliedCoveringExtentZ;
            centerX = hull.AppliedCoveringCenterX;
            centerZ = hull.AppliedCoveringCenterZ;
        }

        /// <summary>
        /// Places one ship's drones with EvaluateSlotPose (no orbit catch-up — matches server fire).
        /// </summary>
        void UpdateGroupOrbit(
            ShipDroneGroup group,
            DynamicBuffer<EquippedEquipmentElement> equipment,
            Vector3 shipPos,
            Quaternion shipRot,
            float shipScale,
            float coveringExtentX,
            float coveringExtentZ,
            float coveringCenterX,
            float coveringCenterZ,
            TeamId ownerTeam,
            int networkId,
            double timeSeconds,
            float dt)
        {
            if (group.Hub == null || group.Visuals.Count == 0)
                return;

            group.Hub.position = shipPos;
            group.Hub.rotation = Quaternion.identity;

            DroneSwarmPositioning.GetShipBasis(shipPos, shipRot, out Vector3 basisPos, out Vector3 forward, out Vector3 right);
            float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(shipScale);
            float orbitRadius = DroneSwarmPositioning.GetDroneOrbitRadiusFromHull(hullRadius);
            float shieldOrbitRadius = DroneSwarmPositioning.GetShieldOrbitRadiusFromHull(hullRadius);

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

            // Each shield picks the closest enemy from its idle rear pose (not round-robin).
            BuildShieldAssignmentsForShip(
                basisPos, forward, right, shieldOrbitRadius, shipScale,
                coveringExtentX, coveringExtentZ, coveringCenterX, coveringCenterZ,
                ownerTeam, networkId, timeSeconds);

            int rearCount = Mathf.Max(1, _rearSlotsScratch.Count);
            int shieldCount = Mathf.Max(1, _shieldSlotsScratch.Count);

            float anchorDt = 0f;
            if (_anchorSimTimeByNetId.TryGetValue(networkId, out double lastSim)
                && timeSeconds > lastSim)
            {
                anchorDt = Mathf.Clamp((float)(timeSeconds - lastSim), 0f, 0.1f);
            }
            _anchorSimTimeByNetId[networkId] = timeSeconds;

            for (int i = 0; i < group.Visuals.Count; i++)
            {
                var v = group.Visuals[i];
                if (v.Instance == null)
                    continue;

                int rearOrd = IndexInList(_rearSlotsScratch, v.SlotIndex);
                int shieldOrd = IndexInList(_shieldSlotsScratch, v.SlotIndex);
                Vector3 enemyPos = default;
                bool hasSwarmTarget = false;
                float slotOrbitRadius = v.ItemType == StoreItemType.ShieldDrone
                    ? shieldOrbitRadius
                    : orbitRadius;

                var ctx = new DroneSwarmPositioning.SlotEvaluationContext
                {
                    ShipPos = basisPos,
                    Forward = forward,
                    Right = right,
                    OrbitRadius = slotOrbitRadius,
                    TimeSeconds = timeSeconds,
                    ShipNetworkId = networkId,
                    MapW = _mapW,
                    MapH = _mapH,
                    RearOrdinal = rearOrd,
                    RearCount = rearCount,
                    ShieldOrdinal = shieldOrd,
                    ShieldCount = shieldCount,
                    HasShieldTarget = false,
                };
                DroneSwarmPositioning.ApplyCoveringHullShape(
                    ref ctx, shipScale, coveringExtentX, coveringExtentZ, coveringCenterX, coveringCenterZ);
                var pose = DroneSwarmPositioning.EvaluateSlotPose(v.ItemType, v.SlotIndex, in ctx);

                if (v.ItemType == StoreItemType.FighterDrone ||
                    v.ItemType == StoreItemType.MiningDrone ||
                    v.ItemType == StoreItemType.ShieldDrone)
                {
                    Vector3 idlePos = pose.WorldPosition;
                    idlePos.y = DroneSwarmLogic.FixedY;
                    bool isFighter = v.ItemType == StoreItemType.FighterDrone;
                    bool isShield = v.ItemType == StoreItemType.ShieldDrone;
                    float leash = isFighter
                        ? DroneSwarmLogic.FighterEngageRange
                        : DroneSwarmLogic.MiningEngageRange;
                    float targetStandoff = 0f;
                    Vector3 shipHome = DroneSwarmPositioning.ResolveFormationHome(in ctx);
                    if (isFighter || isShield)
                    {
                        hasSwarmTarget = isShield
                            ? TryGetOwnerShieldSwarmTarget(
                                shipHome, ownerTeam, networkId, out enemyPos, out targetStandoff)
                            : TryGetOwnerFighterSwarmTarget(
                                idlePos, ownerTeam, networkId, out enemyPos, out leash, out targetStandoff);
                    }
                    else
                    {
                        hasSwarmTarget = TryGetOwnerMiningSwarmTarget(
                            idlePos, out enemyPos, out targetStandoff);
                    }

                    long offsetKey = DroneSwarmLogic.FormationAnchorKey(networkId, v.SlotIndex);
                    var anchorState = _formationOffsetBySlot.TryGetValue(offsetKey, out var prevAnchor)
                        ? prevAnchor : default;
                    Vector3 desired = isShield
                        ? DroneSwarmLogic.ComputeShieldDesiredFormationOffset(
                            shipHome, enemyPos, hasSwarmTarget, targetStandoff, _mapW, _mapH)
                        : DroneSwarmLogic.ComputeDesiredFormationOffset(
                            idlePos, enemyPos, hasSwarmTarget, leash, targetStandoff, _mapW, _mapH);
                    anchorState = DroneSwarmLogic.StepFormationOffset(
                        anchorState, desired, idlePos, anchorDt, _mapW, _mapH, hasSwarmTarget);
                    _formationOffsetBySlot[offsetKey] = anchorState;
                    pose.WorldPosition = idlePos + anchorState.Offset;
                    pose.WorldPosition.y = DroneSwarmLogic.FixedY;
                }

                // Hub-local: toroidal planar offset so a pad across a seam does not stretch the long way.
                Vector3 local = DroneSwarmLogic.ToroidalOffsetXZ(basisPos, pose.WorldPosition, _mapW, _mapH);
                float buzzY = Mathf.Sin((float)timeSeconds * DroneSwarmLogic.BuzzSpeed * 0.91f + v.BuzzPhase)
                    * DroneSwarmLogic.BuzzAmplitude * BuzzVerticalFraction;
                local.y = DroneSwarmLogic.PresentationLiftY + buzzY;
                v.Instance.transform.localPosition = local;

                // Prefab authored size × level mul (L6 = 1.0 → same as pre-leveling drones).
                float levelMul = StoreItemData.GetDroneVisualScale(Mathf.Max(1, v.ItemLevel));
                v.Instance.transform.localScale = v.PrefabLocalScale * levelMul;

                ApplyFacing(
                    v, pose.WorldPosition, basisPos, forward, dt,
                    hasSwarmTarget, enemyPos);

                int hp = 0;
                int maxHp = StoreItemData.GetDroneMaxHp(v.ItemType, Mathf.Max(1, v.ItemLevel));
                if (v.SlotIndex >= 0 && v.SlotIndex < equipment.Length)
                    hp = equipment[v.SlotIndex].RemainingCharges;
                DroneSwarmNameplate.Sync(v.Instance, networkId, hp, maxHp);

                group.Visuals[i] = v;
            }
        }

        /// <summary>
        /// Collects all living ship planar poses once per frame (keyed by NetworkId + team).
        /// </summary>
        void RefreshGlobalEnemyCache(EntityManager em, NativeArray<Entity> shipEntities)
        {
            _enemyPosByNetId.Clear();
            _enemyHullByNetId.Clear();
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

                var lt = em.GetComponentData<LocalTransform>(e);
                float3 pos = lt.Position;
                Quaternion rot = (Quaternion)lt.Rotation;
                if (GhostPresentationTransformCache.TryGetShip(e, out var snap))
                {
                    pos = snap.Position;
                    rot = (Quaternion)snap.Rotation;
                }
                pos.y = 0f;
                Vector3 planar = new Vector3(pos.x, 0f, pos.z);
                DroneSwarmPositioning.GetShipBasis(planar, rot, out planar, out Vector3 fwd, out Vector3 right);
                float coverEx = 0f, coverEz = 0f, coverCx = 0f, coverCz = 0f;
                if (em.HasComponent<ShipHullColliderState>(e))
                {
                    var hull = em.GetComponentData<ShipHullColliderState>(e);
                    coverEx = hull.AppliedCoveringExtentX;
                    coverEz = hull.AppliedCoveringExtentZ;
                    coverCx = hull.AppliedCoveringCenterX;
                    coverCz = hull.AppliedCoveringCenterZ;
                }
                Vector3 hullOrigin = DroneSwarmPositioning.ResolveCoveringOrigin(
                    planar, fwd, right, coverCx, coverCz, lt.Scale);
                _enemyPosByNetId[ghost.NetworkId] = hullOrigin;
                _enemyHullByNetId[ghost.NetworkId] = new CachedAimBody
                {
                    Pos = hullOrigin,
                    Forward = fwd,
                    Right = right,
                    CoveringExtentX = coverEx,
                    CoveringExtentZ = coverEz,
                    CoveringCenterX = coverCx,
                    CoveringCenterZ = coverCz,
                    TransformScale = lt.Scale,
                    FallbackRadius = BodyCollisionMath.GetShipHullRadiusWorld(lt.Scale),
                    HasOrientedHull = true,
                };
                _enemyTeamByNetId[ghost.NetworkId] = st.Team;
            }
        }

        /// <summary>
        /// Collects live enemy turret pad poses from hybrid planet proxies (no map-body gather).
        /// </summary>
        void RefreshEnemyTurretAimCache(EntityManager em)
        {
            var viz = EcsWorldVisualizer.Active;
            if (viz == null)
                return;

            viz.CopyPlanetProxyEntities(_planetProxyScratch);
            for (int i = 0; i < _planetProxyScratch.Count; i++)
            {
                Entity planetEntity = _planetProxyScratch[i];
                if (!em.Exists(planetEntity) ||
                    !em.HasComponent<PlanetState>(planetEntity) ||
                    !em.HasComponent<LocalTransform>(planetEntity) ||
                    !em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                var planet = em.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                var xf = em.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = xf.Position;
                if (viz.TryGetProxy(planetEntity, out GameObject proxy) && proxy != null)
                    planetPos = (float3)proxy.transform.position;

                int slotCount = buffer.Length;
                for (int s = 0; s < slotCount; s++)
                {
                    var slot = buffer[s];
                    if (slot.TurretLevel == 0)
                        continue;

                    float hp = slot.Health;
                    if (PlanetaryDefenseClientHealthSync.TryGetHealth(planet.PlanetId, s, out float liveHp))
                        hp = liveHp;
                    if (hp <= 0f)
                        continue;

                    float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetPos, math.max(0.25f, xf.Scale), planet.PlanetLevel, s, slotCount);
                    int padId = DroneSwarmLogic.MakeDefensePadEnemyId(planet.PlanetId, s);
                    if (padId == 0)
                        continue;
                    Vector3 padPlanar = new Vector3(slotPos.x, 0f, slotPos.z);
                    _enemyPosByNetId[padId] = padPlanar;
                    _enemyHullByNetId[padId] = new CachedAimBody
                    {
                        Pos = padPlanar,
                        FallbackRadius = DroneSwarmLogic.DefensePadColliderRadius,
                        HasOrientedHull = false,
                    };
                    _enemyTeamByNetId[padId] = planet.Ownership;
                }
            }
        }

        void BuildShieldAssignmentsForShip(
            Vector3 ownerPos,
            Vector3 forward,
            Vector3 right,
            float orbitRadius,
            float shipScale,
            float coveringExtentX,
            float coveringExtentZ,
            float coveringCenterX,
            float coveringCenterZ,
            TeamId ownerTeam,
            int ownerNetId,
            double timeSeconds)
        {
            _shieldAssignments.Clear();
            _enemyNetIdsScratch.Clear();
            _shieldIdlePosScratch.Clear();
            _enemyAssignPosScratch.Clear();
            if (_shieldSlotsScratch.Count == 0)
                return;

            float gatherHull = DroneSwarmLogic.ShieldEngageRange + orbitRadius;
            float gatherPad = DroneSwarmLogic.DefensePadEngageRange + orbitRadius;
            float gatherHullSq = gatherHull * gatherHull;
            float gatherPadSq = gatherPad * gatherPad;
            foreach (var kv in _enemyPosByNetId)
            {
                if (ownerNetId > 0 && kv.Key == ownerNetId)
                    continue;
                if (_enemyTeamByNetId.TryGetValue(kv.Key, out var team) &&
                    ownerTeam != TeamId.None && team == ownerTeam)
                    continue;

                float d = DroneSwarmLogic.ToroidalDistanceXZ(
                    ownerPos.x, ownerPos.z, kv.Value.x, kv.Value.z, _mapW, _mapH);
                float maxSq = DroneSwarmLogic.IsDefensePadEnemyId(kv.Key) ? gatherPadSq : gatherHullSq;
                if (d * d > maxSq)
                    continue;
                _enemyNetIdsScratch.Add(kv.Key);
                _enemyAssignPosScratch[kv.Key] = kv.Value;
            }

            int shieldCount = Mathf.Max(1, _shieldSlotsScratch.Count);
            for (int i = 0; i < _shieldSlotsScratch.Count; i++)
            {
                var idleCtx = new DroneSwarmPositioning.SlotEvaluationContext
                {
                    ShipPos = ownerPos,
                    Forward = forward,
                    Right = right,
                    OrbitRadius = orbitRadius,
                    TimeSeconds = timeSeconds,
                    ShipNetworkId = ownerNetId,
                    MapW = _mapW,
                    MapH = _mapH,
                    ShieldOrdinal = i,
                    ShieldCount = shieldCount,
                    HasShieldTarget = false,
                };
                DroneSwarmPositioning.ApplyCoveringHullShape(
                    ref idleCtx, shipScale, coveringExtentX, coveringExtentZ, coveringCenterX, coveringCenterZ);
                _shieldIdlePosScratch.Add(
                    DroneSwarmPositioning.EvaluateSlotPose(
                        StoreItemType.ShieldDrone, _shieldSlotsScratch[i], in idleCtx).WorldPosition);
            }

            DroneSwarmPositioning.BuildShieldAssignments(
                _shieldSlotsScratch, _shieldIdlePosScratch, _enemyNetIdsScratch, _enemyAssignPosScratch,
                _mapW, _mapH, _shieldAssignments);
        }

        /// <summary>
        /// Live asteroid poses for mining swarm (every frame). Under TransformQuarantine we must
        /// NOT <c>ToEntityArray</c> asteroids — walk hybrid proxies from <see cref="EcsWorldVisualizer"/>.
        /// Gather covers local miners plus remotes inside <see cref="RemoteVisualRange"/>.
        /// </summary>
        void RefreshAsteroidSwarmCache(EntityManager em, Vector3 localPos, bool hasLocalPos)
        {
            _cachedAsteroidAims.Clear();
            float gather = DroneSwarmLogic.MiningEngageRange + RemoteVisualRange;
            float gatherSq = gather * gather;

            // --- Preferred under quarantine: hybrid GO proxies (no ECS map-body gather) ---
            var viz = EcsWorldVisualizer.Active;
            if (viz != null)
            {
                _asteroidProxyScratch.Clear();
                viz.CopyAsteroidProxyEntitiesTo(_asteroidProxyScratch);
                for (int i = 0; i < _asteroidProxyScratch.Count; i++)
                {
                    Entity e = _asteroidProxyScratch[i];
                    if (!viz.TryGetProxy(e, out GameObject proxy) || proxy == null || !proxy.activeInHierarchy)
                        continue;
                    if (!IsLiveMiningAsteroid(em, e))
                        continue;

                    Vector3 wp = proxy.transform.position;
                    if (hasLocalPos)
                    {
                        float d = DroneSwarmLogic.ToroidalDistanceXZ(
                            localPos.x, localPos.z, wp.x, wp.z, _mapW, _mapH);
                        if (d * d >= gatherSq)
                            continue;
                    }

                    float scale = em.HasComponent<LocalTransform>(e)
                        ? em.GetComponentData<LocalTransform>(e).Scale
                        : proxy.transform.lossyScale.x;
                    _cachedAsteroidAims.Add((
                        e,
                        new Vector3(wp.x, 0f, wp.z),
                        BodyCollisionMath.GetAsteroidBodyRadiusWorld(scale)));
                }
            }
            else if (!ClientJoinSettleCache.ShouldSkipMapBodyQueries && _queriesCreated)
            {
                // Editor / rare path when quarantine is off and visualizer missing.
                using var entities = _asteroidQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!IsLiveMiningAsteroid(em, entities[i]))
                        continue;
                    var xf = em.GetComponentData<LocalTransform>(entities[i]);
                    float3 p = xf.Position;
                    p.y = 0f;
                    if (hasLocalPos)
                    {
                        float d = DroneSwarmLogic.ToroidalDistanceXZ(
                            localPos.x, localPos.z, p.x, p.z, _mapW, _mapH);
                        if (d * d >= gatherSq)
                            continue;
                    }
                    _cachedAsteroidAims.Add((
                        entities[i],
                        new Vector3(p.x, 0f, p.z),
                        BodyCollisionMath.GetAsteroidBodyRadiusWorld(xf.Scale)));
                }
            }
        }

        void ApplyFacing(
            SlotVisual v,
            Vector3 planarWorldPos,
            Vector3 shipPos,
            Vector3 shipForward,
            float dt,
            bool hasSwarmTarget,
            Vector3 swarmTargetPos)
        {
            if (v.ItemType == StoreItemType.ShieldDrone)
            {
                if (hasSwarmTarget)
                {
                    v.Instance.transform.rotation = DroneSwarmPositioning.ComputeShieldFaceEnemyRotation(
                        planarWorldPos, swarmTargetPos, Vector3.up, _mapW, _mapH);
                    return;
                }

                Quaternion outward = DroneSwarmPositioning.ComputeShieldFaceOutwardRotation(
                    shipPos, planarWorldPos, Vector3.up);
                v.Instance.transform.rotation = Quaternion.Slerp(
                    v.Instance.transform.rotation, outward, Mathf.Clamp01(ShieldFacingTurnSpeed * 0.75f * dt));
                return;
            }

            // --- Fighter / mining: face the shared owner-leash target; snap when locked ---
            Vector3 lookDir = shipForward;
            lookDir.y = 0f;
            bool hasTarget = false;

            if (hasSwarmTarget)
            {
                lookDir = DroneSwarmLogic.ToroidalOffsetXZ(planarWorldPos, swarmTargetPos, _mapW, _mapH);
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

        static bool IsLiveMiningAsteroid(EntityManager em, Entity e)
        {
            if (e == Entity.Null || !em.Exists(e) || !em.HasComponent<AsteroidState>(e))
                return false;
            if (em.HasComponent<AsteroidClientCulledTag>(e))
                return false;
            var st = em.GetComponentData<AsteroidState>(e);
            if (!st.IsAliveForCombat)
                return false;
            if (em.HasComponent<LocalTransform>(e) &&
                em.GetComponentData<LocalTransform>(e).Scale <= AsteroidDeathPhysics.CulledTransformScale * 2f)
                return false;
            return true;
        }

        /// <summary>
        /// Shared fighter pack target from the owner ship (hull vs pad leash). Same pick as server combat.
        /// </summary>
        bool TryGetOwnerFighterSwarmTarget(
            Vector3 ownerPos,
            TeamId ownerTeam,
            int ownerNetId,
            out Vector3 pos,
            out float leash,
            out float targetStandoff)
        {
            pos = default;
            leash = DroneSwarmLogic.FighterEngageRange;
            targetStandoff = 0f;
            float bestSurface = float.MaxValue;
            bool found = false;
            foreach (var kv in _enemyPosByNetId)
            {
                if (ownerNetId > 0 && kv.Key == ownerNetId)
                    continue;
                if (_enemyTeamByNetId.TryGetValue(kv.Key, out var team) &&
                    ownerTeam != TeamId.None && team == ownerTeam)
                    continue;

                bool isPad = DroneSwarmLogic.IsDefensePadEnemyId(kv.Key);
                float standoff = ResolveCachedStandoff(ownerPos, kv.Key, kv.Value);
                float hull = DroneSwarmPositioning.HullRadiusFromApproachStandoff(standoff);
                float surface = DroneSwarmLogic.SurfaceDistanceXZ(
                    ownerPos, kv.Value, hull, _mapW, _mapH);
                float maxRange = DroneSwarmLogic.FighterEngageRange;
                if (surface >= maxRange || surface >= bestSurface)
                    continue;
                bestSurface = surface;
                pos = kv.Value;
                leash = DroneSwarmLogic.ResolveFighterLeash(isPad);
                targetStandoff = standoff;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Shield midpoint target: same enemy set as fighters, hull range
        /// <see cref="DroneSwarmLogic.ShieldEngageRange"/>.
        /// </summary>
        bool TryGetOwnerShieldSwarmTarget(
            Vector3 ownerPos,
            TeamId ownerTeam,
            int ownerNetId,
            out Vector3 pos,
            out float targetStandoff)
        {
            pos = default;
            targetStandoff = 0f;
            float bestSurface = float.MaxValue;
            bool found = false;
            foreach (var kv in _enemyPosByNetId)
            {
                if (ownerNetId > 0 && kv.Key == ownerNetId)
                    continue;
                if (_enemyTeamByNetId.TryGetValue(kv.Key, out var team) &&
                    ownerTeam != TeamId.None && team == ownerTeam)
                    continue;

                bool isPad = DroneSwarmLogic.IsDefensePadEnemyId(kv.Key);
                float standoff = ResolveCachedStandoff(ownerPos, kv.Key, kv.Value);
                float hull = DroneSwarmPositioning.HullRadiusFromApproachStandoff(standoff);
                float surface = DroneSwarmLogic.SurfaceDistanceXZ(
                    ownerPos, kv.Value, hull, _mapW, _mapH);
                float maxRange = isPad
                    ? DroneSwarmLogic.DefensePadEngageRange
                    : DroneSwarmLogic.ShieldEngageRange;
                if (surface >= maxRange || surface >= bestSurface)
                    continue;
                bestSurface = surface;
                pos = kv.Value;
                targetStandoff = standoff;
                found = true;
            }

            return found;
        }

        float ResolveCachedStandoff(Vector3 from, int enemyId, Vector3 pos)
        {
            if (!_enemyHullByNetId.TryGetValue(enemyId, out var hull))
                return DroneSwarmPositioning.ResolveSphereStandoff(0.35f);
            if (hull.HasOrientedHull)
            {
                return DroneSwarmPositioning.ResolveApproachStandoff(
                    from, pos, hull.Forward, hull.Right,
                    hull.CoveringExtentX, hull.CoveringExtentZ, hull.TransformScale,
                    hull.FallbackRadius, _mapW, _mapH);
            }

            return DroneSwarmPositioning.ResolveSphereStandoff(hull.FallbackRadius);
        }

        /// <summary>
        /// Shared mining pack target from the owner ship. Same pick as server combat.
        /// </summary>
        bool TryGetOwnerMiningSwarmTarget(Vector3 ownerPos, out Vector3 pos, out float targetStandoff)
        {
            pos = default;
            targetStandoff = 0f;
            float bestSurface = float.MaxValue;
            bool found = false;
            World world = ResolveShipWorld();
            var em = world != null && world.IsCreated ? world.EntityManager : default;
            bool hasEm = world != null && world.IsCreated;
            var viz = EcsWorldVisualizer.Active;
            for (int i = 0; i < _cachedAsteroidAims.Count; i++)
            {
                var rock = _cachedAsteroidAims[i];
                if (hasEm && !IsLiveMiningAsteroid(em, rock.entity))
                    continue;
                if (viz != null && viz.TryGetProxy(rock.entity, out GameObject proxy) &&
                    (proxy == null || !proxy.activeInHierarchy))
                    continue;
                float hull = rock.radius;
                float surface = DroneSwarmLogic.SurfaceDistanceXZ(
                    ownerPos, rock.pos, hull, _mapW, _mapH);
                if (surface >= DroneSwarmLogic.MiningEngageRange || surface >= bestSurface)
                    continue;
                bestSurface = surface;
                pos = rock.pos;
                targetStandoff = DroneSwarmPositioning.ResolveSphereStandoff(hull);
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

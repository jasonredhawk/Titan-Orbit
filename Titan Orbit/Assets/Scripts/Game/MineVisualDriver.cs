using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Shared;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only hybrid: Instantiates Bomb_4 meshes for every ghosted
    /// <see cref="DeployedMineElement"/> and plays the catalog / FireballsV2 explosion
    /// when <see cref="MineExplosionBridge"/> dequeues a burst.
    /// <para>
    /// [HYBRID] Presentation only — damage stays on <c>MineSimulationSystem</c>.
    /// Pose uses <see cref="ToroidalMapEcs.GetDisplayPosition"/> vs the local ship so mines
    /// on the far tile copy stay next to the camera. Join Team Crash!!! skips ship queries.
    /// </para>
    /// </summary>
    public sealed class MineVisualDriver : MonoBehaviour
    {
        const string EditorPrefabPath = "Assets/Sci-fi Mines/Prefabs/Bomb_4.prefab";
        const string FireballsV2Category = "FireballsV2";

        /// <summary>One spawned Bomb_4 keyed by owner + sequence.</summary>
        struct LiveVisual
        {
            public int OwnerNetworkId;
            public uint Sequence;
            public byte OwnerTeam;
            public int ItemLevel;
            public float VisualScale;
            public float ExplosionVfxScale;
            public float Damage;
            public float3 LogicalPos;
            public Vector3 PrefabLocalScale;
            public GameObject Instance;
        }

        static MineVisualDriver s_instance;

        readonly List<LiveVisual> _visuals = new List<LiveVisual>(32);
        readonly HashSet<ulong> _aliveKeys = new HashSet<ulong>();

        GameObject _prefab;
        Vector3 _prefabLocalScale = Vector3.one;
        MineTeamMaterials _teamMats;
        BulletVfxBank _bank;
        int _fireballsV2Index = -1;
        EntityQuery _shipQuery;
        World _cachedQueryWorld;
        bool _queriesCreated;
        float _mapW;
        float _mapH;

        /// <summary>
        /// [UNITY] After scene load — spawn a DontDestroyOnLoad driver so mines work without scene wiring.
        /// Dedicated server has no presentation.
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

            var go = new GameObject(nameof(MineVisualDriver));
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<MineVisualDriver>();
#endif
        }

        /// <summary>Resolves the Bomb_4 prefab and team materials once.</summary>
        void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            ResolveAssets();
        }

        /// <summary>Drops live meshes when the driver is destroyed.</summary>
        void OnDestroy()
        {
            ClearAll();
            DisposeQueries();
            if (s_instance == this)
                s_instance = null;
        }

        /// <summary>
        /// Prefers <see cref="MineCatalog.visualPrefab"/> so Windows players get the mesh.
        /// Falls back to the Sci-fi Mines Bomb_4 path in the Editor.
        /// Missing Inspector refs (stale fileID) are treated as null — they throw
        /// <see cref="MissingReferenceException"/> on <c>transform</c> even when <c>!= null</c>.
        /// </summary>
        void ResolveAssets()
        {
            var catalog = MineCatalog.LoadDefault();
            _prefab = TryLivePrefab(catalog != null ? catalog.visualPrefab : null);

#if UNITY_EDITOR
            if (_prefab == null)
                _prefab = TryLivePrefab(
                    UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(EditorPrefabPath));
#endif
            if (_prefab != null)
                _prefabLocalScale = _prefab.transform.localScale;

            _teamMats = MineTeamMaterials.LoadDefault();
            _bank = BulletVfxBank.LoadDefault();
            if (_bank != null)
                _bank.TryGetCategoryIndexByName(FireballsV2Category, out _fireballsV2Index);
        }

        /// <summary>
        /// Syncs meshes to ghosted mine buffers and drains explosion VFX.
        /// Skips ECS gathers during Join Team Instantiates.
        /// </summary>
        void LateUpdate()
        {
            DrainExplosions();

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            RefreshMapSize();
            if (!ToroidalMapEcs.IsValidMapSize(_mapW, _mapH))
                return;

            World world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
            {
                ClearAll();
                return;
            }

            EnsureQueries(world);
            if (!_queriesCreated)
                return;

            var em = world.EntityManager;
            using var ships = _shipQuery.ToEntityArray(Allocator.Temp);

            float3 localPos = float3.zero;
            bool hasLocal = ShipDisplayPose.HasLocalPose;
            if (hasLocal)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                localPos = new float3(p.x, p.y, p.z);
            }
            else if (EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 ecsPos))
            {
                localPos = new float3(ecsPos.x, ecsPos.y, ecsPos.z);
                hasLocal = true;
            }

            _aliveKeys.Clear();
            for (int s = 0; s < ships.Length; s++)
            {
                Entity ship = ships[s];
                if (!em.HasBuffer<DeployedMineElement>(ship))
                    continue;

                var buf = em.GetBuffer<DeployedMineElement>(ship);
                for (int i = 0; i < buf.Length; i++)
                {
                    var mine = buf[i];
                    ulong key = PackKey(mine.OwnerNetworkId, mine.Sequence);
                    _aliveKeys.Add(key);
                    SyncVisual(in mine, hasLocal ? localPos : mine.Position);
                }
            }

            // --- Despawn missing mines (play VFX if the RPC has not already) ---
            for (int i = _visuals.Count - 1; i >= 0; i--)
            {
                var v = _visuals[i];
                if (_aliveKeys.Contains(PackKey(v.OwnerNetworkId, v.Sequence)))
                    continue;

                MineExplosionBridge.Enqueue(new MineExplosionBridge.Request
                {
                    Sequence = v.Sequence,
                    Position = v.LogicalPos,
                    OwnerTeam = v.OwnerTeam,
                    ItemLevel = v.ItemLevel,
                    VisualScale = v.VisualScale,
                    ExplosionVfxScale = v.ExplosionVfxScale,
                    Damage = v.Damage,
                });
                if (v.Instance != null)
                    Destroy(v.Instance);
                _visuals.RemoveAt(i);
            }
        }

        /// <summary>Creates or updates one Bomb_4 for a ghosted mine.</summary>
        void SyncVisual(in DeployedMineElement mine, float3 referencePos)
        {
            int idx = FindVisual(mine.OwnerNetworkId, mine.Sequence);
            if (idx < 0)
            {
                if (_prefab == null)
                    return;

                GameObject go = Instantiate(_prefab);
                go.name = $"Mine_{mine.OwnerNetworkId}_{mine.Sequence}";
                ApplyTeamMaterial(go, (TeamId)mine.OwnerTeam);
                var created = new LiveVisual
                {
                    OwnerNetworkId = mine.OwnerNetworkId,
                    Sequence = mine.Sequence,
                    OwnerTeam = mine.OwnerTeam,
                    ItemLevel = mine.ItemLevel,
                    VisualScale = mine.VisualScale,
                    ExplosionVfxScale = mine.ExplosionVfxScale,
                    Damage = mine.Damage,
                    LogicalPos = mine.Position,
                    PrefabLocalScale = _prefabLocalScale,
                    Instance = go,
                };
                _visuals.Add(created);
                idx = _visuals.Count - 1;
            }

            var v = _visuals[idx];
            v.LogicalPos = mine.Position;
            v.VisualScale = mine.VisualScale;
            v.ExplosionVfxScale = mine.ExplosionVfxScale;
            v.Damage = mine.Damage;
            _visuals[idx] = v;

            if (v.Instance == null)
                return;

            float3 display = ToroidalMapEcs.GetDisplayPosition(mine.Position, referencePos, _mapW, _mapH);
            v.Instance.transform.position = new Vector3(display.x, display.y, display.z);
            float mul = math.max(0.05f, mine.VisualScale);
            v.Instance.transform.localScale = v.PrefabLocalScale * mul;
        }

        /// <summary>Plays queued bursts at the display-unwrapped explode point.</summary>
        void DrainExplosions()
        {
            float3 localPos = float3.zero;
            bool hasLocal = ShipDisplayPose.HasLocalPose;
            if (hasLocal)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                localPos = new float3(p.x, p.y, p.z);
            }

            while (MineExplosionBridge.TryDequeue(out var req))
            {
                // Keep Sequence in the seen-set so a later buffer-despawn does not play twice.
                PlayExplosion(in req, hasLocal ? localPos : req.Position);
            }
        }

        /// <summary>
        /// Instantiates the catalog team explosion (FireballsV2 impact), or the bank
        /// impact if the catalog slot is empty. Size is mine <c>visualScale</c> times the
        /// row's <c>explosionVfxScale</c> (burst vs a 1× mine).
        /// Does <b>not</b> call <c>SpawnBulletImpactVfx</c> — that path multiplies the bank 0.25 global.
        /// </summary>
        void PlayExplosion(in MineExplosionBridge.Request req, float3 referencePos)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;

            float3 logical = req.Position;
            logical.y = 0f;
            float3 display = ToroidalMapEcs.IsValidMapSize(_mapW, _mapH)
                ? ToroidalMapEcs.GetDisplayPosition(logical, referencePos, _mapW, _mapH)
                : logical;

            var team = (TeamId)req.OwnerTeam;
            var stats = MineCatalog.Get(req.ItemLevel);
            var catalog = MineCatalog.LoadDefault();
            GameObject prefab = TryLivePrefab(catalog != null ? catalog.GetExplosionVfx(team) : null);
            if (prefab == null && _bank != null && _fireballsV2Index >= 0)
                prefab = TryLivePrefab(_bank.GetImpactPrefab(_fireballsV2Index, team));

            float mineScale = req.VisualScale > 0.01f
                ? req.VisualScale
                : math.max(0.05f, stats.visualScale);
            float burst = req.ExplosionVfxScale > 0.01f
                ? req.ExplosionVfxScale
                : math.max(0.05f, stats.explosionVfxScale);
            float global = MineCatalog.GetExplosionGlobalScale();
            float scale = math.max(0.05f, mineScale * burst * global);
            float pitch = BulletVisualFactory.GetImpactSoundPitch(req.Damage);
            Vector3 pos = new Vector3(display.x, 0f, display.z);
            if (prefab != null)
                BulletVisualFactory.SpawnImpactAt(pos, prefab, pitch, scale, BulletVisualFactory.DefaultImpactDuration);
        }

        /// <summary>Swaps every renderer on the Bomb_4 instance to the team material.</summary>
        void ApplyTeamMaterial(GameObject root, TeamId team)
        {
            if (root == null || _teamMats == null)
                return;

            Material mat = _teamMats.GetMaterialForTeam(team);
            if (mat == null)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].sharedMaterial = mat;
            }
        }

        /// <summary>Finds a live visual by owner + sequence, or -1.</summary>
        int FindVisual(int ownerNetworkId, uint sequence)
        {
            for (int i = 0; i < _visuals.Count; i++)
            {
                if (_visuals[i].OwnerNetworkId == ownerNetworkId && _visuals[i].Sequence == sequence)
                    return i;
            }

            return -1;
        }

        /// <summary>Packs owner + sequence into one set key.</summary>
        static ulong PackKey(int ownerNetworkId, uint sequence)
        {
            return ((ulong)(uint)ownerNetworkId << 32) | sequence;
        }

        /// <summary>
        /// True live prefab, or null. Stale serialized refs compare non-null then throw on
        /// <c>transform</c> — catch that so Awake cannot spam MissingReferenceException.
        /// </summary>
        static GameObject TryLivePrefab(GameObject prefab)
        {
            if (prefab == null)
                return null;
            try
            {
                _ = prefab.transform;
                return prefab;
            }
            catch (MissingReferenceException)
            {
                return null;
            }
        }

        /// <summary>Destroys every live mesh (world teardown / no client world).</summary>
        void ClearAll()
        {
            for (int i = 0; i < _visuals.Count; i++)
            {
                if (_visuals[i].Instance != null)
                    Destroy(_visuals[i].Instance);
            }

            _visuals.Clear();
        }

        /// <summary>Caches a ship query on the client world.</summary>
        void EnsureQueries(World world)
        {
            if (_queriesCreated && _cachedQueryWorld == world && _shipQuery.IsEmptyIgnoreFilter == false)
                return;
            if (_queriesCreated && _cachedQueryWorld == world)
                return;

            DisposeQueries();
            _shipQuery = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            _cachedQueryWorld = world;
            _queriesCreated = true;
        }

        /// <summary>Releases the cached query when the world changes.</summary>
        void DisposeQueries()
        {
            if (_queriesCreated && _shipQuery != default)
                _shipQuery.Dispose();
            _queriesCreated = false;
            _cachedQueryWorld = null;
        }

        /// <summary>Reads map size from session meta, then the client MapStateSingleton.</summary>
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
            if (ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                _mapW = map.MapWidth;
                _mapH = map.MapHeight;
            }
        }
    }
}

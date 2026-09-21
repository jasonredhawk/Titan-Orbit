using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only: clones prefab components on a dying ship, groups nearby ones into
    /// 2–5 rigid chunks, and flies those chunks as non-interactive debris until respawn.
    /// Fire/V2 one-shots retrigger at torn contacts (parts that used to touch).
    /// Chunks bounce off ship / asteroid ghosts with toroidal sphere math (no PhysicsCollider,
    /// nothing on the server). Motion is seeded from <see cref="ShipDeathVfxState.Packed"/>
    /// so all clients match when that ghost word has arrived; if it is still 0 we still
    /// explode with a local fallback seed so a late Packed cannot vanish the hull.
    /// <para>
    /// Hooked from <see cref="EcsWorldVisualizer"/> (no extra ship entity queries).
    /// The visualizer hides the live proxy only after <see cref="TryBegin"/> has a wreck.
    /// </para>
    /// </summary>
    public sealed class ShipDeathDebrisDriver : MonoBehaviour
    {
        const string FireV2Folder =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Explosions/Fire/V2/";
        const string DebrisRootName = "ShipDeathDebris";
        const int ExplosionPrewarmCount = 8;
        const float MinChunkRadius = 0.35f;

        struct Piece
        {
            public GameObject Go;
        }

        struct SeamFire
        {
            public int ChunkIndex;
            public float3 LocalOffset;
            public float NextTime;
            public float Interval;
            public float Scale;
        }

        struct Chunk
        {
            public GameObject Root;
            public float3 LogicalPos;
            public quaternion Rotation;
            public float3 Velocity;
            public float3 SpinDegPerSec;
            public float Radius;
        }

        struct VisualBody
        {
            public Entity Entity;
            public float3 LogicalPos;
            public float Radius;
        }

        struct Wreck
        {
            public Entity Ship;
            public uint Packed;
            public float3 CenterLogical;
            public List<Chunk> Chunks;
            public List<Piece> Pieces;
            public List<SeamFire> SeamFires;
            public float StartTime;
            public byte Team;
        }

        static ShipDeathDebrisDriver s_instance;
        static readonly List<Transform> s_partScratch = new List<Transform>(64);
        static readonly List<float3> s_offsetScratch = new List<float3>(64);
        static readonly List<int> s_clusterScratch = new List<int>(64);
        static readonly List<float3> s_chunkComScratch = new List<float3>(8);
        static readonly List<int> s_chunkSizeScratch = new List<int>(8);
        static readonly List<int> s_chunkIndexScratch = new List<int>(8);
        static readonly List<Entity> s_proxyScratch = new List<Entity>(64);
        static readonly List<VisualBody> s_bodyScratch = new List<VisualBody>(64);
        static readonly List<int> s_seamAScratch = new List<int>(8);
        static readonly List<int> s_seamBScratch = new List<int>(8);

        readonly Dictionary<Entity, Wreck> _wrecks = new Dictionary<Entity, Wreck>(16);
        readonly List<Entity> _endScratch = new List<Entity>(8);
        readonly GameObject[] _explosionByTeam = new GameObject[6];
        ShipDeathDebrisSettings _settings;
        float _mapW;
        float _mapH;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (s_instance != null)
                return;

            var go = new GameObject(nameof(ShipDeathDebrisDriver));
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<ShipDeathDebrisDriver>();
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
            _settings = ShipDeathDebrisSettings.LoadOrDefault();
            ResolveExplosionPrefabs();
            EnqueueExplosionPrewarm();
        }

        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            EndAll();
        }

        /// <summary>
        /// Snapshot the live proxy and start debris. Returns true when this death already has
        /// a wreck (new or existing). Does not require <see cref="ShipDeathVfxState.Packed"/>
        /// — a 0 word still explodes with a fallback seed.
        /// </summary>
        /// <param name="ship">Dying ship ghost.</param>
        /// <param name="proxy">Hybrid hull GameObject. Force-activated for the snapshot if hidden.</param>
        /// <param name="em">Client presentation EntityManager.</param>
        /// <returns>True when flying pieces exist for this ship.</returns>
        public static bool TryBegin(Entity ship, GameObject proxy, EntityManager em)
        {
            if (s_instance == null || ship == Entity.Null || proxy == null)
                return false;
            if (!em.Exists(ship) || !em.HasComponent<ShipState>(ship))
                return false;

            // --- Already exploding this death ---
            // Do not snapshot again: the visualizer hides the hull after the first success,
            // and a second Collect on an inactive proxy is how the second death used to vanish.
            if (s_instance._wrecks.ContainsKey(ship))
                return true;

            uint packed = 0;
            if (em.HasComponent<ShipDeathVfxState>(ship))
                packed = em.GetComponentData<ShipDeathVfxState>(ship).Packed;

            return s_instance.Begin(ship, proxy, em, packed);
        }

        /// <summary>True when this ship already has flying debris (hide the live hull).</summary>
        public static bool IsPlaying(Entity ship)
        {
            return s_instance != null && ship != Entity.Null && s_instance._wrecks.ContainsKey(ship);
        }

        /// <summary>Destroys debris for this ship (respawn or proxy teardown).</summary>
        public static void End(Entity ship)
        {
            if (s_instance == null || ship == Entity.Null)
                return;
            s_instance.DestroyWreck(ship);
        }

        /// <summary>
        /// Clones hull modules, kicks them outward, and stores the wreck. Force-activates
        /// <paramref name="proxy"/> so an already-hidden hull can still be snapshotted.
        /// </summary>
        /// <returns>True when at least one piece was created.</returns>
        bool Begin(Entity ship, GameObject proxy, EntityManager em, uint packed)
        {
            if (_wrecks.ContainsKey(ship))
                return true;

            // --- Packed may still be 0 on the death frame ---
            // [NETCODE] IsDead lives on ShipState; Packed is a second ghost component.
            // After respawn Packed is cleared to 0, then written again on the next death.
            // The first death usually delivered both in one snapshot. Later deaths can
            // show IsDead a tick earlier — we still explode, then keep this wreck if
            // Packed arrives later (do not restart from the hidden hull).
            if (packed == 0)
                packed = FallbackPacked(ship);

            bool isMega = em.HasComponent<MegaShipState>(ship)
                          && em.GetComponentData<MegaShipState>(ship).IsMega;
            string prefix = ResolveFamilyPrefix(em, ship);

            // [UNITY] Instantiate copies activeSelf. A child of an inactive proxy is
            // inactive-in-hierarchy; force the hull on so clones spawn visible.
            bool restoredActive = false;
            if (!proxy.activeSelf)
            {
                proxy.SetActive(true);
                restoredActive = true;
            }

            ShipDeathDebrisParts.CollectOrFallback(proxy.transform, isMega, prefix, s_partScratch);
            if (s_partScratch.Count == 0)
            {
                if (restoredActive)
                    proxy.SetActive(false);
                return false;
            }

            ShipDeathVfxState.Unpack(packed, out uint seed, out float2 impulseDir, out float power01);

            float3 center = proxy.transform.position;
            if (em.HasComponent<LocalTransform>(ship))
            {
                var lt = em.GetComponentData<LocalTransform>(ship);
                center = lt.Position;
            }

            float3 shipVel = float3.zero;
            if (em.HasComponent<ShipKinematics>(ship))
                shipVel = em.GetComponentData<ShipKinematics>(ship).Velocity;

            float hullRadius = 1.5f;
            if (em.HasComponent<LocalTransform>(ship))
            {
                float scale = em.GetComponentData<LocalTransform>(ship).Scale;
                hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(math.max(0.25f, scale));
            }

            byte team = 0;
            if (em.HasComponent<ShipState>(ship))
                team = (byte)em.GetComponentData<ShipState>(ship).Team;

            var wreck = new Wreck
            {
                Ship = ship,
                Packed = packed,
                CenterLogical = center,
                Chunks = new List<Chunk>(8),
                Pieces = new List<Piece>(s_partScratch.Count),
                SeamFires = new List<SeamFire>(8),
                StartTime = Time.time,
                Team = team,
            };

            CompactPartScratch();
            var collected = new HashSet<Transform>(s_partScratch);

            // --- Spatial chunks ---
            // Nearby modules share a root so they tumble as wreckage, not as a bag of
            // equal-force parts. Cluster count and seams come from the death seed.
            s_offsetScratch.Clear();
            for (int i = 0; i < s_partScratch.Count; i++)
            {
                float3 offset = (float3)s_partScratch[i].position - center;
                offset.y = 0f;
                s_offsetScratch.Add(offset);
            }

            int clusterCount = ShipDeathDebrisMath.AssignSpatialClusters(
                seed,
                s_offsetScratch,
                _settings.ClusterCountMin,
                _settings.ClusterCountMax,
                s_clusterScratch);

            BuildChunkCenters(center, clusterCount);

            for (int c = 0; c < clusterCount; c++)
            {
                if (s_chunkSizeScratch[c] <= 0)
                    continue;

                float3 com = s_chunkComScratch[c];
                float3 offset = com - center;
                offset.y = 0f;
                ShipDeathDebrisMath.ComputeClusterLaunch(
                    seed,
                    c,
                    offset,
                    impulseDir,
                    power01,
                    hullRadius,
                    shipVel,
                    _settings,
                    out float3 velocity,
                    out float3 spin);

                var root = new GameObject($"{DebrisRootName}_Chunk_{c}");
                root.transform.SetParent(transform, false);
                root.transform.SetPositionAndRotation(
                    new Vector3(com.x, com.y, com.z),
                    Quaternion.identity);

                wreck.Chunks.Add(new Chunk
                {
                    Root = root,
                    LogicalPos = com,
                    Rotation = quaternion.identity,
                    Velocity = velocity,
                    SpinDegPerSec = spin,
                    Radius = MinChunkRadius,
                });
            }

            s_chunkIndexScratch.Clear();
            int written = 0;
            for (int c = 0; c < clusterCount; c++)
            {
                if (s_chunkSizeScratch[c] <= 0)
                    s_chunkIndexScratch.Add(0);
                else
                    s_chunkIndexScratch.Add(written++);
            }

            for (int i = 0; i < s_partScratch.Count; i++)
            {
                Transform src = s_partScratch[i];
                int cluster = i < s_clusterScratch.Count ? s_clusterScratch[i] : 0;
                int chunkIndex = cluster >= 0 && cluster < s_chunkIndexScratch.Count
                    ? s_chunkIndexScratch[cluster]
                    : 0;
                if (chunkIndex < 0 || chunkIndex >= wreck.Chunks.Count)
                    chunkIndex = 0;

                GameObject clone = Instantiate(src.gameObject);
                clone.name = src.name + "_Debris";
                clone.SetActive(true);
                StripCollectedDescendants(clone.transform, src, collected);
                StripInteractive(clone);

                clone.transform.SetPositionAndRotation(src.position, src.rotation);
                clone.transform.localScale = src.lossyScale;
                Transform parent = wreck.Chunks.Count > 0
                    ? wreck.Chunks[chunkIndex].Root.transform
                    : transform;
                clone.transform.SetParent(parent, true);

                if (wreck.Chunks.Count > 0)
                {
                    Chunk sized = wreck.Chunks[chunkIndex];
                    float3 com = sized.LogicalPos;
                    float3 part = (float3)src.position;
                    float radial = math.length(new float2(part.x - com.x, part.z - com.z));
                    sized.Radius = math.max(sized.Radius, radial + 0.3f);
                    wreck.Chunks[chunkIndex] = sized;
                }

                wreck.Pieces.Add(new Piece
                {
                    Go = clone,
                });
            }

            if (wreck.Pieces.Count == 0)
                return false;

            QueueSeamFires(wreck, seed, hullRadius);
            _wrecks[ship] = wreck;

            PlayBurst(center, hullRadius, power01, (TeamId)team);
            var audio = AudioManager.GetOrFind();
            if (audio != null)
                audio.PlayShipDeathSound();
            return true;
        }

        /// <summary>
        /// Local seed when the ghosted Packed word is still 0. Motion will not match other
        /// clients for this one death; seeing a breakup is the priority.
        /// </summary>
        static uint FallbackPacked(Entity ship)
        {
            uint seed = (uint)math.max(1, ship.Index);
            seed ^= (uint)(Time.frameCount * 747796405);
            if (seed == 0)
                seed = 1;
            return ShipDeathVfxState.Pack(seed, float2.zero, 0f);
        }

        void LateUpdate()
        {
            // [TITAN-ORBIT] Map size from ToroidalMapEcs (MapStateSingleton / session meta).
            RefreshMapSize();
            if (_wrecks.Count == 0)
                return;

            // --- Drop wrecks whose ship is alive again ---
            // [HYBRID] Visualizer already calls End on respawn. This catches frames where
            // the proxy sync was skipped (Instantiates backlog) so the next death can start.
            EndWrecksIfShipAlive();

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            float3 reference = float3.zero;
            bool hasRef = ShipDisplayPose.HasLocalPose;
            if (hasRef)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                reference = new float3(p.x, p.y, p.z);
            }

            // Visualizer proxies only — no extra asteroid EntityQuery (mapW/mapH from ToroidalMapEcs).
            CollectVisualBodies();

            _endScratch.Clear();
            foreach (var kv in _wrecks)
            {
                var wreck = kv.Value;
                if (wreck.Pieces == null || wreck.Chunks == null)
                {
                    _endScratch.Add(kv.Key);
                    continue;
                }

                for (int i = 0; i < wreck.Chunks.Count; i++)
                {
                    Chunk chunk = wreck.Chunks[i];
                    if (chunk.Root == null)
                        continue;

                    ShipDeathDebrisMath.IntegrateDrag(
                        ref chunk.Velocity,
                        ref chunk.SpinDegPerSec,
                        dt,
                        _settings.LinearDrag,
                        _settings.AngularDrag);

                    chunk.LogicalPos += chunk.Velocity * dt;
                    chunk.LogicalPos.y = 0f;
                    chunk.Rotation = math.mul(
                        chunk.Rotation,
                        quaternion.Euler(math.radians(chunk.SpinDegPerSec * dt)));

                    ResolveChunkAgainstBodies(ref chunk, wreck.Ship);

                    float3 display = chunk.LogicalPos;
                    if (hasRef && ToroidalMapEcs.IsValidMapSize(_mapW, _mapH))
                        display = ToroidalMapEcs.GetDisplayPosition(chunk.LogicalPos, reference, _mapW, _mapH);

                    chunk.Root.transform.SetPositionAndRotation(
                        new Vector3(display.x, display.y, display.z),
                        chunk.Rotation);
                    wreck.Chunks[i] = chunk;
                }

                TickSeamFires(wreck, reference, hasRef);
            }

            for (int i = 0; i < _endScratch.Count; i++)
                DestroyWreck(_endScratch[i]);
        }

        /// <summary>
        /// Places repeating Fire/V2 one-shots on the faces that used to touch — torn
        /// cross-cluster contacts first. Prefabs stay one-shots; we retrigger them.
        /// </summary>
        void QueueSeamFires(Wreck wreck, uint seed, float hullRadius)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;
            if (wreck.SeamFires == null || wreck.Chunks == null || wreck.Chunks.Count == 0)
                return;

            int maxEmitters = Mathf.Max(0, _settings.MaxSeamBursts);
            if (maxEmitters <= 0)
                return;

            int maxPairs = math.max(1, (maxEmitters + 1) / 2);
            ShipDeathDebrisMath.CollectSeamContacts(
                s_offsetScratch,
                s_clusterScratch,
                maxPairs,
                s_seamAScratch,
                s_seamBScratch);

            int emitter = 0;
            for (int i = 0; i < s_seamAScratch.Count && emitter < maxEmitters; i++)
            {
                TryAddSeamFire(wreck, seed, s_seamAScratch[i], hullRadius, ref emitter);
                if (emitter >= maxEmitters)
                    break;
                if (s_seamBScratch[i] != s_seamAScratch[i])
                    TryAddSeamFire(wreck, seed, s_seamBScratch[i], hullRadius, ref emitter);
            }
        }

        void TryAddSeamFire(Wreck wreck, uint seed, int partIndex, float hullRadius, ref int emitter)
        {
            if (partIndex < 0 || partIndex >= s_partScratch.Count)
                return;
            Transform src = s_partScratch[partIndex];
            if (src == null)
                return;

            int cluster = partIndex < s_clusterScratch.Count ? s_clusterScratch[partIndex] : 0;
            int chunkIndex = cluster >= 0 && cluster < s_chunkIndexScratch.Count
                ? s_chunkIndexScratch[cluster]
                : 0;
            if (chunkIndex < 0 || chunkIndex >= wreck.Chunks.Count)
                return;

            ShipDeathDebrisMath.ComputeSeamBurst(
                seed,
                emitter,
                _settings,
                out float firstDelay,
                out float interval,
                out float scale,
                out float3 jitter);

            float3 com = wreck.Chunks[chunkIndex].LogicalPos;
            float3 world = (float3)src.position;
            float3 local = world - com;
            local.y = 0f;
            local += jitter * math.max(0.35f, hullRadius);

            wreck.SeamFires.Add(new SeamFire
            {
                ChunkIndex = chunkIndex,
                LocalOffset = local,
                NextTime = wreck.StartTime + firstDelay,
                Interval = interval,
                Scale = scale,
            });
            emitter++;
        }

        void TickSeamFires(Wreck wreck, float3 reference, bool hasRef)
        {
            if (wreck.SeamFires == null || wreck.Chunks == null)
                return;

            float now = Time.time;
            GameObject prefab = PickExplosionPrefab((TeamId)wreck.Team);
            if (prefab == null)
                return;

            for (int i = 0; i < wreck.SeamFires.Count; i++)
            {
                SeamFire fire = wreck.SeamFires[i];
                if (now < fire.NextTime)
                    continue;
                if (fire.ChunkIndex < 0 || fire.ChunkIndex >= wreck.Chunks.Count)
                    continue;

                Chunk chunk = wreck.Chunks[fire.ChunkIndex];
                if (chunk.Root == null)
                    continue;

                float3 logical = chunk.LogicalPos + math.mul(chunk.Rotation, fire.LocalOffset);
                logical.y = 0f;
                float3 display = logical;
                if (hasRef && ToroidalMapEcs.IsValidMapSize(_mapW, _mapH))
                    display = ToroidalMapEcs.GetDisplayPosition(logical, reference, _mapW, _mapH);

                PlaySeamBurst(
                    new Vector3(display.x, 0f, display.z),
                    prefab,
                    fire.Scale,
                    chunk.Root.transform);

                fire.Interval *= math.max(1f, _settings.SeamRepeatGrow);
                fire.NextTime = now + fire.Interval;
                wreck.SeamFires[i] = fire;
            }
        }

        void PlaySeamBurst(Vector3 display, GameObject prefab, float scale, Transform attach)
        {
            if (prefab == null)
                return;
            if (!BulletOneShotVfxPool.TryRent(prefab, out GameObject go) || go == null)
                return;

            go.transform.SetPositionAndRotation(display, Quaternion.identity);
            VfxUrpCompat.ApplyImpactVisualScale(go, scale);
            MuteAudio(go);
            VfxUrpCompat.PrepareVfxInstance(go);
            if (attach != null)
                go.transform.SetParent(attach, true);
            BulletOneShotVfxPool.ScheduleReturn(go, _settings.SeamBurstDuration);
        }

        void PlayBurst(float3 logical, float hullRadius, float power01, TeamId team)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;

            GameObject prefab = PickExplosionPrefab(team);
            if (prefab == null)
                return;

            float3 reference = logical;
            if (ShipDisplayPose.HasLocalPose)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                reference = new float3(p.x, p.y, p.z);
            }

            float3 display = logical;
            if (ToroidalMapEcs.IsValidMapSize(_mapW, _mapH))
                display = ToroidalMapEcs.GetDisplayPosition(logical, reference, _mapW, _mapH);

            float scale = (_settings.BurstScale + _settings.BurstScaleFromPower * power01)
                          * math.max(0.5f, hullRadius / 1.5f);
            float pitch = BulletVisualFactory.GetImpactSoundPitch(power01 * ShipDeathVfxState.PowerReference);
            BulletVisualFactory.SpawnImpactAt(
                new Vector3(display.x, 0f, display.z),
                prefab,
                pitch,
                scale,
                BulletVisualFactory.DefaultImpactDuration);
        }

        /// <summary>
        /// Ends any wreck whose ghost is gone or <see cref="ShipState.IsDead"/> is false
        /// so a later death is not skipped as "already playing".
        /// </summary>
        void EndWrecksIfShipAlive()
        {
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            _endScratch.Clear();
            foreach (var kv in _wrecks)
            {
                Entity ship = kv.Key;
                if (!em.Exists(ship) || !em.HasComponent<ShipState>(ship))
                {
                    _endScratch.Add(ship);
                    continue;
                }

                if (!em.GetComponentData<ShipState>(ship).IsDead)
                    _endScratch.Add(ship);
            }

            for (int i = 0; i < _endScratch.Count; i++)
                DestroyWreck(_endScratch[i]);
        }

        void DestroyWreck(Entity ship)
        {
            if (!_wrecks.TryGetValue(ship, out var wreck))
                return;
            _wrecks.Remove(ship);

            if (wreck.Chunks != null)
            {
                for (int i = 0; i < wreck.Chunks.Count; i++)
                {
                    if (wreck.Chunks[i].Root != null)
                        Destroy(wreck.Chunks[i].Root);
                }
            }
        }

        static void CompactPartScratch()
        {
            int write = 0;
            for (int i = 0; i < s_partScratch.Count; i++)
            {
                if (s_partScratch[i] != null)
                    s_partScratch[write++] = s_partScratch[i];
            }

            if (write < s_partScratch.Count)
                s_partScratch.RemoveRange(write, s_partScratch.Count - write);
        }

        /// <summary>
        /// Averages collected part positions per cluster label into
        /// <see cref="s_chunkComScratch"/>. Empty labels stay at the ship center.
        /// </summary>
        static void BuildChunkCenters(float3 shipCenter, int clusterCount)
        {
            s_chunkComScratch.Clear();
            s_chunkSizeScratch.Clear();
            int k = math.max(0, clusterCount);
            for (int c = 0; c < k; c++)
            {
                s_chunkComScratch.Add(float3.zero);
                s_chunkSizeScratch.Add(0);
            }

            int partCount = math.min(s_partScratch.Count, s_clusterScratch.Count);
            for (int i = 0; i < partCount; i++)
            {
                Transform src = s_partScratch[i];
                if (src == null)
                    continue;

                int c = s_clusterScratch[i];
                if (c < 0 || c >= k)
                    continue;

                s_chunkComScratch[c] += (float3)src.position;
                s_chunkSizeScratch[c]++;
            }

            for (int c = 0; c < k; c++)
            {
                if (s_chunkSizeScratch[c] <= 0)
                {
                    s_chunkComScratch[c] = shipCenter;
                    continue;
                }

                s_chunkComScratch[c] /= s_chunkSizeScratch[c];
            }
        }

        void EndAll()
        {
            _endScratch.Clear();
            foreach (var key in _wrecks.Keys)
                _endScratch.Add(key);
            for (int i = 0; i < _endScratch.Count; i++)
                DestroyWreck(_endScratch[i]);
        }

        void RefreshMapSize()
        {
            if (ToroidalMapEcs.TryGetMapSize(out float w, out float h))
            {
                _mapW = w;
                _mapH = h;
            }
        }

        /// <summary>
        /// Prefers authored Fire/V2 slots, then Editor folder paths so Play Mode works
        /// before the Resources asset is wired. Same team map as asteroid death V1.
        /// </summary>
        void ResolveExplosionPrefabs()
        {
            BindExplosionSlot(1, _settings != null ? _settings.ExplosionVfxRed : null, "RedFireImpactV2.prefab");
            BindExplosionSlot(2, _settings != null ? _settings.ExplosionVfxBlue : null, "BlueFireImpactV2.prefab");
            BindExplosionSlot(3, _settings != null ? _settings.ExplosionVfxGreen : null, "GreenFireImpactV2.prefab");
            BindExplosionSlot(4, _settings != null ? _settings.ExplosionVfxYellow : null, "YellowFireImpactV2.prefab");
            BindExplosionSlot(5, _settings != null ? _settings.ExplosionVfxPurple : null, "PurpleFireImpactV2.prefab");

            GameObject first = null;
            for (int i = 1; i < _explosionByTeam.Length; i++)
            {
                if (_explosionByTeam[i] == null)
                    continue;
                first = _explosionByTeam[i];
                break;
            }

            _explosionByTeam[0] = first;
        }

        void BindExplosionSlot(int index, GameObject authored, string editorFileName)
        {
            GameObject live = TryLivePrefab(authored);
#if UNITY_EDITOR
            if (live == null)
            {
                live = TryLivePrefab(
                    UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(FireV2Folder + editorFileName));
            }
#endif
            _explosionByTeam[index] = live;
        }

        void EnqueueExplosionPrewarm()
        {
            for (int i = 0; i < _explosionByTeam.Length; i++)
            {
                if (_explosionByTeam[i] != null)
                    BulletOneShotVfxPool.EnqueuePrewarm(_explosionByTeam[i], ExplosionPrewarmCount);
            }
        }

        GameObject PickExplosionPrefab(TeamId team)
        {
            if (_explosionByTeam[0] == null)
                ResolveExplosionPrefabs();

            int index = (int)team;
            if (index >= 0 && index < _explosionByTeam.Length && _explosionByTeam[index] != null)
                return _explosionByTeam[index];

            if (_settings != null)
            {
                GameObject fromSettings = TryLivePrefab(_settings.GetExplosionVfx(team));
                if (fromSettings != null)
                    return fromSettings;
            }

            return _explosionByTeam[0];
        }

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

        /// <summary>
        /// Ships + live asteroids from <see cref="EcsWorldVisualizer"/> proxies, sorted by
        /// entity index so bounce order matches on every client.
        /// </summary>
        void CollectVisualBodies()
        {
            s_bodyScratch.Clear();
            var viz = EcsWorldVisualizer.Active;
            if (viz == null)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            bool skipShips = ClientJoinSettleCache.ShouldSkipShipEntityQueries;
            bool skipAsteroids = ClientJoinSettleCache.ShouldSkipMapBodyQueries;

            if (!skipShips)
            {
                viz.CopyShipProxyEntitiesTo(s_proxyScratch);
                for (int i = 0; i < s_proxyScratch.Count; i++)
                    TryAddShipBody(em, viz, s_proxyScratch[i]);
            }

            if (!skipAsteroids)
            {
                viz.CopyAsteroidProxyEntitiesTo(s_proxyScratch);
                for (int i = 0; i < s_proxyScratch.Count; i++)
                    TryAddAsteroidBody(em, viz, s_proxyScratch[i]);
            }

            s_bodyScratch.Sort(CompareVisualBody);
        }

        void TryAddShipBody(EntityManager em, EcsWorldVisualizer viz, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity))
                return;
            if (!em.HasComponent<ShipTag>(entity) || !em.HasComponent<ShipState>(entity))
                return;
            if (em.GetComponentData<ShipState>(entity).IsDead)
                return;
            if (!viz.TryGetProxy(entity, out GameObject proxy) || proxy == null || !proxy.activeInHierarchy)
                return;

            float3 pos;
            float radius;
            if (em.HasComponent<LocalTransform>(entity))
            {
                var lt = em.GetComponentData<LocalTransform>(entity);
                pos = lt.Position;
                radius = BodyCollisionMath.GetShipHullRadiusWorld(math.max(0.25f, lt.Scale));
            }
            else
            {
                Vector3 p = proxy.transform.position;
                pos = new float3(p.x, 0f, p.z);
                radius = BodyCollisionMath.GetShipHullRadiusWorld(1f);
            }

            pos.y = 0f;
            s_bodyScratch.Add(new VisualBody
            {
                Entity = entity,
                LogicalPos = pos,
                Radius = radius,
            });
        }

        void TryAddAsteroidBody(EntityManager em, EcsWorldVisualizer viz, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity))
                return;
            if (!em.HasComponent<AsteroidTag>(entity) || !em.HasComponent<AsteroidState>(entity))
                return;
            if (!em.GetComponentData<AsteroidState>(entity).IsAliveForCombat)
                return;
            if (em.HasComponent<AsteroidClientCulledTag>(entity))
                return;
            if (!viz.TryGetProxy(entity, out GameObject proxy) || proxy == null || !proxy.activeInHierarchy)
                return;

            float3 pos;
            float radius;
            if (em.HasComponent<LocalTransform>(entity))
            {
                var lt = em.GetComponentData<LocalTransform>(entity);
                pos = lt.Position;
                radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(math.max(0.01f, lt.Scale));
            }
            else
            {
                Vector3 p = proxy.transform.position;
                pos = new float3(p.x, 0f, p.z);
                radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(1f);
            }

            pos.y = 0f;
            s_bodyScratch.Add(new VisualBody
            {
                Entity = entity,
                LogicalPos = pos,
                Radius = radius,
            });
        }

        void ResolveChunkAgainstBodies(ref Chunk chunk, Entity ignoreShip)
        {
            bool torus = ToroidalMapEcs.IsValidMapSize(_mapW, _mapH);
            for (int i = 0; i < s_bodyScratch.Count; i++)
            {
                VisualBody body = s_bodyScratch[i];
                if (body.Entity == ignoreShip)
                    continue;

                float3 offset = torus
                    ? ToroidalMapEcs.ShortestOffsetXZ(body.LogicalPos, chunk.LogicalPos, _mapW, _mapH)
                    : new float3(
                        chunk.LogicalPos.x - body.LogicalPos.x,
                        0f,
                        chunk.LogicalPos.z - body.LogicalPos.z);

                ShipDeathDebrisMath.ResolveVisualSphere(
                    ref chunk.LogicalPos,
                    ref chunk.Velocity,
                    ref chunk.SpinDegPerSec,
                    offset,
                    chunk.Radius,
                    body.Radius,
                    _settings.VisualBounce,
                    _settings.VisualHitSpin);
            }
        }

        static int CompareVisualBody(VisualBody a, VisualBody b)
        {
            int c = a.Entity.Index.CompareTo(b.Entity.Index);
            return c != 0 ? c : a.Entity.Version.CompareTo(b.Entity.Version);
        }

        static string ResolveFamilyPrefix(EntityManager em, Entity ship)
        {
            if (!em.HasComponent<ShipState>(ship))
                return "AstroEagle";

            var shipState = em.GetComponentData<ShipState>(ship);
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    ship,
                    shipState.Team,
                    shipState.ShipLevel,
                    shipState.BranchIndex,
                    out string chassisId,
                    allowFallback: true)
                || string.IsNullOrEmpty(chassisId))
                return "AstroEagle";

            int us = chassisId.IndexOf('_');
            return us > 0 ? chassisId.Substring(0, us) : chassisId;
        }

        static void StripCollectedDescendants(Transform clone, Transform original, HashSet<Transform> collected)
        {
            int n = Mathf.Min(clone.childCount, original.childCount);
            for (int i = n - 1; i >= 0; i--)
            {
                Transform oc = original.GetChild(i);
                Transform cc = clone.GetChild(i);
                if (collected.Contains(oc))
                    Destroy(cc.gameObject);
                else
                    StripCollectedDescendants(cc, oc, collected);
            }
        }

        static void StripInteractive(GameObject root)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null)
                    Destroy(cols[i]);
            }

            var bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null)
                    Destroy(bodies[i]);
            }

            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null)
                    Destroy(behaviours[i]);
            }
        }

        static void MuteAudio(GameObject root)
        {
            var sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].enabled = false;
            }
        }
    }
}

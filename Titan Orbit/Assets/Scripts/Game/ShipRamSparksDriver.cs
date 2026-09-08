using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client grind / ram contact VFX: one persistent impact stream per grinding ship
    /// (or per rock neighborhood when the HitRpc has no NetworkId).
    /// <para>
    /// Sequence-0 used to rent the ship's full Sci-Fi bullet impact on every pulse
    /// (1.25–3 s, lights, rings, many systems). Profiler GPU 9→26 ms while grinding.
    /// Generic orange sparks were cheaper but looked nothing like the focused bullet type.
    /// </para>
    /// <para>
    /// This driver rents the <b>current focused bank's impact prefab once</b>, then
    /// rewrites it into a constant low-rate stream: same particle materials / glow / fire
    /// as the bullet hit, without the one-shot boom (rings, shockwaves, lights, audio).
    /// Instances live in a grind-only pool — never returned to
    /// <see cref="BulletOneShotVfxPool"/>, so bullet hits keep their authored bursts.
    /// Kill / plow booms still play on <see cref="BulletImpactAttach"/>.
    /// </para>
    /// Client presentation only — no RPC or ghost fields.
    /// </summary>
    [DefaultExecutionOrder(67009)]
    public class ShipRamSparksDriver : MonoBehaviour
    {
        /// <summary>Per-system cap after we convert the one-shot explosion to a trickle.</summary>
        const int MaxParticlesPerSystem = 18;

        /// <summary>How many child ParticleSystems we keep (Glow / Fire / Dust). Extra rings stay off.</summary>
        const int MaxKeptSystems = 3;

        /// <summary>Steady grind emit rate before intensity scale (split across kept systems).</summary>
        const float BaseEmitRate = 28f;

        /// <summary>First-contact / hard-ram burst (clamped by unused particle slots).</summary>
        const int BaseBurstCount = 8;

        /// <summary>Keep emitting this long after the last pulse or local contact.</summary>
        const float HoldSeconds = 0.35f;

        /// <summary>Park the GO after emission stops and leftover sparks die.</summary>
        const float RecycleSlackSeconds = 0.4f;

        /// <summary>Max concurrent grinders (local + remotes). Extra notifies reuse the oldest.</summary>
        const int MaxEmitters = 8;

        /// <summary>
        /// Grind stream is smaller than a real bullet boom — same prefab, quieter read.
        /// Bank Global Visual Scale still applies via <see cref="BulletVisualFactory.GetImpactScale"/>.
        /// </summary>
        const float GrindScaleMul = 0.7f;

        /// <summary>Key for the predicted local hull — merges HitRpc + <see cref="ShipAsteroidContactState"/>.</summary>
        const int LocalShipKey = int.MinValue;

        /// <summary>Treat a HitRpc as the local ship when the flash is this close (world XZ).</summary>
        const float LocalMergeRadius = 18f;

        /// <summary>
        /// One live grind stream. <see cref="Systems"/> is cached so LateUpdate / Place
        /// never calls GetComponentsInChildren (that allocates).
        /// </summary>
        struct Emitter
        {
            public int Key;
            public GameObject Go;
            public ParticleSystem[] Systems;
            public int PrefabKey;
            public int BankIndex;
            public TeamId Team;
            public float DieAt;
            public bool Active;
            public bool IsFallback;
        }

        /// <summary>
        /// [UNITY] Marks a grind-owned impact instance that already paid
        /// <see cref="SimplifyImpactForConstantGrind"/>. We Instantiates these ourselves
        /// and never hand them to the one-shot bullet pool.
        /// </summary>
        sealed class GrindImpactReady : MonoBehaviour
        {
        }

        static ShipRamSparksDriver s_Instance;
        static Material s_fallbackMat;

        readonly List<Emitter> _live = new List<Emitter>(MaxEmitters);
        readonly Dictionary<int, Stack<GameObject>> _poolByPrefab = new Dictionary<int, Stack<GameObject>>(8);
        readonly Stack<GameObject> _fallbackPool = new Stack<GameObject>(4);

        Transform _poolRoot;
        int _lastTickFrame = -1;
        bool _localWasContacting;

        /// <summary>
        /// Places or refreshes the grind stream at a Sequence-0 ram / grind flash.
        /// <paramref name="isKill"/> stops the stream (the bullet boom plays elsewhere).
        /// </summary>
        /// <param name="displayPos">Observer display-space contact (Y flattened).</param>
        /// <param name="normalXZ">Spray direction, asteroid → ship (unit XZ, Y ignored).</param>
        /// <param name="intensity">From HitRpc <c>ScaleMultiplier</c> / damage (hard ram &gt; scrape).</param>
        /// <param name="isKill">True when the rock died this pulse.</param>
        /// <param name="bankIndex">Focused bullet bank — which impact prefab to copy.</param>
        /// <param name="team">Team tint for that bank's colored impact prefab.</param>
        public static void NotifyRamContact(
            float3 displayPos,
            float3 normalXZ,
            float intensity,
            bool isKill,
            int bankIndex,
            TeamId team)
        {
            if (s_Instance == null)
                return;
            s_Instance.ApplyNotify(displayPos, normalXZ, intensity, isKill, bankIndex, team);
        }

        /// <summary>[UNITY] Attach next to <see cref="BulletVfxDriver"/> so grind VFX exist in Play Mode.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<ShipRamSparksDriver>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<ShipRamSparksDriver>();
                return;
            }

            var go = new GameObject("ShipRamSparksDriver");
            DontDestroyOnLoad(go);
            go.AddComponent<ShipRamSparksDriver>();
        }

        void OnEnable()
        {
            s_Instance = this;
        }

        void OnDisable()
        {
            if (s_Instance == this)
                s_Instance = null;
            RecycleAll();
        }

        /// <summary>
        /// After <see cref="CameraFollowEcs"/>: local contact first so sparks start before
        /// the HitRpc, then park emitters whose hold expired.
        /// </summary>
        void LateUpdate()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
            {
                RecycleAll();
                return;
            }

            if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                TickLocalContact();

            TickEmitters();
        }

        /// <summary>
        /// HitRpc path: Sequence-0 ram / grind pulse from <see cref="BulletVfxDriver"/>.
        /// </summary>
        void ApplyNotify(float3 displayPos, float3 normalXZ, float intensity, bool isKill, int bankIndex, TeamId team)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            displayPos.y = 0f;
            int key = ResolveKey(displayPos);
            if (isKill)
            {
                StopEmitter(key);
                return;
            }

            EnsureEmitter(key, displayPos, normalXZ, intensity, burst: true, bankIndex, team);
        }

        /// <summary>
        /// Predicted local hull: <see cref="ShipAsteroidContactState"/> is not ghosted, so
        /// remotes never take this path. Sprays off the rock along <c>OutwardNormal</c>.
        /// Bank / team come from the local ship's focused fire type (B-key / heal).
        /// </summary>
        void TickLocalContact()
        {
            var world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
            {
                _localWasContacting = false;
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship) ||
                ship == Entity.Null)
            {
                _localWasContacting = false;
                return;
            }

            var em = world.EntityManager;
            if (!em.Exists(ship) || !em.HasComponent<ShipAsteroidContactState>(ship))
            {
                _localWasContacting = false;
                return;
            }

            var contact = em.GetComponentData<ShipAsteroidContactState>(ship);
            if (contact.InContact == 0)
            {
                _localWasContacting = false;
                return;
            }

            float3 shipPos;
            float hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(1f);
            if (ShipDisplayPose.HasLocalPose)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                shipPos = new float3(p.x, 0f, p.z);
            }
            else if (em.HasComponent<LocalTransform>(ship))
            {
                var lt = em.GetComponentData<LocalTransform>(ship);
                shipPos = lt.Position;
                shipPos.y = 0f;
                hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(lt.Scale);
            }
            else
            {
                _localWasContacting = false;
                return;
            }

            float3 n = contact.OutwardNormal;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                n = new float3(0f, 0f, 1f);
            else
                n = math.normalize(n);

            // Contact sits on the rock: from the ship, step back along the outward normal.
            float3 displayPos = shipPos - n * hullRadius;
            displayPos.y = 0f;

            // Focused bullet type — same index the HUD tile / B-key cycle uses.
            int bankIndex = 0;
            TeamId team = TeamId.None;
            if (em.HasComponent<ShipLoadoutState>(ship))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(ship);
                bankIndex = BulletBankFireResolve.ResolveFireBankIndex(in loadout);
            }

            if (em.HasComponent<ShipState>(ship))
                team = em.GetComponentData<ShipState>(ship).Team;

            bool first = !_localWasContacting;
            _localWasContacting = true;
            EnsureEmitter(LocalShipKey, displayPos, n, intensity: 1f, burst: first, bankIndex, team);
        }

        /// <summary>Stops emission when the hold window expires, then parks leftover GOs.</summary>
        void TickEmitters()
        {
            float now = Time.unscaledTime;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var e = _live[i];
                if (e.Go == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (now <= e.DieAt)
                    continue;

                SetEmitRate(e.Systems, 0f);
                e.Active = false;
                _live[i] = e;

                if (now > e.DieAt + RecycleSlackSeconds)
                    RecycleAt(i);
            }
        }

        /// <summary>
        /// Creates or refreshes the stream for this ship / rock key. Swaps the rented
        /// prefab when the player cycles B mid-grind so the particles match the new type.
        /// </summary>
        void EnsureEmitter(
            int key,
            float3 displayPos,
            float3 normalXZ,
            float intensity,
            bool burst,
            int bankIndex,
            TeamId team)
        {
            intensity = math.clamp(intensity, 0.35f, 2.25f);
            bankIndex = math.max(0, bankIndex);
            int index = IndexOfKey(key);
            if (index < 0 && _live.Count >= MaxEmitters)
                RecycleAt(OldestIndex());

            if (index >= 0)
            {
                var existing = _live[index];
                bool bankChanged = existing.BankIndex != bankIndex || existing.Team != team;
                if (bankChanged)
                {
                    RecycleAt(index);
                    index = -1;
                }
            }

            if (index < 0)
            {
                if (!TryRent(bankIndex, team, out GameObject go, out ParticleSystem[] systems, out int prefabKey, out bool fallback))
                    return;

                var created = new Emitter
                {
                    Key = key,
                    Go = go,
                    Systems = systems,
                    PrefabKey = prefabKey,
                    BankIndex = bankIndex,
                    Team = team,
                    DieAt = Time.unscaledTime + HoldSeconds,
                    Active = true,
                    IsFallback = fallback,
                };
                _live.Add(created);
                index = _live.Count - 1;
                Place(ref created, displayPos, normalXZ, intensity, burst: true);
                _live[index] = created;
                return;
            }

            var live = _live[index];
            bool wasCold = !live.Active || Time.unscaledTime > live.DieAt;
            Place(ref live, displayPos, normalXZ, intensity, burst: burst || wasCold);
            live.DieAt = Time.unscaledTime + HoldSeconds;
            live.Active = true;
            _live[index] = live;
        }

        /// <summary>
        /// Moves the stream to the contact, aims it off the rock, and sets the trickle rate.
        /// Burst only on first contact / hard pulse so grind stays a constant stream.
        /// </summary>
        void Place(ref Emitter e, float3 displayPos, float3 normalXZ, float intensity, bool burst)
        {
            if (e.Go == null)
                return;

            e.Go.transform.position = new Vector3(displayPos.x, 0.2f, displayPos.z);

            float3 n = normalXZ;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                n = new float3(0f, 0f, 1f);
            else
                n = math.normalize(n);
            // Tilt up so sparks read in a top-down camera (pure XZ sits on the play plane).
            float3 spray = math.normalize(n + new float3(0f, 0.45f, 0f));
            e.Go.transform.rotation = Quaternion.LookRotation(
                new Vector3(spray.x, spray.y, spray.z), Vector3.up);

            float rate = BaseEmitRate * intensity;
            SetEmitRate(e.Systems, rate);

            if (burst)
            {
                int count = math.clamp((int)(BaseBurstCount * intensity), 4, MaxParticlesPerSystem);
                EmitBurst(e.Systems, count);
            }
        }

        /// <summary>Splits <paramref name="totalRate"/> across kept systems so Glow + Fire both trickle.</summary>
        static void SetEmitRate(ParticleSystem[] systems, float totalRate)
        {
            if (systems == null || systems.Length == 0)
                return;

            float per = totalRate / systems.Length;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null)
                    continue;
                var emission = systems[i].emission;
                emission.rateOverTime = per;
            }
        }

        /// <summary>One-shot extra particles on first contact (does not restart the loop).</summary>
        static void EmitBurst(ParticleSystem[] systems, int totalCount)
        {
            if (systems == null || systems.Length == 0 || totalCount <= 0)
                return;

            int per = math.max(1, totalCount / systems.Length);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                    systems[i].Emit(per);
            }
        }

        /// <summary>Local hull when the flash is next to us; otherwise an 8-unit rock neighborhood.</summary>
        int ResolveKey(float3 displayPos)
        {
            if (IsNearLocalShip(displayPos))
                return LocalShipKey;

            int x = (int)math.floor(displayPos.x / 8f);
            int z = (int)math.floor(displayPos.z / 8f);
            return (x * 73856093) ^ (z * 19349663);
        }

        static bool IsNearLocalShip(float3 displayPos)
        {
            if (!ShipDisplayPose.HasLocalPose)
                return false;
            Vector3 p = ShipDisplayPose.LocalPosition;
            float dx = p.x - displayPos.x;
            float dz = p.z - displayPos.z;
            return dx * dx + dz * dz <= LocalMergeRadius * LocalMergeRadius;
        }

        int IndexOfKey(int key)
        {
            for (int i = 0; i < _live.Count; i++)
            {
                if (_live[i].Key == key)
                    return i;
            }

            return -1;
        }

        int OldestIndex()
        {
            int best = 0;
            float oldest = _live[0].DieAt;
            for (int i = 1; i < _live.Count; i++)
            {
                if (_live[i].DieAt < oldest)
                {
                    oldest = _live[i].DieAt;
                    best = i;
                }
            }

            return best;
        }

        void StopEmitter(int key)
        {
            int index = IndexOfKey(key);
            if (index < 0)
                return;
            RecycleAt(index);
        }

        /// <summary>Stops particles and parks the GO on the grind-only stack for that prefab.</summary>
        void RecycleAt(int index)
        {
            var e = _live[index];
            _live.RemoveAt(index);
            if (e.Go == null)
                return;

            StopSystems(e.Systems);
            e.Go.SetActive(false);
            if (_poolRoot != null)
                e.Go.transform.SetParent(_poolRoot, false);

            if (e.IsFallback)
            {
                _fallbackPool.Push(e.Go);
                return;
            }

            if (!_poolByPrefab.TryGetValue(e.PrefabKey, out var stack))
            {
                stack = new Stack<GameObject>(2);
                _poolByPrefab[e.PrefabKey] = stack;
            }

            stack.Push(e.Go);
        }

        void RecycleAll()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
                RecycleAt(i);
            _localWasContacting = false;
        }

        static void StopSystems(ParticleSystem[] systems)
        {
            if (systems == null)
                return;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                    systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        /// <summary>
        /// Rents a grind-simplified impact for this bank + team, or the generic spark fallback
        /// on mobile / missing prefab.
        /// </summary>
        bool TryRent(
            int bankIndex,
            TeamId team,
            out GameObject go,
            out ParticleSystem[] systems,
            out int prefabKey,
            out bool fallback)
        {
            go = null;
            systems = null;
            prefabKey = 0;
            fallback = false;

            GameObject prefab = ResolveImpactPrefab(bankIndex, team);
            if (prefab != null && !Application.isMobilePlatform)
            {
                prefabKey = prefab.GetInstanceID();
                if (_poolByPrefab.TryGetValue(prefabKey, out var stack) && stack.Count > 0)
                {
                    go = stack.Pop();
                    if (go != null)
                    {
                        go.transform.SetParent(null, false);
                        go.SetActive(true);
                        systems = CollectKeptSystems(go);
                        RestartSystems(systems);
                        return true;
                    }
                }

                go = BuildGrindImpact(prefab, bankIndex);
                if (go != null)
                {
                    systems = CollectKeptSystems(go);
                    return true;
                }
            }

            fallback = true;
            prefabKey = 0;
            return TryRentFallback(out go, out systems);
        }

        /// <summary>Bank impact prefab for the focused bullet type (team-colored).</summary>
        static GameObject ResolveImpactPrefab(int bankIndex, TeamId team)
        {
            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return null;
            return bank.GetImpactPrefab(bankIndex, team);
        }

        /// <summary>
        /// Instantiates a dedicated grind copy. We do not use <see cref="BulletOneShotVfxPool"/>
        /// because we rewrite emission / lifetime — returning that to the hit pool would
        /// make real bullet impacts trickle instead of boom.
        /// </summary>
        GameObject BuildGrindImpact(GameObject prefab, int bankIndex)
        {
            EnsurePoolRoot();
            var go = Instantiate(prefab);
            go.name = prefab.name + "_RamGrind";
            // Prefab Play On Awake would flash the full boom before we rewrite emission.
            go.SetActive(false);
            VfxUrpCompat.PrepareVfxInstance(go, playParticles: false);
            SimplifyImpactForConstantGrind(go);

            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            float worldScale = BulletVisualFactory.GetImpactScale(bank, 1f, bankIndex) * GrindScaleMul;
            VfxUrpCompat.ApplyImpactVisualScale(go, worldScale);
            go.AddComponent<GrindImpactReady>();

            go.SetActive(true);
            ParticleSystem[] systems = CollectKeptSystems(go);
            RestartSystems(systems);
            return go;
        }

        /// <summary>
        /// Turns a one-shot Sci-Fi impact into a cheap constant stream:
        /// mute audio, kill boom extras (rings / shockwaves / lights / trails),
        /// cap particles, replace bursts with a low rate, shorten lifetime.
        /// </summary>
        static void SimplifyImpactForConstantGrind(GameObject root)
        {
            if (root == null)
                return;

            // --- Mute authored impact SFX (Play On Awake would fire every recycle) ---
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] == null)
                    continue;
                sources[i].Stop();
                sources[i].playOnAwake = false;
                sources[i].enabled = false;
            }

            VfxUrpCompat.StripSceneFlashLights(root);

            TrailRenderer[] trails = root.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] != null)
                    trails[i].enabled = false;
            }

            // --- Rank children: keep Glow / Fire / Dust, drop Ring / Shockwave ---
            ParticleSystem[] all = root.GetComponentsInChildren<ParticleSystem>(true);
            int kept = 0;
            for (int i = 0; i < all.Length; i++)
            {
                ParticleSystem ps = all[i];
                if (ps == null)
                    continue;

                if (ps.isPlaying)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                bool heavy = IsHeavyOneShotChild(ps.gameObject.name);
                if (heavy || kept >= MaxKeptSystems)
                {
                    ps.gameObject.SetActive(false);
                    continue;
                }

                ConfigureSystemAsGrindStream(ps);
                kept++;
            }
        }

        /// <summary>
        /// Boom extras that look wrong as a loop and cost GPU. Glow / Fire / Dust stay.
        /// </summary>
        static bool IsHeavyOneShotChild(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return ContainsIgnoreCase(name, "ring")
                   || ContainsIgnoreCase(name, "shock")
                   || ContainsIgnoreCase(name, "wave")
                   || ContainsIgnoreCase(name, "distort");
        }

        static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Authored impacts burst once then die. Grind needs a short-lived trickle
        /// so particles do not pile into a blob.
        /// </summary>
        static void ConfigureSystemAsGrindStream(ParticleSystem ps)
        {
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = math.min(main.maxParticles, MaxParticlesPerSystem);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.2f, 5.5f);
            main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(System.Array.Empty<ParticleSystem.Burst>());

            var collision = ps.collision;
            collision.enabled = false;
            var trails = ps.trails;
            trails.enabled = false;
            var lights = ps.lights;
            lights.enabled = false;
            var sub = ps.subEmitters;
            sub.enabled = false;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
                return;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.maxParticleSize = Mathf.Min(renderer.maxParticleSize, 0.22f);
        }

        /// <summary>Active particle systems after simplify (disabled boom children are skipped).</summary>
        static ParticleSystem[] CollectKeptSystems(GameObject root)
        {
            if (root == null)
                return System.Array.Empty<ParticleSystem>();

            ParticleSystem[] all = root.GetComponentsInChildren<ParticleSystem>(false);
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.activeInHierarchy)
                    count++;
            }

            if (count == 0)
                return System.Array.Empty<ParticleSystem>();

            var kept = new ParticleSystem[count];
            int w = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.activeInHierarchy)
                    kept[w++] = all[i];
            }

            return kept;
        }

        static void RestartSystems(ParticleSystem[] systems)
        {
            if (systems == null)
                return;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null)
                    continue;
                systems[i].Clear(true);
                systems[i].Play(true);
            }
        }

        bool TryRentFallback(out GameObject go, out ParticleSystem[] systems)
        {
            go = null;
            systems = null;

            if (_fallbackPool.Count > 0)
            {
                go = _fallbackPool.Pop();
                if (go != null)
                {
                    go.transform.SetParent(null, false);
                    go.SetActive(true);
                    var ps = go.GetComponent<ParticleSystem>();
                    if (ps != null)
                    {
                        ps.Clear(true);
                        ps.Play(true);
                        systems = new[] { ps };
                        return true;
                    }
                }
            }

            go = BuildFallbackEmitterGo();
            if (go == null)
                return false;
            var built = go.GetComponent<ParticleSystem>();
            if (built == null)
                return false;
            systems = new[] { built };
            return true;
        }

        /// <summary>
        /// Last-resort generic sparks when the bank has no impact prefab (or mobile).
        /// Same cheap billboard stream as the previous grind driver.
        /// </summary>
        GameObject BuildFallbackEmitterGo()
        {
            EnsurePoolRoot();
            var go = new GameObject("RamGrindSparks");
            var ps = go.AddComponent<ParticleSystem>();
            ConfigureFallbackParticleSystem(ps);
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = SharedFallbackMaterial();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
            }

            return go;
        }

        void EnsurePoolRoot()
        {
            if (_poolRoot != null)
                return;
            var root = new GameObject("RamGrindSparksPool");
            root.transform.SetParent(transform, false);
            root.SetActive(false);
            _poolRoot = root.transform;
        }

        static void ConfigureFallbackParticleSystem(ParticleSystem ps)
        {
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.maxParticles = MaxParticlesPerSystem;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 0.28f;
            main.startSpeed = 6.5f;
            main.startSize = 0.14f;
            main.startColor = new Color(1f, 0.72f, 0.28f, 1f);
            main.gravityModifier = 1.35f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 28f;
            shape.radius = 0.12f;

            var color = ps.colorOverLifetime;
            color.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.92f, 0.55f), 0f),
                    new GradientColorKey(new Color(1f, 0.45f, 0.12f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f),
                });
            color.color = grad;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.15f));

            var collision = ps.collision;
            collision.enabled = false;
            var trails = ps.trails;
            trails.enabled = false;
            var lights = ps.lights;
            lights.enabled = false;
        }

        static Material SharedFallbackMaterial()
        {
            if (s_fallbackMat != null)
                return s_fallbackMat;

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                            ?? Shader.Find("Particles/Standard Unlit")
                            ?? Shader.Find("Unlit/Color");
            s_fallbackMat = new Material(shader)
            {
                name = "RamGrindSparks_Unlit",
                color = Color.white,
            };
            if (s_fallbackMat.HasProperty("_BaseColor"))
                s_fallbackMat.SetColor("_BaseColor", Color.white);
            if (s_fallbackMat.HasProperty("_Surface"))
                s_fallbackMat.SetFloat("_Surface", 1f);
            if (s_fallbackMat.HasProperty("_Blend"))
                s_fallbackMat.SetFloat("_Blend", 2f);
            if (s_fallbackMat.HasProperty("_SrcBlend"))
                s_fallbackMat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (s_fallbackMat.HasProperty("_DstBlend"))
                s_fallbackMat.SetFloat("_DstBlend", (float)BlendMode.One);
            if (s_fallbackMat.HasProperty("_ZWrite"))
                s_fallbackMat.SetFloat("_ZWrite", 0f);
            s_fallbackMat.renderQueue = 3000;
            return s_fallbackMat;
        }
    }
}

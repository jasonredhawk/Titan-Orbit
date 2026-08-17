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
    /// Client-only: clones every prefab component on a dying ship and flies them as
    /// non-interactive debris until that ship respawns. Motion is seeded from
    /// <see cref="ShipDeathVfxState.Packed"/> so all clients match.
    /// <para>
    /// Hooked from <see cref="EcsWorldVisualizer"/> (no extra ship entity queries).
    /// </para>
    /// </summary>
    public sealed class ShipDeathDebrisDriver : MonoBehaviour
    {
        const string FireballsV2Category = "FireballsV2";
        const string DebrisRootName = "ShipDeathDebris";

        struct Piece
        {
            public GameObject Go;
            public float3 LogicalPos;
            public quaternion Rotation;
            public float3 Velocity;
            public float3 SpinDegPerSec;
            public GameObject Burn;
            public float BurnDelay;
            public bool WillBurn;
        }

        struct Wreck
        {
            public Entity Ship;
            public uint Packed;
            public float3 CenterLogical;
            public List<Piece> Pieces;
            public float StartTime;
            public byte Team;
        }

        static ShipDeathDebrisDriver s_instance;
        static readonly List<Transform> s_partScratch = new List<Transform>(64);

        readonly Dictionary<Entity, Wreck> _wrecks = new Dictionary<Entity, Wreck>(16);
        readonly List<Entity> _endScratch = new List<Entity>(8);
        ShipDeathDebrisSettings _settings;
        BulletVfxBank _bank;
        int _fireballsIndex = -1;
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
            _bank = BulletVfxBank.LoadDefault();
            if (_bank != null)
                _bank.TryGetCategoryIndexByName(FireballsV2Category, out _fireballsIndex);
        }

        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            EndAll();
        }

        /// <summary>
        /// Snapshot the live proxy (still active) and start debris. No-op when already playing
        /// this Packed value, or when Packed is 0.
        /// </summary>
        public static void TryBegin(Entity ship, GameObject proxy, EntityManager em)
        {
            if (s_instance == null || ship == Entity.Null || proxy == null)
                return;
            if (!em.Exists(ship) || !em.HasComponent<ShipState>(ship))
                return;

            uint packed = 0;
            if (em.HasComponent<ShipDeathVfxState>(ship))
                packed = em.GetComponentData<ShipDeathVfxState>(ship).Packed;
            if (packed == 0)
                return;

            s_instance.Begin(ship, proxy, em, packed);
        }

        /// <summary>Destroys debris for this ship (respawn or proxy teardown).</summary>
        public static void End(Entity ship)
        {
            if (s_instance == null || ship == Entity.Null)
                return;
            s_instance.DestroyWreck(ship);
        }

        void Begin(Entity ship, GameObject proxy, EntityManager em, uint packed)
        {
            if (_wrecks.TryGetValue(ship, out var existing) && existing.Packed == packed)
                return;
            DestroyWreck(ship);

            bool isMega = em.HasComponent<MegaShipState>(ship)
                          && em.GetComponentData<MegaShipState>(ship).IsMega;
            string prefix = ResolveFamilyPrefix(em, ship);

            ShipDeathDebrisParts.Collect(proxy.transform, isMega, prefix, s_partScratch);
            if (s_partScratch.Count == 0)
                return;

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
                Pieces = new List<Piece>(s_partScratch.Count),
                StartTime = Time.time,
                Team = team,
            };

            var collected = new HashSet<Transform>(s_partScratch);
            var sizes = new List<float>(s_partScratch.Count);

            for (int i = 0; i < s_partScratch.Count; i++)
            {
                Transform src = s_partScratch[i];
                if (src == null)
                    continue;

                GameObject clone = Instantiate(src.gameObject);
                clone.name = src.name + "_Debris";
                StripCollectedDescendants(clone.transform, src, collected);
                StripInteractive(clone);

                float3 logical = src.position;
                clone.transform.SetPositionAndRotation(src.position, src.rotation);
                clone.transform.localScale = src.lossyScale;
                clone.transform.SetParent(transform, true);

                float3 offset = logical - center;
                offset.y = 0f;
                ShipDeathDebrisMath.ComputeLaunch(
                    seed,
                    i,
                    offset,
                    impulseDir,
                    power01,
                    hullRadius,
                    shipVel,
                    _settings,
                    out float3 velocity,
                    out float3 spin);

                wreck.Pieces.Add(new Piece
                {
                    Go = clone,
                    LogicalPos = logical,
                    Rotation = src.rotation,
                    Velocity = velocity,
                    SpinDegPerSec = spin,
                    Burn = null,
                    BurnDelay = 0f,
                    WillBurn = false,
                });
                sizes.Add(EstimateSize(clone));
            }

            QueueStaggeredBurns(wreck, sizes, seed);
            _wrecks[ship] = wreck;

            PlayBurst(center, hullRadius, power01, (TeamId)team);
            var audio = AudioManager.GetOrFind();
            if (audio != null)
                audio.PlayShipDeathSound();
        }

        void LateUpdate()
        {
            // [TITAN-ORBIT] Map size from ToroidalMapEcs (MapStateSingleton / session meta).
            RefreshMapSize();
            if (_wrecks.Count == 0)
                return;

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

            _endScratch.Clear();
            foreach (var kv in _wrecks)
            {
                var wreck = kv.Value;
                if (wreck.Pieces == null)
                {
                    _endScratch.Add(kv.Key);
                    continue;
                }

                for (int i = 0; i < wreck.Pieces.Count; i++)
                {
                    Piece piece = wreck.Pieces[i];
                    if (piece.Go == null)
                        continue;

                    ShipDeathDebrisMath.IntegrateDrag(
                        ref piece.Velocity,
                        ref piece.SpinDegPerSec,
                        dt,
                        _settings.LinearDrag,
                        _settings.AngularDrag);

                    piece.LogicalPos += piece.Velocity * dt;
                    piece.LogicalPos.y = 0f;
                    piece.Rotation = math.mul(
                        piece.Rotation,
                        quaternion.Euler(math.radians(piece.SpinDegPerSec * dt)));

                    float3 display = piece.LogicalPos;
                    if (hasRef && ToroidalMapEcs.IsValidMapSize(_mapW, _mapH))
                        display = ToroidalMapEcs.GetDisplayPosition(piece.LogicalPos, reference, _mapW, _mapH);

                    piece.Go.transform.SetPositionAndRotation(
                        new Vector3(display.x, display.y, display.z),
                        piece.Rotation);

                    float age = Time.time - wreck.StartTime;
                    if (piece.WillBurn && piece.Burn == null && age >= piece.BurnDelay)
                        TryStartBurn(ref piece, wreck.Team);
                    else if (piece.Burn != null)
                        KeepBurnPlaying(piece.Burn);

                    wreck.Pieces[i] = piece;
                }
            }

            for (int i = 0; i < _endScratch.Count; i++)
                DestroyWreck(_endScratch[i]);
        }

        void QueueStaggeredBurns(Wreck wreck, List<float> sizes, uint seed)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;
            if (wreck.Pieces == null || sizes.Count == 0)
                return;

            int max = Mathf.Min(_settings.MaxBurnAttachments, wreck.Pieces.Count);
            if (max <= 0)
                return;

            var order = new List<int>(sizes.Count);
            for (int i = 0; i < sizes.Count; i++)
                order.Add(i);
            order.Sort((a, b) => sizes[b].CompareTo(sizes[a]));

            for (int n = 0; n < max; n++)
            {
                int i = order[n];
                Piece piece = wreck.Pieces[i];
                if (piece.Go == null)
                    continue;
                piece.WillBurn = true;
                piece.BurnDelay = ShipDeathDebrisMath.ComputeBurnDelay(
                    seed, i, _settings.BurnStartDelayMin, _settings.BurnStartDelayMax);
                wreck.Pieces[i] = piece;
            }
        }

        void TryStartBurn(ref Piece piece, byte team)
        {
            if (_bank == null || piece.Go == null)
                return;

            int bankIndex = _fireballsIndex >= 0 ? _fireballsIndex : 0;
            GameObject prefab = _bank.GetImpactPrefab(bankIndex, (TeamId)team);
            if (prefab == null)
                return;
            if (!BulletOneShotVfxPool.TryRent(prefab, out GameObject burn) || burn == null)
                return;

            burn.name = prefab.name + "_DeathBurn";
            burn.transform.SetParent(piece.Go.transform, false);
            burn.transform.localPosition = Vector3.zero;
            burn.transform.localRotation = Quaternion.identity;
            VfxUrpCompat.ApplyImpactVisualScale(burn, _settings.BurnScale);
            MuteAudio(burn);
            VfxUrpCompat.SetParticleSystemsLooping(burn, true);
            piece.Burn = burn;
        }

        static void KeepBurnPlaying(GameObject burn)
        {
            if (burn == null)
                return;

            var systems = burn.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                    continue;

                var main = ps.main;
                if (!main.loop)
                {
                    main.loop = true;
                    main.playOnAwake = false;
                }

                if (!ps.isPlaying)
                    ps.Play(true);
            }
        }

        void PlayBurst(float3 logical, float hullRadius, float power01, TeamId team)
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
                return;
            if (_bank == null)
                return;

            int bankIndex = _fireballsIndex >= 0 ? _fireballsIndex : 0;
            GameObject prefab = _bank.GetImpactPrefab(bankIndex, team);
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

        void DestroyWreck(Entity ship)
        {
            if (!_wrecks.TryGetValue(ship, out var wreck))
                return;
            _wrecks.Remove(ship);
            if (wreck.Pieces == null)
                return;

            for (int i = 0; i < wreck.Pieces.Count; i++)
            {
                Piece piece = wreck.Pieces[i];
                if (piece.Burn != null)
                {
                    VfxUrpCompat.SetParticleSystemsLooping(piece.Burn, false);
                    RestoreAudio(piece.Burn);
                    BulletOneShotVfxPool.ReturnNow(piece.Burn);
                }

                if (piece.Go != null)
                    Destroy(piece.Go);
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

        static float EstimateSize(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            float best = 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || renderers[i] is ParticleSystemRenderer)
                    continue;
                Vector3 e = renderers[i].bounds.size;
                best = Mathf.Max(best, e.x * e.y * e.z);
            }

            return best;
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

        static void RestoreAudio(GameObject root)
        {
            var sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].enabled = true;
            }
        }
    }
}

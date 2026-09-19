using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only: plays a Fire/V1 death fireball when
    /// <see cref="AsteroidDeathVfxBridge"/> dequeues a destroy RPC.
    /// Unclaimed rocks use ModularFireImpact; territory-tinted rocks use that team's
    /// FireImpact (same ownership lookup as the mesh tint). Size follows the rock's
    /// uniform scale; blast pitch drops as the rock gets larger.
    /// Presentation only — rents <see cref="BulletOneShotVfxPool"/> (no per-kill Instantiates).
    /// Pose is the wrapped sim position (one entity, no display tiles).
    /// </summary>
    public sealed class AsteroidDeathVfxDriver : MonoBehaviour
    {
        const string V1Folder =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Explosions/Fire/V1/";

        /// <summary>Idle shells per prefab so the first belt wipe does not Instantiates on the kill frame.</summary>
        const int PrewarmCount = 2;

        static AsteroidDeathVfxDriver s_instance;

        /// <summary>Index 0 = unclaimed ModularFireImpact; 1–5 = TeamA–TeamE FireImpacts.</summary>
        readonly GameObject[] _prefabsByTeam = new GameObject[6];

        /// <summary>
        /// [UNITY] After scene load — spawn a DontDestroyOnLoad driver so deaths work without scene wiring.
        /// Dedicated server has no presentation.
        /// </summary>
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

            var go = new GameObject(nameof(AsteroidDeathVfxDriver));
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<AsteroidDeathVfxDriver>();
#endif
        }

        /// <summary>Resolves Fire/V1 prefabs once and queues a small pool prewarm.</summary>
        void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            ResolvePrefabs();
            EnqueuePrewarm();
        }

        /// <summary>Clears the singleton when the driver is destroyed.</summary>
        void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        /// <summary>Drains queued destroy bursts after sim has applied the RPC this frame.</summary>
        void LateUpdate()
        {
            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
            {
                while (AsteroidDeathVfxBridge.TryDequeue(out _))
                {
                }

                return;
            }

            DrainExplosions();
        }

        /// <summary>
        /// Prefers <see cref="AsteroidSettings"/> Fire/V1 slots, then Editor paths so Play Mode
        /// works before the asset is wired. Does not call
        /// <see cref="AsteroidSettingsCache.ResolveOrDefault"/> — that would cache an empty
        /// fallback if the scene loader has not published yet.
        /// </summary>
        void ResolvePrefabs()
        {
            var settings = TryResolveSettings();
            BindSlot(0, settings != null ? settings.deathExplosionVfxNeutral : null, "ModularFireImpact.prefab");
            BindSlot(1, settings != null ? settings.deathExplosionVfxRed : null, "RedFireImpact.prefab");
            BindSlot(2, settings != null ? settings.deathExplosionVfxBlue : null, "BlueFireImpact.prefab");
            BindSlot(3, settings != null ? settings.deathExplosionVfxGreen : null, "GreenFireImpact.prefab");
            BindSlot(4, settings != null ? settings.deathExplosionVfxYellow : null, "YellowFireImpact.prefab");
            BindSlot(5, settings != null ? settings.deathExplosionVfxPurple : null, "PurpleFireImpact.prefab");

            if (_prefabsByTeam[0] == null)
            {
                for (int i = 1; i < _prefabsByTeam.Length; i++)
                {
                    if (_prefabsByTeam[i] == null)
                        continue;
                    _prefabsByTeam[0] = _prefabsByTeam[i];
                    break;
                }
            }
        }

        /// <summary>Assigns a live prefab into <see cref="_prefabsByTeam"/>, with an Editor path fallback.</summary>
        void BindSlot(int index, GameObject authored, string editorFileName)
        {
            GameObject live = TryLivePrefab(authored);
#if UNITY_EDITOR
            if (live == null)
            {
                live = TryLivePrefab(
                    UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(V1Folder + editorFileName));
            }
#endif
            _prefabsByTeam[index] = live;
        }

        /// <summary>Budgeted Instantiates during join warmup — one job per distinct Fire/V1 prefab.</summary>
        void EnqueuePrewarm()
        {
            for (int i = 0; i < _prefabsByTeam.Length; i++)
            {
                if (_prefabsByTeam[i] != null)
                    BulletOneShotVfxPool.EnqueuePrewarm(_prefabsByTeam[i], PrewarmCount);
            }
        }

        /// <summary>Plays each queued burst at the wrapped destroy pose.</summary>
        void DrainExplosions()
        {
            if (_prefabsByTeam[0] == null)
                ResolvePrefabs();
            if (_prefabsByTeam[0] == null)
            {
                while (AsteroidDeathVfxBridge.TryDequeue(out _))
                {
                }

                return;
            }

            var settings = TryResolveSettings();
            while (AsteroidDeathVfxBridge.TryDequeue(out var req))
                PlayExplosion(in req, settings);
        }

        /// <summary>
        /// Rents the tint-matching Fire/V1 impact, sizes it to the rock, and retunes the blast pitch.
        /// Does not call <c>SpawnBulletImpactVfx</c> — that path multiplies the bank 0.25 global.
        /// </summary>
        void PlayExplosion(in AsteroidDeathVfxBridge.Request req, AsteroidSettings settings)
        {
            float visualScale = req.Scale > 0.01f ? req.Scale : 1f;
            float scale = settings != null
                ? settings.ComputeDeathExplosionScale(visualScale)
                : math.max(0.05f, visualScale * 1.15f);
            float pitch = settings != null
                ? settings.ComputeDeathExplosionPitch(visualScale)
                : math.lerp(1.55f, 0.5f, math.saturate((visualScale - 0.35f) / 3.15f));

            float3 logical = req.Position;
            logical.y = 0f;
            TeamId tint = ResolveExplosionTeam(logical);
            GameObject prefab = PickPrefab(tint, settings);
            if (prefab == null)
                return;

            Vector3 pos = new Vector3(logical.x, 0f, logical.z);
            BulletVisualFactory.SpawnImpactAt(
                pos,
                prefab,
                pitch,
                scale,
                BulletVisualFactory.DefaultImpactDuration,
                replayAudio: true);
        }

        /// <summary>
        /// Same ownership colour as the rock mesh: presentation triangles + local viewer team.
        /// Unclaimed positions stay <see cref="TeamId.None"/> (ModularFireImpact).
        /// </summary>
        static TeamId ResolveExplosionTeam(float3 logical)
        {
            float3 canonical = ToroidalMapEcs.Wrap(new float3(logical.x, 0f, logical.z));
            PlanetConnectionPresentationTriangles.GetOwnershipAtPosition(
                canonical, out byte mask, out TeamId primary);

            TeamId viewer = TeamId.None;
            if (EcsGameBridge.TryGetLocalShipState(out var ship))
                viewer = ship.Team;

            return PlanetConnectionGraphLogic.ResolveAsteroidTintTeam(mask, primary, viewer);
        }

        /// <summary>Team FireImpact, or ModularFireImpact when the slot is empty.</summary>
        GameObject PickPrefab(TeamId team, AsteroidSettings settings)
        {
            int index = (int)team;
            if (index >= 0 && index < _prefabsByTeam.Length && _prefabsByTeam[index] != null)
                return _prefabsByTeam[index];

            if (settings != null)
            {
                GameObject fromSettings = TryLivePrefab(settings.GetDeathExplosionVfx(team));
                if (fromSettings != null)
                    return fromSettings;
            }

            return _prefabsByTeam[0];
        }

        /// <summary>
        /// Published cache, or a one-shot Resources load. Never writes a fallback into the cache.
        /// </summary>
        static AsteroidSettings TryResolveSettings()
        {
            if (AsteroidSettingsCache.Settings != null)
                return AsteroidSettingsCache.Settings;
            return Resources.Load<AsteroidSettings>("AsteroidSettings");
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
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Immediate client-side gem explosion visuals when an asteroid is destroyed.
    /// GhostSpawn Instantiates is 1/frame — waiting for networked gem ghosts made bursts feel
    /// seconds late. This presenter queues local Gem prefab shells the frame the client sees
    /// <c>AsteroidState.IsDestroyed</c>, then Instantiates a few per frame so one destroy cannot
    /// hitch the whole scene (looks like a blink). When real gem ghosts Instantiates later,
    /// nearby local shells are cleared to avoid doubles. Cosmetic only — pickup authority stays
    /// on server gem ghosts.
    /// </summary>
    public sealed class ClientGemBurstPresenter : MonoBehaviour
    {
        /// <summary>
        /// Max local gem Instantiates per frame. Destroy used to Instantiates up to
        /// <see cref="GemExplosionSettings.MaxGemCount"/> (10) in one shot — that GC + mesh
        /// Instantiates spike is the hitch that looked like the scene blinked away.
        /// </summary>
        const int MaxSpawnsPerFrame = 1;

        static ClientGemBurstPresenter _instance;

        [SerializeField] GameObject gemVisualPrefab;
        [SerializeField] float localLifetimeSeconds = 2.25f;
        [SerializeField] float claimRadius = 2.5f;

        readonly List<LocalBurstGem> _live = new List<LocalBurstGem>(32);
        readonly List<PendingBurstGem> _pending = new List<PendingBurstGem>(16);

        /// <summary>One already-spawned local burst gem (motion integrated in Update).</summary>
        struct LocalBurstGem
        {
            public GameObject Go;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float LinearDamping;
            public float AngularDamping;
            public float StopSpeed;
            public float DieAt;
        }

        /// <summary>Queued spawn — Instantiates later so the destroy frame stays light.</summary>
        struct PendingBurstGem
        {
            public Vector3 Position;
            public float Value;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float LinearDamping;
            public float AngularDamping;
            public float StopSpeed;
            public float DieAt;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstance()
        {
            if (_instance != null)
                return;
            _instance = FindAnyObjectByType<ClientGemBurstPresenter>();
            if (_instance != null)
                return;

            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");
            _instance = go.AddComponent<ClientGemBurstPresenter>();
        }

        void Awake()
        {
            _instance = this;
            if (gemVisualPrefab == null)
                gemVisualPrefab = GemVisualApplier.LoadDefaultGemPrefab();
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            ClearAll();
        }

        /// <summary>
        /// Queues local exploding gems at the asteroid pose. Call when the client first sees
        /// IsDestroyed (or the asteroid proxy is about to vanish). Instantiates are spread
        /// across the next few frames — see <see cref="MaxSpawnsPerFrame"/>.
        /// </summary>
        public static void PlayBurst(float3 worldPosition, float remainingValue, uint seed)
        {
            EnsureInstance();
            if (_instance == null)
                return;
            _instance.PlayBurstInternal(worldPosition, remainingValue, seed);
        }

        /// <summary>
        /// Removes local burst shells near a networked gem proxy and returns their motion
        /// so the hybrid proxy can continue the explosion instead of freezing.
        /// </summary>
        public static bool TryClaimNear(Vector3 worldPosition, out Vector3 velocity, out Vector3 angularVelocity)
        {
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            if (_instance == null)
                return false;
            return _instance.ClaimNearInternal(worldPosition, out velocity, out angularVelocity);
        }

        /// <summary>Legacy wrapper — claim without reading motion.</summary>
        public static void ClaimNear(Vector3 worldPosition) =>
            TryClaimNear(worldPosition, out _, out _);

        /// <summary>
        /// Builds the burst plan (count, velocities) and enqueues Instantiates — does not
        /// Instantiates every gem on this call.
        /// </summary>
        void PlayBurstInternal(float3 worldPosition, float remainingValue, uint seed)
        {
            if (remainingValue < 0.25f)
                return;

            var sw = TitanOrbitDebugFlags.LogAsteroidDestroyPerf ? Stopwatch.StartNew() : null;

            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();

            var rng = Random.CreateFromIndex(seed);
            int count = GemExplosionMath.ResolveGemCount(
                remainingValue, settings.MinGemCount, settings.MaxGemCount, ref rng);
            if (count <= 0)
                return;

            if (gemVisualPrefab == null)
                gemVisualPrefab = GemVisualApplier.LoadDefaultGemPrefab();

            // Place in toroidal *display* space so ClaimNear matches networked gem proxies.
            Vector3 center = new Vector3(worldPosition.x, worldPosition.y, worldPosition.z);
            if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                center = ToroidalDisplay.ToDisplayPosition(worldPosition, reference);

            float dieAt = Time.time + localLifetimeSeconds;
            int queued = 0;

            for (int i = 0; i < count; i++)
            {
                float value = GemExplosionMath.ValuePerGem(remainingValue, count, i);
                float3 dir = GemExplosionMath.RandomUnitXZ(ref rng);
                float radius = settings.AsteroidExplosionRadius * rng.NextFloat(0.3f, 1f);
                float3 offset = dir * radius;
                float3 vel = GemExplosionMath.BurstVelocity(
                    dir,
                    settings.AsteroidExplosionSpeed,
                    settings.SpeedRandomMin,
                    settings.SpeedRandomMax,
                    ref rng);
                float3 ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax, ref rng);

                _pending.Add(new PendingBurstGem
                {
                    Position = center + new Vector3(offset.x, 0f, offset.z),
                    Value = value,
                    Velocity = new Vector3(vel.x, 0f, vel.z),
                    AngularVelocity = new Vector3(ang.x, ang.y, ang.z),
                    LinearDamping = settings.LinearDamping,
                    AngularDamping = settings.AngularDamping,
                    StopSpeed = settings.StopSpeedThreshold,
                    DieAt = dieAt,
                });
                queued++;
            }

            // Spawn the first budget immediately so the burst still "pops" on the destroy frame.
            int spawnedNow = DrainPendingSpawns(MaxSpawnsPerFrame);

            AsteroidDestroyBlinkProbe.NotifyGemWork(
                $"localBurst plan={count} spawnedNow={spawnedNow} pending={_pending.Count}");

            if (sw != null)
            {
                sw.Stop();
                Debug.Log(
                    $"[AsteroidDestroy] Local burst plan gems={count} spawnedThisFrame={spawnedNow} " +
                    $"pendingLeft={_pending.Count} enqueue+firstSpawnMs={sw.Elapsed.TotalMilliseconds:F2} " +
                    $"frameDtMs={Time.deltaTime * 1000f:F1}");
            }
        }

        /// <summary>
        /// Instantiates up to <paramref name="budget"/> pending local gems. Returns how many were created.
        /// </summary>
        int DrainPendingSpawns(int budget)
        {
            if (budget <= 0 || _pending.Count == 0)
                return 0;

            if (gemVisualPrefab == null)
                gemVisualPrefab = GemVisualApplier.LoadDefaultGemPrefab();

            int spawned = 0;
            while (spawned < budget && _pending.Count > 0)
            {
                PendingBurstGem p = _pending[0];
                _pending.RemoveAt(0);

                GameObject go;
                if (!GemVisualApplier.TryCreateGemVisual(gemVisualPrefab, p.Value, out go) || go == null)
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GemBurstLocal";
                    var col = go.GetComponent<Collider>();
                    if (col != null)
                        Destroy(col);
                }
                else
                {
                    go.name = "GemBurstLocal";
                }

                go.transform.position = p.Position;
                go.transform.rotation = Quaternion.identity;

                _live.Add(new LocalBurstGem
                {
                    Go = go,
                    Velocity = p.Velocity,
                    AngularVelocity = p.AngularVelocity,
                    LinearDamping = p.LinearDamping,
                    AngularDamping = p.AngularDamping,
                    StopSpeed = p.StopSpeed,
                    DieAt = p.DieAt,
                });
                spawned++;
            }

            return spawned;
        }

        bool ClaimNearInternal(Vector3 worldPosition, out Vector3 velocity, out Vector3 angularVelocity)
        {
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            float r2 = claimRadius * claimRadius;
            bool claimed = false;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var g = _live[i];
                if (g.Go == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if ((g.Go.transform.position - worldPosition).sqrMagnitude > r2)
                    continue;

                // Hand off the fastest nearby local gem's motion to the networked proxy.
                if (!claimed || g.Velocity.sqrMagnitude > velocity.sqrMagnitude)
                {
                    velocity = g.Velocity;
                    angularVelocity = g.AngularVelocity;
                    claimed = true;
                }

                Destroy(g.Go);
                _live.RemoveAt(i);
            }

            return claimed;
        }

        void Update()
        {
            // --- Spread remaining Instantiates across frames (hitch fix) ---
            if (_pending.Count > 0)
            {
                var sw = TitanOrbitDebugFlags.LogAsteroidDestroyPerf ? Stopwatch.StartNew() : null;
                int spawned = DrainPendingSpawns(MaxSpawnsPerFrame);
                if (sw != null && spawned > 0)
                {
                    sw.Stop();
                    Debug.Log(
                        $"[AsteroidDestroy] Local burst drain spawned={spawned} pendingLeft={_pending.Count} " +
                        $"ms={sw.Elapsed.TotalMilliseconds:F2} frameDtMs={Time.deltaTime * 1000f:F1}");
                }
            }

            float dt = Time.deltaTime;
            float now = Time.time;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var g = _live[i];
                if (g.Go == null || now >= g.DieAt)
                {
                    if (g.Go != null)
                        Destroy(g.Go);
                    _live.RemoveAt(i);
                    continue;
                }

                // --- Same damping model as server GemMotionSystem / original Rigidbody ---
                Vector3 vel = g.Velocity;
                vel *= 1f / (1f + g.LinearDamping * dt);
                if (vel.sqrMagnitude < g.StopSpeed * g.StopSpeed)
                    vel = Vector3.zero;

                Vector3 ang = g.AngularVelocity;
                ang *= 1f / (1f + g.AngularDamping * dt);

                g.Go.transform.position += vel * dt;
                if (ang.sqrMagnitude > 0.0001f)
                    g.Go.transform.Rotate(ang * Mathf.Rad2Deg * dt, Space.World);

                g.Velocity = vel;
                g.AngularVelocity = ang;
                _live[i] = g;
            }
        }

        void ClearAll()
        {
            _pending.Clear();
            for (int i = 0; i < _live.Count; i++)
            {
                if (_live[i].Go != null)
                    Destroy(_live[i].Go);
            }
            _live.Clear();
        }
    }
}

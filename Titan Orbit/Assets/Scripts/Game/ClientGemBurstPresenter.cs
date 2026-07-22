using System.Collections.Generic;
using System.Diagnostics;
using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.ECS;
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
    /// seconds late. This presenter queues local Gem shells the frame the client sees
    /// <c>AsteroidState.IsDestroyed</c>, then Rents from <see cref="GemVisualPool"/>.
    /// When real gem ghosts arrive, <see cref="TryTakeNear"/> hands one GO per ghost
    /// (no Destroy/Instantiates). Cosmetic only — pickup authority stays on server gem ghosts.
    /// <para>
    /// Session 74383c: gem count “pop” (1→3 or 3→1 big) was local burst vs network handoff —
    /// not tractor beams. Causes: (1) <c>MaxSpawnsPerFrame=1</c> dribbled pending gems across
    /// frames; (2) first <see cref="TryTakeNear"/> Returned every other local in radius so the
    /// burst collapsed, then later network gems Instantiates with <c>handoff:false</c>.
    /// </para>
    /// <para>
    /// Burst direction contract: each gem’s spawn offset and launch velocity share one random
    /// XZ unit dir so motion is always away from the asteroid center. Handoff matches by
    /// nearest display pose (not fastest speed) so a right-side ghost does not claim a left-flying GO.
    /// </para>
    /// </summary>
    public sealed class ClientGemBurstPresenter : MonoBehaviour
    {
        /// <summary>
        /// Max local gem Rents per frame. Pool Rent is ~0.03 ms when warm (destroy-probe logs),
        /// so we can drain a whole small burst in one frame. Kept as a soft cap in case a future
        /// settings bump raises MaxGemCount a lot.
        /// </summary>
        const int MaxSpawnsPerFrame = 8;

        static ClientGemBurstPresenter _instance;

        [SerializeField] GameObject gemVisualPrefab;
        [SerializeField] float localLifetimeSeconds = 2.25f;
        /// <summary>
        /// How close a networked gem display pose must be to claim a local burst GO.
        /// Wider than explosion radius so Instantiates lag + flight still match (gem-count-fix:
        /// <c>handoff:false</c> while locals were still live → visible double-count).
        /// </summary>
        [SerializeField] float claimRadius = 6f;

        readonly List<LocalBurstGem> _live = new List<LocalBurstGem>(32);
        readonly List<PendingBurstGem> _pending = new List<PendingBurstGem>(16);

        /// <summary>One already-spawned local burst gem (motion integrated in Update).</summary>
        struct LocalBurstGem
        {
            public GameObject Go;
            /// <summary>Asteroid display-space center for this burst (outward radial reference).</summary>
            public Vector3 BurstCenter;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
            public float LinearDamping;
            public float AngularDamping;
            public float StopSpeed;
            public float DieAt;
        }

        /// <summary>Queued spawn — Rent later so the destroy frame stays light.</summary>
        struct PendingBurstGem
        {
            public Vector3 Position;
            /// <summary>Asteroid display-space center copied onto the live gem at Rent.</summary>
            public Vector3 BurstCenter;
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

            // --- Tell the pool which prefab to grow with, and top up idle stock ---
            GemVisualPool.EnsurePrefab(gemVisualPrefab);
            GemVisualPool.Prewarm(GemVisualPool.DefaultPrewarmCount);
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            ClearAll();
        }

        /// <summary>
        /// Queues local exploding gems at the asteroid pose. Call when the client first sees
        /// IsDestroyed (or the asteroid proxy is about to vanish). Rents are spread
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
        /// Takes one local burst GameObject near <paramref name="worldPosition"/> for reuse as a
        /// networked gem proxy (ownership transfer — caller must not Return it until the ghost dies).
        /// Sibling locals stay alive so the next ghost can claim them; nearby <b>pending</b>
        /// spawns are cancelled so late Rents do not appear after the network burst arrives.
        /// </summary>
        /// <param name="worldPosition">Networked gem display pose used for nearest-match claim.</param>
        /// <param name="go">Transferred local GameObject, or null.</param>
        /// <param name="velocity">Launch velocity at handoff (XZ).</param>
        /// <param name="angularVelocity">Tumble at handoff.</param>
        /// <param name="burstCenter">Asteroid display center for this burst (outward clamp).</param>
        /// <returns>True when a GO was handed off with its launch velocity.</returns>
        public static bool TryTakeNear(
            Vector3 worldPosition,
            out GameObject go,
            out Vector3 velocity,
            out Vector3 angularVelocity,
            out Vector3 burstCenter)
        {
            go = null;
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            burstCenter = Vector3.zero;
            if (_instance == null)
                return false;
            return _instance.TakeNearInternal(
                worldPosition, out go, out velocity, out angularVelocity, out burstCenter);
        }

        /// <summary>
        /// Removes local burst shells near a networked gem proxy and returns their motion
        /// without transferring a GO (legacy). Prefer <see cref="TryTakeNear"/> for zero-flash handoff.
        /// </summary>
        public static bool TryClaimNear(Vector3 worldPosition, out Vector3 velocity, out Vector3 angularVelocity)
        {
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            if (_instance == null)
                return false;

            // Take then immediately park — velocity preserved, GO recycled for the next Rent.
            if (!_instance.TakeNearInternal(
                    worldPosition, out GameObject go, out velocity, out angularVelocity, out _))
                return false;

            if (go != null && !GemVisualPool.TryReturn(go))
                Object.Destroy(go);
            return true;
        }

        /// <summary>Legacy wrapper — claim without reading motion.</summary>
        public static void ClaimNear(Vector3 worldPosition) =>
            TryClaimNear(worldPosition, out _, out _);

        /// <summary>
        /// Builds the burst plan (count, velocities) matching server
        /// <c>SpawnAsteroidDestructionGems</c> / <c>GemSpawning.Spawn</c> seeds, then Rents.
        /// </summary>
        void PlayBurstInternal(float3 worldPosition, float remainingValue, uint seed)
        {
            if (remainingValue < GemEconomyConstants.MinGemSpawnValue)
                return;

            var sw = Stopwatch.StartNew();

            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();

            // --- Same count RNG as server (seed only consumed by ResolveGemCount) ---
            var countRng = Random.CreateFromIndex(seed);
            int count = GemExplosionMath.ResolveGemCount(
                remainingValue, settings.MinGemCount, settings.MaxGemCount, ref countRng);
            if (count <= 0)
                return;

            if (gemVisualPrefab == null)
                gemVisualPrefab = GemVisualApplier.LoadDefaultGemPrefab();
            GemVisualPool.EnsurePrefab(gemVisualPrefab);

            // Place in toroidal *display* space so TryTakeNear matches networked gem proxies.
            Vector3 center = new Vector3(worldPosition.x, worldPosition.y, worldPosition.z);
            if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                center = ToroidalDisplay.ToDisplayPosition(worldPosition, reference);

            // Drop leftovers from a previous nearby burst so liveLeft cannot inflate (logs: liveLeft 4 after count 3).
            ReturnAllNear(center, claimRadius);

            float dieAt = Time.time + localLifetimeSeconds;
            int planned = 0;

            for (int i = 0; i < count; i++)
            {
                float value = GemExplosionMath.ValuePerGem(remainingValue, count, i);
                // [TITAN-ORBIT] Mirror server skip — otherwise client shows extra crumbs.
                if (value < GemEconomyConstants.MinGemSpawnValue)
                    continue;

                // --- Per-gem RNG must match GemSpawning.Spawn(salt) ---
                // Server: salt = seed + (i+1)*97; rng = hash(asteroidPos) + salt + 17.
                // Same unit dir drives spawn offset AND launch velocity → always away from center.
                uint salt = seed + (uint)(i + 1) * 97u;
                var gemRng = Random.CreateFromIndex(math.hash(worldPosition) + salt + 17u);
                float3 dir = GemExplosionMath.RandomUnitXZ(ref gemRng);
                float radius = settings.AsteroidExplosionRadius * gemRng.NextFloat(0.3f, 1f);
                float3 offset = dir * radius;
                float3 vel = GemExplosionMath.BurstVelocity(
                    dir,
                    settings.AsteroidExplosionSpeed,
                    settings.SpeedRandomMin,
                    settings.SpeedRandomMax,
                    ref gemRng);
                float3 ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax, ref gemRng);

                _pending.Add(new PendingBurstGem
                {
                    Position = center + new Vector3(offset.x, 0f, offset.z),
                    BurstCenter = center,
                    Value = value,
                    Velocity = new Vector3(vel.x, 0f, vel.z),
                    AngularVelocity = new Vector3(ang.x, ang.y, ang.z),
                    LinearDamping = settings.LinearDamping,
                    AngularDamping = settings.AngularDamping,
                    StopSpeed = settings.StopSpeedThreshold,
                    DieAt = dieAt,
                });
                planned++;
            }

            // Spawn at most one gem on the kill frame; Update drains the rest.
            // [TITAN-ORBIT] kill-impact-fix: draining the whole burst here cost ~1.7 ms.
            // One Rent keeps handoff ready for the first ghost without the full hitch.
            int spawnedNow = DrainPendingSpawns(1);
            sw.Stop();

            if (TitanOrbitDebugFlags.LogAsteroidDestroyPerf)
            {
                Debug.Log(
                    $"[AsteroidDestroy] Local burst plan gems={planned} spawnedThisFrame={spawnedNow} " +
                    $"pendingLeft={_pending.Count} enqueue+firstSpawnMs={sw.Elapsed.TotalMilliseconds:F2} " +
                    $"frameDtMs={Time.deltaTime * 1000f:F1}");
            }
        }

        /// <summary>
        /// Returns every live local burst GO near <paramref name="worldPosition"/> to the pool.
        /// Used when a networked gem Instantiates without handoff (cull the cosmetic twin) and
        /// when a new burst starts (clear orphans).
        /// </summary>
        public static int ReturnAllNear(Vector3 worldPosition, float radius)
        {
            if (_instance == null)
                return 0;
            return _instance.ReturnAllNearInternal(worldPosition, radius);
        }

        int ReturnAllNearInternal(Vector3 worldPosition, float radius)
        {
            CancelPendingNear(worldPosition, radius);
            if (_live.Count == 0 || radius <= 0f)
                return 0;

            float r2 = radius * radius;
            int removed = 0;
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

                if (!GemVisualPool.TryReturn(g.Go))
                    Destroy(g.Go);
                _live.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>
        /// Rents up to <paramref name="budget"/> pending local gems from the pool.
        /// </summary>
        int DrainPendingSpawns(int budget)
        {
            if (budget <= 0 || _pending.Count == 0)
                return 0;

            if (gemVisualPrefab == null)
                gemVisualPrefab = GemVisualApplier.LoadDefaultGemPrefab();
            GemVisualPool.EnsurePrefab(gemVisualPrefab);

            int spawned = 0;
            while (spawned < budget && _pending.Count > 0)
            {
                PendingBurstGem p = _pending[0];
                _pending.RemoveAt(0);

                // --- Pool Rent (no Instantiates when idle stock remains) ---
                if (!GemVisualPool.TryRent(p.Value, out GameObject go, "GemBurstLocal") || go == null)
                {
                    // Last-resort primitive — not pooled; rare (missing prefab).
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GemBurstLocal";
                    var col = go.GetComponent<Collider>();
                    if (col != null)
                        Destroy(col);
                }

                go.transform.position = p.Position;
                go.transform.rotation = Quaternion.identity;

                _live.Add(new LocalBurstGem
                {
                    Go = go,
                    BurstCenter = p.BurstCenter,
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

        /// <summary>
        /// Picks the nearest local gem in radius for handoff (pose match to the ghost spawn).
        /// Leaves sibling live gems alone so each networked ghost can claim one. Cancels nearby
        /// pending Rents so the burst does not keep dribbling new locals after ghosts arrive.
        /// </summary>
        bool TakeNearInternal(
            Vector3 worldPosition,
            out GameObject go,
            out Vector3 velocity,
            out Vector3 angularVelocity,
            out Vector3 burstCenter)
        {
            go = null;
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            burstCenter = Vector3.zero;
            float r2 = claimRadius * claimRadius;
            int bestIndex = -1;
            float bestDistSq = float.MaxValue;

            // --- Nearest pose wins (not fastest speed) ---
            // [TITAN-ORBIT] Claiming by max speed handed a left-flying GO to a right-side ghost.
            // Soft-reconcile then yanked across the asteroid while velocity still pointed left —
            // looked like “starts on one side, flies the other way, then flips.”
            for (int i = 0; i < _live.Count; i++)
            {
                var g = _live[i];
                if (g.Go == null)
                    continue;

                float distSq = (g.Go.transform.position - worldPosition).sqrMagnitude;
                if (distSq > r2)
                    continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return false;

            var best = _live[bestIndex];
            go = best.Go;
            velocity = best.Velocity;
            angularVelocity = best.AngularVelocity;
            burstCenter = best.BurstCenter;
            // [TITAN-ORBIT] Detach from rented set so visualizer can AttachRented as network proxy.
            GemVisualPool.DetachRented(go);
            _live.RemoveAt(bestIndex);

            // --- Stop late pending Rents in this burst area ---
            // [TITAN-ORBIT] MaxSpawnsPerFrame used to leave pending=2 after spawnedNow=1; those
            // Rents after the first handoff looked like “suddenly more gems.” Drop the queue.
            CancelPendingNear(worldPosition, claimRadius * 1.5f);

            return go != null;
        }

        /// <summary>
        /// Drops queued local spawns near <paramref name="worldPosition"/> (no GO created yet).
        /// </summary>
        /// <returns>How many pending entries were removed.</returns>
        int CancelPendingNear(Vector3 worldPosition, float radius)
        {
            if (_pending.Count == 0 || radius <= 0f)
                return 0;

            float r2 = radius * radius;
            int removed = 0;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if ((_pending[i].Position - worldPosition).sqrMagnitude > r2)
                    continue;
                _pending.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        void Update()
        {
            // --- Spread remaining Rents across frames (hitch fix) ---
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
                    {
                        if (!GemVisualPool.TryReturn(g.Go))
                            Destroy(g.Go);
                    }

                    _live.RemoveAt(i);
                    continue;
                }

                // --- Same damping model as server GemMotionSystem / original Rigidbody ---
                Vector3 vel = g.Velocity;
                vel *= 1f / (1f + g.LinearDamping * dt);
                if (vel.sqrMagnitude < g.StopSpeed * g.StopSpeed)
                    vel = Vector3.zero;

                // Keep flight radially away from the asteroid center (display space).
                float3 outward = GemExplosionMath.EnsureOutwardBurstVelocity(
                    g.Go.transform.position, g.BurstCenter, new float3(vel.x, 0f, vel.z));
                vel = new Vector3(outward.x, 0f, outward.z);

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
                if (_live[i].Go == null)
                    continue;
                if (!GemVisualPool.TryReturn(_live[i].Go))
                    Destroy(_live[i].Go);
            }

            _live.Clear();
        }
    }
}

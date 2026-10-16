using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// [HYBRID] Recycles short-lived bullet muzzle / impact GameObjects so asteroid kills and
    /// high fire-rate do not Instantiates + Destroy a fresh VFX prefab every shot.
    /// <para>
    /// Why a pool: <see cref="BulletVisualFactory"/> previously called
    /// <c>Object.Instantiate</c> then <c>Object.Destroy(go, duration)</c> for every impact and
    /// muzzle flash. Runtime logs (session 74383c) showed BulletVfxDriver ticks of ~18–20 ms on
    /// kill frames with zero live tracers — that cost is Instantiates of the impact prefab, not
    /// gem bursts. Rent/Return reuses already-URP-fixed instances and only restarts particles.
    /// </para>
    /// Lives in TitanOrbit.Entities so <see cref="BulletVisualFactory"/> can call it without a
    /// Game↔Entities asmdef cycle. <see cref="Game.BulletVfxDriver"/> ticks pending returns each
    /// LateUpdate. Cosmetic only — hit authority stays on NetCode HitRpc / ECS.
    /// </summary>
    public static class BulletOneShotVfxPool
    {
        /// <summary>Idle shells ready to Rent, keyed by prefab InstanceID.</summary>
        static readonly Dictionary<int, Stack<GameObject>> s_available =
            new Dictionary<int, Stack<GameObject>>(8);

        /// <summary>Every GO this pool created — Return rejects foreign objects.</summary>
        static readonly HashSet<GameObject> s_owned = new HashSet<GameObject>();

        /// <summary>Instances currently sitting on an idle stack. Blocks a second push of the same shell.</summary>
        static readonly HashSet<GameObject> s_idle = new HashSet<GameObject>();

        /// <summary>Prefab InstanceID for each owned instance (needed on Return).</summary>
        static readonly Dictionary<GameObject, int> s_prefabKeyByInstance =
            new Dictionary<GameObject, int>(64);

        /// <summary>Active rentals waiting for their display duration to end.</summary>
        static readonly List<PendingReturn> s_pending = new List<PendingReturn>(32);

        /// <summary>Hidden parent for inactive VFX (keeps Hierarchy tidy).</summary>
        static Transform s_root;

        /// <summary>Total Instantiates performed by this pool (grows under heavy fire).</summary>
        static int s_createdTotal;

        /// <summary>Instantiates from Rent, not join prewarm. WebGL budgets this separately.</summary>
        static int s_combatCreated;

        /// <summary>Shells created per prefab InstanceID (prewarm + combat).</summary>
        static readonly Dictionary<int, int> s_countByPrefab = new Dictionary<int, int>(32);

        /// <summary>Soft cap before we log once that combat is Instantiates-growing again.</summary>
        /// Raised: budgeted prewarm intentionally creates hundreds of prepared shells.
        const int SoftMaxCreated = 2500;

        /// <summary>
        /// WebGL cannot return particle-buffer pages to the browser. Join warmup already
        /// creates one small stack per muzzle and impact (hundreds of shells). A global
        /// cap of 64 was spent before the first shot, so a volley could not grow a new
        /// shell and those shots drew nothing. Slow fire still worked: it reused the
        /// three prewarmed muzzles. Combat may add this many more, and each prefab may
        /// hold enough live shells for one burst.
        /// </summary>
        const int WebGlCombatCreateBudget = 160;

        /// <summary>Muzzles or impacts of one prefab that may exist at once on WebGL.</summary>
        const int WebGlPerPrefabCap = 24;

        static bool s_loggedSoftCap;

        /// <summary>One deferred return: when <see cref="ReturnAt"/> elapses we park the GO.</summary>
        struct PendingReturn
        {
            public GameObject Go;
            public float ReturnAt;
        }

        /// <summary>Queued prewarm work so load does not Instantiates 1000+ VFX in one frame.</summary>
        struct PrewarmJob
        {
            public GameObject Prefab;
            public int TargetCount;
        }

        static readonly List<PrewarmJob> s_prewarmQueue = new List<PrewarmJob>(64);
        static int s_prewarmQueueIndex;

        /// <summary>How many Instantiates this pool has performed this session (debug / HUD).</summary>
        public static int CreatedTotal => s_createdTotal;

        /// <summary>True when the budgeted prewarm queue is empty.</summary>
        public static bool PrewarmComplete =>
            s_prewarmQueue.Count == 0 || s_prewarmQueueIndex >= s_prewarmQueue.Count;

        /// <summary>
        /// 0–1 fill of the budgeted Instantiates queue (jobs finished / jobs enqueued).
        /// Join-load graphics warmup uses this for the loading bar's VFX slice.
        /// </summary>
        public static float PrewarmProgress
        {
            get
            {
                if (s_prewarmQueue.Count <= 0)
                    return 1f;
                return Mathf.Clamp01((float)s_prewarmQueueIndex / s_prewarmQueue.Count);
            }
        }

        /// <summary>
        /// [UNITY] Domain reload / enter Play without Domain Reload — drop stale GO refs.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_available.Clear();
            s_owned.Clear();
            s_prefabKeyByInstance.Clear();
            s_idle.Clear();
            s_pending.Clear();
            s_prewarmQueue.Clear();
            s_prewarmQueueIndex = 0;
            s_countByPrefab.Clear();
            s_root = null;
            s_createdTotal = 0;
            s_combatCreated = 0;
            s_loggedSoftCap = false;
        }

        /// <summary>
        /// Ensures the inactive-parent root exists under DontDestroyOnLoad.
        /// </summary>
        static void EnsureRoot()
        {
            if (s_root != null)
                return;

            var go = new GameObject("BulletOneShotVfxPool");
            go.AddComponent<VfxUrpCompat.VfxPoolRootMarker>();
            Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            s_root = go.transform;
        }

        /// <summary>
        /// Queues a prewarm target so <see cref="TickPrewarm"/> can Instantiates over many frames.
        /// Dedupes by prefab: keeps the highest target count requested.
        /// </summary>
        /// <param name="prefab">Muzzle or impact prefab (null = no-op).</param>
        /// <param name="count">Desired idle stack depth for this prefab.</param>
        public static void EnqueuePrewarm(GameObject prefab, int count)
        {
            if (prefab == null || count <= 0)
                return;

            for (int i = 0; i < s_prewarmQueue.Count; i++)
            {
                if (s_prewarmQueue[i].Prefab != prefab)
                    continue;
                if (s_prewarmQueue[i].TargetCount < count)
                {
                    var job = s_prewarmQueue[i];
                    job.TargetCount = count;
                    s_prewarmQueue[i] = job;
                }
                return;
            }

            s_prewarmQueue.Add(new PrewarmJob { Prefab = prefab, TargetCount = count });
        }

        /// <summary>
        /// Instantiates up to <paramref name="budget"/> prepared idle shells from the prewarm queue.
        /// Call once per presentation frame from BulletVfxDriver until <see cref="PrewarmComplete"/>.
        /// </summary>
        /// <param name="budget">Max Instantiates this frame (keep small — Sci-Fi VFX are heavy).</param>
        /// <returns>How many Instantiates ran this tick.</returns>
        public static int TickPrewarm(int budget)
        {
            if (budget <= 0 || PrewarmComplete)
                return 0;

            int created = 0;
            while (created < budget && s_prewarmQueueIndex < s_prewarmQueue.Count)
            {
                var job = s_prewarmQueue[s_prewarmQueueIndex];
                int before = s_createdTotal;
                // Create at most (budget - created) shells toward this job's target.
                int have = 0;
                int key = job.Prefab.GetInstanceID();
                if (s_available.TryGetValue(key, out var stack))
                    have = stack.Count;
                int need = job.TargetCount - have;
                if (need <= 0)
                {
                    s_prewarmQueueIndex++;
                    continue;
                }

                int slice = Mathf.Min(need, budget - created);
                Prewarm(job.Prefab, have + slice);
                created += s_createdTotal - before;
                if (s_available.TryGetValue(key, out stack) && stack.Count >= job.TargetCount)
                    s_prewarmQueueIndex++;
            }

            return created;
        }

        /// <summary>
        /// Pre-creates idle shells for <paramref name="prefab"/> so the first kill/fire does not
        /// pay Instantiates on the combat frame. Prefer <see cref="EnqueuePrewarm"/> +
        /// <see cref="TickPrewarm"/> at load so one frame does not Instantiates hundreds of shells
        /// (session 74383c: spawnMs 531 ms creating 1204 at once).
        /// </summary>
        /// <param name="prefab">Muzzle or impact prefab (or null — no-op).</param>
        /// <param name="count">How many idle instances to ensure on the stack.</param>
        public static void Prewarm(GameObject prefab, int count)
        {
            if (prefab == null || count <= 0)
                return;

            EnsureRoot();
            int key = prefab.GetInstanceID();
            if (!s_available.TryGetValue(key, out var stack))
            {
                stack = new Stack<GameObject>(count);
                s_available[key] = stack;
            }

            while (stack.Count < count)
            {
                // Parent is inactive, so playOnAwake does not run at the origin.
                var instance = Object.Instantiate(prefab, s_root, false);
                instance.name = prefab.name + "_Pooled";
                instance.SetActive(false);
                TitanOrbit.Core.VfxUrpCompat.PrepareVfxInstance(instance, playParticles: false);
                TitanOrbit.Core.VfxUrpCompat.ApplyImpactVisualScale(instance, 1f);
                s_owned.Add(instance);
                s_prefabKeyByInstance[instance] = key;
                NoteCreated(key, combat: false);
                stack.Push(instance);
                s_idle.Add(instance);
            }
        }

        /// <summary>
        /// How many flashes are waiting to Return (debug).
        /// </summary>
        public static int PendingCount => s_pending.Count;

        /// <summary>
        /// Rents an inactive instance of <paramref name="prefab"/>, or Instantiates one if the
        /// stack is empty. Caller places it, then <see cref="TitanOrbit.Core.VfxUrpCompat.PrepareVfxInstance"/>
        /// turns it on at that pose. Enabling here plays the burst at the pool, and a volley
        /// of same-frame restarts then emits nothing.
        /// </summary>
        /// <param name="prefab">Impact or muzzle prefab from BulletVfxBank.</param>
        /// <param name="instance">Inactive GO ready for pose + PrepareVfxInstance.</param>
        /// <param name="grew">True when this Rent paid Instantiates (cold path).</param>
        /// <returns>True when a usable instance was produced.</returns>
        public static bool TryRent(GameObject prefab, out GameObject instance, out bool grew)
        {
            instance = null;
            grew = false;
            if (prefab == null)
                return false;

            EnsureRoot();
            int key = prefab.GetInstanceID();

            if (s_available.TryGetValue(key, out var stack))
            {
                while (stack.Count > 0 && instance == null)
                {
                    instance = stack.Pop();
                    if (instance != null)
                        s_idle.Remove(instance);
                }
            }

            if (instance == null)
            {
                if (!CanCreateForCombat(key))
                {
                    // Volley is larger than this prefab's stack. Reuse the oldest live
                    // flash of the same prefab instead of dropping the shot.
                    if (!TryReclaimPending(key, out instance))
                        return false;
                }
                else
                {
                // --- Cold path: Instantiates once, then reuse forever for this prefab ---
                // Inactive parent so playOnAwake waits for the shot pose, even in a volley.
                instance = Object.Instantiate(prefab, s_root, false);
                instance.name = prefab.name + "_Pooled";
                instance.SetActive(false);
                TitanOrbit.Core.VfxUrpCompat.PrepareVfxInstance(instance, playParticles: false);
                TitanOrbit.Core.VfxUrpCompat.ApplyImpactVisualScale(instance, 1f);
                s_owned.Add(instance);
                s_prefabKeyByInstance[instance] = key;
                NoteCreated(key, combat: true);
                grew = true;

                if (s_createdTotal > SoftMaxCreated && !s_loggedSoftCap)
                {
                    s_loggedSoftCap = true;
                    Debug.LogWarning(
                        "[BulletOneShotVfxPool] Created " + s_createdTotal +
                        " one-shot VFX shells (soft cap " + SoftMaxCreated +
                        "). Fire-rate / kill density is Instantiates-growing the pool.");
                }
                }
            }

            if (instance == null)
                return false;

            // Deactivate before unparent. Unparenting an active child of the inactive
            // pool would enable it at the origin and the shot's Play would no-op.
            if (instance.activeSelf)
                instance.SetActive(false);
            instance.transform.SetParent(null, false);
            return true;
        }

        /// <summary>Convenience overload when the caller does not need the grew flag.</summary>
        public static bool TryRent(GameObject prefab, out GameObject instance)
        {
            return TryRent(prefab, out instance, out _);
        }

        /// <summary>
        /// Rents only if an idle shell already exists — never Instantiates.
        /// Join-load graphics warmup uses this so a <c>Camera.Render</c> of a particle
        /// program does not grow the pool on the overlay frame.
        /// </summary>
        /// <param name="prefab">Muzzle or impact prefab from the bank.</param>
        /// <param name="instance">Active idle shell, or null when the stack is empty.</param>
        /// <returns>True when an existing idle instance was activated.</returns>
        public static bool TryRentIdle(GameObject prefab, out GameObject instance)
        {
            instance = null;
            if (prefab == null)
                return false;

            int key = prefab.GetInstanceID();
            if (!s_available.TryGetValue(key, out var stack) || stack.Count == 0)
                return false;

            return TryRent(prefab, out instance, out _);
        }

        /// <summary>
        /// Schedules Return after <paramref name="durationSeconds"/> (replaces Object.Destroy delay).
        /// Safe if <paramref name="instance"/> was not rented here — falls back to Destroy.
        /// </summary>
        public static void ScheduleReturn(GameObject instance, float durationSeconds)
        {
            if (instance == null)
                return;

            if (!s_owned.Contains(instance))
            {
                Object.Destroy(instance, Mathf.Max(0.05f, durationSeconds));
                return;
            }

            float returnAt = Time.unscaledTime + Mathf.Max(0.05f, durationSeconds);
            for (int i = 0; i < s_pending.Count; i++)
            {
                if (s_pending[i].Go != instance)
                    continue;

                // One timer per shell. A second schedule used to fire mid-volley and park the live flash.
                PendingReturn updated = s_pending[i];
                updated.ReturnAt = returnAt;
                s_pending[i] = updated;
                return;
            }

            s_pending.Add(new PendingReturn
            {
                Go = instance,
                ReturnAt = returnAt,
            });
        }

        /// <summary>
        /// Parks expired one-shots back into the idle stack. Call once per presentation frame
        /// from BulletVfxDriver (client only).
        /// </summary>
        public static void TickReturns()
        {
            if (s_pending.Count == 0)
                return;

            float now = Time.unscaledTime;
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                var entry = s_pending[i];
                if (now < entry.ReturnAt)
                    continue;

                s_pending.RemoveAt(i);
                ReturnNow(entry.Go);
            }
        }

        /// <summary>
        /// Immediately deactivates and stacks a rented instance. The next rent enables it
        /// at the new shot. Public so looping status VFX can return when burn / shock ends.
        /// </summary>
        public static void ReturnNow(GameObject instance)
        {
            if (instance == null)
                return;

            if (!s_owned.Contains(instance) ||
                !s_prefabKeyByInstance.TryGetValue(instance, out int key))
            {
                Object.Destroy(instance);
                return;
            }

            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                if (s_pending[i].Go == instance)
                    s_pending.RemoveAt(i);
            }

            // Disable only. Stop+Clear in the same frame as the next rent's Play
            // is what blanks a volley. The next enable starts playOnAwake clean.
            EnsureRoot();
            instance.SetActive(false);
            instance.transform.SetParent(s_root, false);

            if (!s_available.TryGetValue(key, out var stack))
            {
                stack = new Stack<GameObject>(8);
                s_available[key] = stack;
            }

            if (s_idle.Add(instance))
                stack.Push(instance);
        }

        static void NoteCreated(int key, bool combat)
        {
            s_createdTotal++;
            if (combat)
                s_combatCreated++;
            s_countByPrefab.TryGetValue(key, out int n);
            s_countByPrefab[key] = n + 1;
        }

        /// <summary>
        /// Desktop always creates. WebGL creates until this prefab can cover a burst,
        /// and until combat (not warmup) has added <see cref="WebGlCombatCreateBudget"/> shells.
        /// </summary>
        static bool CanCreateForCombat(int key)
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer)
                return true;

            s_countByPrefab.TryGetValue(key, out int have);
            if (have >= WebGlPerPrefabCap)
                return false;
            return s_combatCreated < WebGlCombatCreateBudget;
        }

        /// <summary>
        /// Hands back the oldest in-flight shell of <paramref name="key"/> so a shot
        /// still plays when WebGL will not allocate another.
        /// </summary>
        static bool TryReclaimPending(int key, out GameObject instance)
        {
            instance = null;
            int index = -1;
            float oldest = float.MaxValue;
            for (int i = 0; i < s_pending.Count; i++)
            {
                GameObject go = s_pending[i].Go;
                if (go == null)
                    continue;
                if (!s_prefabKeyByInstance.TryGetValue(go, out int pendingKey) || pendingKey != key)
                    continue;
                if (s_pending[i].ReturnAt >= oldest)
                    continue;
                oldest = s_pending[i].ReturnAt;
                index = i;
            }

            if (index < 0)
                return false;

            instance = s_pending[index].Go;
            s_pending.RemoveAt(index);
            if (instance.activeSelf)
                instance.SetActive(false);
            instance.transform.SetParent(null, false);
            return instance != null;
        }
    }
}

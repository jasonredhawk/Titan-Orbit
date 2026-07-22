using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Recycles gem crystal GameObject proxies so asteroid bursts, ship gem dumps,
    /// and combat spills do not Instantiates/Destroy every nugget.
    /// <para>
    /// Why a pool: each Instantiates of <c>Gem.prefab</c> paid Rigidbody/Collider setup, mesh
    /// clone cost, and (on first use) URP transparent material/shader work — that hitch showed
    /// up as a lag spike and a one-frame blank gem. Rent/Return reuses already-stripped,
    /// already-tinted shells. Cosmetic only — pickup authority stays on ECS gem ghosts.
    /// </para>
    /// Used by <see cref="GemVisualApplier"/> and networked gem proxies in
    /// <see cref="EcsWorldVisualizer"/> (pool Rent — no Instantiates when warm).
    /// <see cref="EcsWorldVisualizer"/> DestroyProxy.
    /// </summary>
    public static class GemVisualPool
    {
        /// <summary>
        /// How many inactive gems to build at scene load. Covers a few overlapping asteroid
        /// bursts without growing the pool on the destroy frame.
        /// </summary>
        public const int DefaultPrewarmCount = 32;

        /// <summary>
        /// Soft cap — we still grow past this if needed (never drop a gem visual), but log once
        /// so designers know Instantiates is happening again under heavy dump load.
        /// </summary>
        const int SoftMaxCreated = 128;

        /// <summary>Inactive ready-to-rent shells (LIFO so a just-Returned gem is reused next).</summary>
        static readonly Stack<GameObject> s_available = new Stack<GameObject>(64);

        /// <summary>Every GO this pool created — used to accept Returns and ignore foreign objects.</summary>
        static readonly HashSet<GameObject> s_owned = new HashSet<GameObject>();

        /// <summary>Currently rented (active) instances — Return only accepts these.</summary>
        static readonly HashSet<GameObject> s_rented = new HashSet<GameObject>();

        /// <summary>Hidden parent for inactive gems (keeps Hierarchy tidy).</summary>
        static Transform s_root;

        /// <summary>Prefab used when the pool must Instantiates a brand-new shell.</summary>
        static GameObject s_prefab;

        /// <summary>Total Instantiates ever performed by this pool (grows when demand exceeds stock).</summary>
        static int s_createdTotal;

        /// <summary>True after <see cref="Prewarm"/> has run once this play session.</summary>
        static bool s_prewarmed;

        /// <summary>Logs the soft-cap warning only once per session.</summary>
        static bool s_loggedSoftCap;

        /// <summary>
        /// [UNITY] Domain reload / enter Play Mode without Domain Reload — clear statics so
        /// destroyed GOs are not kept in stacks across sessions.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_available.Clear();
            s_owned.Clear();
            s_rented.Clear();
            s_root = null;
            s_prefab = null;
            s_createdTotal = 0;
            s_prewarmed = false;
            s_loggedSoftCap = false;
        }

        /// <summary>
        /// [TITAN-ORBIT] Scene-load prewarm: Instantiates + strip + tint off the combat hot path,
        /// then parks shells inactive under the pool root. Also builds the shared gem tint material.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoPrewarm()
        {
            // Prefab may still be null here — EnsurePrefab resolves Resources/Editor path.
            EnsurePrefab(null);
            Prewarm(DefaultPrewarmCount);
        }

        /// <summary>
        /// Remembers which prefab to Instantiates when the pool must grow.
        /// Safe to call every Rent — only the first non-null wins unless cleared.
        /// </summary>
        /// <param name="prefab">Gem crystal prefab (inspector or Resources).</param>
        public static void EnsurePrefab(GameObject prefab)
        {
            if (s_prefab != null)
                return;

            if (prefab != null)
            {
                s_prefab = prefab;
                return;
            }

            // [STANDARD] Fall back to project default (Editor AssetDatabase or Resources/Gem).
            s_prefab = GemVisualApplier.LoadDefaultGemPrefab();
        }

        /// <summary>
        /// Builds inactive gem shells now so the first asteroid destroy does not Instantiates.
        /// Idempotent — later calls only top up if the available stack is below <paramref name="count"/>.
        /// </summary>
        /// <param name="count">Target number of idle shells in the stack.</param>
        public static void Prewarm(int count)
        {
            EnsurePrefab(null);
            if (s_prefab == null)
                return;

            EnsureRoot();

            // --- Material/shader warm (once) — must happen before any combat Rent ---
            // [TITAN-ORBIT] Shared tint used to be built on first destroy; that hitch + blank frame
            // was the "materials don't show for a split second" bug.
            GemVisualApplier.EnsureSharedTintReady(s_prefab);

            int need = Mathf.Max(0, count - s_available.Count);
            for (int i = 0; i < need; i++)
            {
                GameObject shell = CreateNewInactiveShell();
                if (shell == null)
                    break;
                s_available.Push(shell);
            }

            s_prewarmed = true;
        }

        /// <summary>
        /// Rents an active gem visual, scaled for <paramref name="gemValue"/>.
        /// Grows the pool (Instantiates) only when the idle stack is empty.
        /// </summary>
        /// <param name="gemValue">Authoritative gem value — drives uniform scale only.</param>
        /// <param name="instance">Active GameObject ready to place in the world.</param>
        /// <param name="instanceName">Hierarchy name (GemTagProxy vs GemBurstLocal).</param>
        /// <returns>False only when the prefab is missing and Instantiates cannot run.</returns>
        public static bool TryRent(float gemValue, out GameObject instance, string instanceName = "GemTagProxy")
        {
            instance = null;
            EnsurePrefab(null);
            if (s_prefab == null && s_available.Count == 0)
                return false;

            EnsureRoot();
            GemVisualApplier.EnsureSharedTintReady(s_prefab);

            // --- Prefer idle shell (LIFO: just-Returned burst gem is reused by network handoff) ---
            while (s_available.Count > 0 && instance == null)
            {
                GameObject candidate = s_available.Pop();
                if (candidate == null)
                {
                    // [STANDARD] Unity destroyed it externally — drop and try next.
                    continue;
                }

                instance = candidate;
            }

            // --- Grow on demand (combat dump larger than prewarm) ---
            if (instance == null)
            {
                instance = CreateNewInactiveShell();
                if (instance == null)
                    return false;

                if (s_createdTotal > SoftMaxCreated && !s_loggedSoftCap)
                {
                    s_loggedSoftCap = true;
                    Debug.LogWarning(
                        $"[GemVisualPool] Created {s_createdTotal} gem visuals (soft cap {SoftMaxCreated}). " +
                        "Heavy gem dumps are growing the pool — consider raising DefaultPrewarmCount.");
                }
            }

            // --- Activate for world use ---
            instance.name = instanceName;
            instance.transform.SetParent(null, false);
            instance.transform.localScale = Vector3.one * GemVisualApplier.ComputeVisualScale(gemValue);
            instance.transform.rotation = Quaternion.identity;
            instance.SetActive(true);
            s_rented.Add(instance);
            return true;
        }

        /// <summary>
        /// Returns a rented gem to the idle stack. No-ops (returns false) for null, foreign, or
        /// already-returned objects so <see cref="EcsWorldVisualizer"/> can fall back to Destroy.
        /// </summary>
        /// <param name="instance">Previously rented gem visual.</param>
        /// <returns>True when the GO was parked inactive for reuse.</returns>
        public static bool TryReturn(GameObject instance)
        {
            if (instance == null)
                return false;

            // --- Only accept GOs this pool created ---
            if (!s_owned.Contains(instance))
                return false;

            // Already idle (inactive, not rented) — treat as success so callers do not Destroy.
            if (!s_rented.Remove(instance) && !instance.activeSelf)
                return true;

            // --- Reset motion driver so a later Rent does not keep a dead Entity bind ---
            var motion = instance.GetComponent<GemClientMotionApplier>();
            if (motion != null)
                motion.Unbind();

            EnsureRoot();
            instance.SetActive(false);
            instance.transform.SetParent(s_root, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            s_available.Push(instance);
            return true;
        }

        /// <summary>
        /// Marks a rented gem as no longer rented without parking it.
        /// Kept for rare ownership-transfer cases; gem burst handoff no longer uses this.
        /// </summary>
        public static void DetachRented(GameObject instance)
        {
            if (instance == null)
                return;
            s_rented.Remove(instance);
        }

        /// <summary>
        /// Re-registers a transferred gem as rented (after local-burst → network handoff).
        /// </summary>
        public static void AttachRented(GameObject instance)
        {
            if (instance == null || !s_owned.Contains(instance))
                return;
            s_rented.Add(instance);
        }

        /// <summary>True when <paramref name="instance"/> was Instantiated by this pool.</summary>
        public static bool Owns(GameObject instance) =>
            instance != null && s_owned.Contains(instance);

        /// <summary>Idle + rented counts for diagnostics.</summary>
        public static void GetCounts(out int available, out int rented, out int createdTotal)
        {
            available = s_available.Count;
            rented = s_rented.Count;
            createdTotal = s_createdTotal;
        }

        /// <summary>Creates the hidden pool root once.</summary>
        static void EnsureRoot()
        {
            if (s_root != null)
                return;

            var go = new GameObject("GemVisualPool");
            Object.DontDestroyOnLoad(go);
            go.SetActive(true);
            s_root = go.transform;
        }

        /// <summary>
        /// Cold Instantiates path: strip physics immediately, apply shared tint, park inactive.
        /// This is the only place that should Instantiates a gem visual during gameplay growth.
        /// </summary>
        static GameObject CreateNewInactiveShell()
        {
            if (s_prefab == null)
                return null;

            // --- Instantiates + strip (DestroyImmediate so PhysX never registers on the proxy) ---
            if (!GemVisualApplier.TryCreateGemVisualRaw(s_prefab, gemValue: 1f, immediateStrip: true, out GameObject shell)
                || shell == null)
            {
                return null;
            }

            shell.name = "GemPooled";
            shell.SetActive(false);
            shell.transform.SetParent(s_root, false);
            s_owned.Add(shell);
            s_createdTotal++;
            return shell;
        }
    }
}

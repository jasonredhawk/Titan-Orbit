using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// [HYBRID] Recycles in-flight bullet tracer GameObjects (root + projectile visual child).
    /// <para>
    /// Why: <see cref="Game.BulletVfxDriver"/> used to <c>new GameObject</c> +
    /// <c>Object.Instantiate</c> the projectile particle prefab on every shot. Session 74383c
    /// destroy-probe showed <c>spawnMs:14.21</c> on the first fire volley while muzzle/impact
    /// were already pooled — that Instantiates cost is the tracer mesh, not gems.
    /// </para>
    /// Cosmetic only. Keyed by projectile prefab InstanceID (or a sentinel for procedural fallbacks).
    /// </summary>
    public static class BulletTracerPool
    {
        /// <summary>Key used when bank has no projectile prefab (procedural sphere/trail).</summary>
        const int ProceduralKey = 1;

        static readonly Dictionary<int, Stack<GameObject>> s_available =
            new Dictionary<int, Stack<GameObject>>(16);

        static readonly HashSet<GameObject> s_owned = new HashSet<GameObject>();
        static readonly Dictionary<GameObject, int> s_keyByInstance = new Dictionary<GameObject, int>(64);

        static Transform s_root;
        static int s_createdTotal;

        /// <summary>How many tracer shells this pool Instantiates this session.</summary>
        public static int CreatedTotal => s_createdTotal;

        /// <summary>[UNITY] Clear on domain / Play Mode enter without Domain Reload.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_available.Clear();
            s_owned.Clear();
            s_keyByInstance.Clear();
            s_root = null;
            s_createdTotal = 0;
        }

        static void EnsureRoot()
        {
            if (s_root != null)
                return;

            var go = new GameObject("BulletTracerPool");
            Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            s_root = go.transform;
        }

        /// <summary>
        /// Pre-creates idle tracer shells for a projectile prefab so the first volley is warm.
        /// </summary>
        public static void Prewarm(GameObject projectilePrefab, int count)
        {
            if (count <= 0)
                return;

            EnsureRoot();
            int key = projectilePrefab != null ? projectilePrefab.GetInstanceID() : ProceduralKey;
            if (!s_available.TryGetValue(key, out var stack))
            {
                stack = new Stack<GameObject>(count);
                s_available[key] = stack;
            }

            while (stack.Count < count)
            {
                GameObject go = CreateShell(projectilePrefab, key);
                if (go == null)
                    break;
                go.SetActive(false);
                go.transform.SetParent(s_root, false);
                stack.Push(go);
            }
        }

        /// <summary>
        /// Rents a tracer root (visual child already attached). Caller sets pose + restarts particles.
        /// </summary>
        /// <param name="grew">True when this Rent paid Instantiates.</param>
        public static bool TryRent(
            GameObject projectilePrefab,
            out GameObject root,
            out bool grew)
        {
            root = null;
            grew = false;
            EnsureRoot();

            int key = projectilePrefab != null ? projectilePrefab.GetInstanceID() : ProceduralKey;
            if (s_available.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                root = stack.Pop();
            }
            else
            {
                root = CreateShell(projectilePrefab, key);
                grew = true;
            }

            if (root == null)
                return false;

            root.transform.SetParent(null, false);
            root.SetActive(true);
            return true;
        }

        /// <summary>
        /// Parks a tracer back into the idle stack. Clears trails/particles for clean reuse.
        /// Falls back to Destroy when the GO was not rented from this pool.
        /// </summary>
        public static void Return(GameObject root)
        {
            if (root == null)
                return;

            if (!s_owned.Contains(root) || !s_keyByInstance.TryGetValue(root, out int key))
            {
                Object.Destroy(root);
                return;
            }

            // --- Stop particles / clear trails so the next Rent starts clean ---
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                    systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            var trails = root.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] != null)
                    trails[i].Clear();
            }

            // Stretch writes child localScale.Z during flight — restore uniform XY so the
            // next Rent does not start from a longer slug.
            if (root.transform.childCount > 0)
            {
                Transform visual = root.transform.GetChild(0);
                Vector3 s = visual.localScale;
                float uniform = Mathf.Max(0.01f, (Mathf.Abs(s.x) + Mathf.Abs(s.y)) * 0.5f);
                visual.localScale = new Vector3(uniform, uniform, uniform);
                visual.localPosition = Vector3.zero;
            }

            EnsureRoot();
            root.SetActive(false);
            root.transform.SetParent(s_root, false);

            if (!s_available.TryGetValue(key, out var stack))
            {
                stack = new Stack<GameObject>(8);
                s_available[key] = stack;
            }

            stack.Push(root);
        }

        /// <summary>
        /// Builds one pooled shell: empty root + projectile visual (prefab or procedural).
        /// </summary>
        static GameObject CreateShell(GameObject projectilePrefab, int key)
        {
            var root = new GameObject("BulletTracer_Pooled");
            if (projectilePrefab != null)
            {
                var visual = Object.Instantiate(projectilePrefab, root.transform);
                visual.name = "ProjectileVisual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                VfxUrpCompat.FixAllIn1MaterialsForUrp(visual);
            }
            else
            {
                // Procedural fallback — rare when bank prefab missing.
                var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                visual.name = "ProjectileVisual";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localScale = Vector3.one * 0.15f;
                var col = visual.GetComponent<Collider>();
                if (col != null)
                    Object.Destroy(col);
            }

            s_owned.Add(root);
            s_keyByInstance[root] = key;
            s_createdTotal++;
            return root;
        }

        /// <summary>
        /// Prewarms tracers for the first few bank categories × two teams (client fire path).
        /// </summary>
        public static void PrewarmFromBank(BulletVfxBank bank, int categoryCap = 4, int perPrefab = 4)
        {
            if (bank == null || Application.isMobilePlatform)
                return;

            int catCount = Mathf.Min(bank.CategoryCount, categoryCap);
            for (int i = 0; i < catCount; i++)
            {
                Prewarm(bank.GetProjectileVisualPrefab(i, TeamId.TeamA), perPrefab);
                Prewarm(bank.GetProjectileVisualPrefab(i, TeamId.TeamB), perPrefab);
            }
        }
    }
}

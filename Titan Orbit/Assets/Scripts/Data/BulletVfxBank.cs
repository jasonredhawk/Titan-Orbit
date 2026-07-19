using System;
using System.Collections.Generic;
using System.Reflection;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [HYBRID] Projectile bank for client bullet VFX (Sci-Fi Arsenal demo prefabs). Each category maps
    /// to a <see cref="BulletBankProfile"/> for gameplay modifiers and a list of team-colored prefabs.
    /// Team color picks the variant (e.g. LaserBoltBlueOBJ for TeamB). Loaded by
    /// <see cref="Game.BulletVfxDriver"/> — does not affect server hit detection.
    /// </summary>
    [CreateAssetMenu(fileName = "BulletVfxBank", menuName = "Titan Orbit/Bullet VFX Bank")]
    public class BulletVfxBank : ScriptableObject
    {
        const string DefaultAssetPath = "Assets/Data/BulletVfxBank.asset";

        /// <summary>One row in the bank — name, prefab variants, and paired gameplay profile.</summary>
        [Serializable]
        public class Category
        {
            public string categoryName = "Laserbolt";
            public List<GameObject> prefabs = new List<GameObject>();
            public BulletBankProfile profile = new BulletBankProfile();
        }

        [SerializeField] List<Category> categories = new List<Category>();
        [SerializeField] GameObject fallbackImpactPrefab;
        [SerializeField] float visualScaleMultiplier = 0.5f;

        /// <summary>Global scale on spawned VFX instances (designer tuning).</summary>
        public float VisualScaleMultiplier => Mathf.Max(0.05f, visualScaleMultiplier);
        public int CategoryCount => categories != null ? categories.Count : 0;

        /// <summary>Loads Resources/BulletVfxBank or editor default path — whichever exists first.</summary>
        public static BulletVfxBank LoadDefault()
        {
            // --- LoadDefault ---
            var fromResources = Resources.Load<BulletVfxBank>("BulletVfxBank");
            if (fromResources != null)
                return fromResources;

#if UNITY_EDITOR
            var fromPath = UnityEditor.AssetDatabase.LoadAssetAtPath<BulletVfxBank>(DefaultAssetPath);
            if (fromPath != null)
                return fromPath;
#endif
            return null;
        }

        /// <summary>
        /// Picks a prefab from category <paramref name="index"/> whose name contains the team color token.
        /// Falls back to first non-null prefab in the category.
        /// </summary>
        public GameObject GetBankPrefab(int index, TeamId team)
        {
            // --- Compute value ---
            if (categories == null || index < 0 || index >= categories.Count)
                return null;

            var cat = categories[index];
            if (cat?.prefabs == null || cat.prefabs.Count == 0)
                return null;

            string colorName = GetColorNameForTeam(team);
            foreach (GameObject prefab in cat.prefabs)
            {
                if (prefab != null && prefab.name.IndexOf(colorName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return prefab;
            }

            foreach (GameObject prefab in cat.prefabs)
            {
                if (prefab != null)
                    return prefab;
            }

            return null;
        }

        /// <summary>Sci-Fi Arsenal projectile particle child prefab for in-flight tracer.</summary>
        public GameObject GetProjectileVisualPrefab(int index, TeamId team)
        {
            GameObject bankPrefab = GetBankPrefab(index, team);
            return TryGetSciFiParticlePrefab(bankPrefab, "projectileParticle");
        }

        /// <summary>Muzzle flash prefab at fire time.</summary>
        public GameObject GetMuzzlePrefab(int index, TeamId team)
        {
            GameObject bankPrefab = GetBankPrefab(index, team);
            return TryGetSciFiParticlePrefab(bankPrefab, "muzzleParticle");
        }

        /// <summary>Impact burst prefab on hit; uses <see cref="fallbackImpactPrefab"/> when bank has none.</summary>
        public GameObject GetImpactPrefab(int index, TeamId team)
        {
            // --- Compute value ---
            GameObject bankPrefab = GetBankPrefab(index, team);
            GameObject impact = TryGetSciFiParticlePrefab(bankPrefab, "impactParticle");
            return impact != null ? impact : fallbackImpactPrefab;
        }

        /// <summary>Gameplay profile paired with VFX category index (damage multipliers, burn, etc.).</summary>
        public bool TryGetProfile(int index, out BulletBankProfile profile)
        {
            // --- Attempt resolution ---
            profile = null;
            if (categories == null || index < 0 || index >= categories.Count)
                return false;

            var cat = categories[index];
            if (cat == null)
                return false;

            profile = cat.profile ?? new BulletBankProfile();
            return true;
        }

        /// <summary>[TITAN-ORBIT] Maps <see cref="TeamId"/> to Sci-Fi Arsenal color token in prefab names.</summary>
        static string GetColorNameForTeam(TeamId team)
        {
            // --- Compute value ---
            switch (team)
            {
                case TeamId.TeamA: return "Red";
                case TeamId.TeamB: return "Blue";
                case TeamId.TeamC: return "Green";
                case TeamId.TeamD: return "Yellow";
                case TeamId.TeamE: return "Purple";
                default: return "Blue";
            }
        }

        /// <summary>
        /// Sci-Fi Arsenal lives in Assembly-CSharp; TitanOrbit.Data cannot reference it directly.
        /// Read public prefab fields from <c>SciFiProjectileScript</c> via reflection.
        /// </summary>
        static GameObject TryGetSciFiParticlePrefab(GameObject bankPrefab, string fieldName)
        {
            // --- Attempt resolution ---
            if (bankPrefab == null || string.IsNullOrEmpty(fieldName))
                return null;

            foreach (MonoBehaviour script in bankPrefab.GetComponents<MonoBehaviour>())
            {
                if (script == null || script.GetType().Name != "SciFiProjectileScript")
                    continue;

                FieldInfo field = script.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
                return field?.GetValue(script) as GameObject;
            }

            return null;
        }
    }
}

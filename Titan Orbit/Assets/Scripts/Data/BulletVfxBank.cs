using System;
using System.Collections.Generic;
using System.Reflection;
using TitanOrbit.Core;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [HYBRID] Single project-wide projectile VFX bank (Sci-Fi Arsenal team-colored prefabs).
    /// One asset at <c>Resources/BulletVfxBank</c> so Editor and player builds share the same file
    /// (no Data + Resources duplicate to keep in sync).
    /// <para>
    /// Each category maps to a <see cref="BulletBankProfile"/> and team-colored prefabs. Loaded by
    /// <see cref="Game.BulletVfxDriver"/>. Presentation only — hit detection stays server-side.
    /// </para>
    /// <para>
    /// Inspector scale knobs:
    /// <list type="bullet">
    /// <item><b>Global Visual Scale Multiplier</b> — shrink/grow every bullet VFX.</item>
    /// <item><b>Upgrade Visual Scale Multiplier</b> — how much tier/attribute fire-power growth
    /// becomes size (0.5 = half-step: 3→8 fire → ~1.83× size, not 2.67×).</item>
    /// </list>
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "BulletVfxBank", menuName = "Titan Orbit/Bullet VFX Bank")]
    public class BulletVfxBank : ScriptableObject
    {
        /// <summary>[UNITY] Sole asset path — Resources so builds can <see cref="Resources.Load"/>.</summary>
        public const string ResourcesAssetPath = "Assets/Resources/BulletVfxBank.asset";

        /// <summary>Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "BulletVfxBank";

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

        [Header("Visual scale (all categories)")]
        [Tooltip("Multiplies every spawned bullet / impact VFX after per-shot fire-power scale. 1 = authored size; 0.25 = quarter size.")]
        [FormerlySerializedAs("visualScaleMultiplier")]
        [SerializeField] float globalVisualScaleMultiplier = 0.25f;

        [Tooltip("How much fire-power growth above level-1 becomes bullet size. 1 = size tracks damage 1:1; 0.5 = half-step (3→8 fire ≈ 1.83× size).")]
        [SerializeField] float upgradeVisualScaleMultiplier = 0.5f;

        /// <summary>Global VFX size on spawned instances.</summary>
        public float GlobalVisualScaleMultiplier => Mathf.Max(0.05f, globalVisualScaleMultiplier);

        /// <summary>
        /// Fraction of (damage/reference − 1) applied to visual size.
        /// Callers push this into <c>BulletVisualScale.ActiveUpgradeVisualScaleMultiplier</c> on load.
        /// </summary>
        public float UpgradeVisualScaleMultiplier => Mathf.Clamp01(upgradeVisualScaleMultiplier);

        /// <summary>[LEGACY] Alias of <see cref="GlobalVisualScaleMultiplier"/> for older call sites.</summary>
        public float VisualScaleMultiplier => GlobalVisualScaleMultiplier;

        public int CategoryCount => categories != null ? categories.Count : 0;

        /// <summary>
        /// Loads the single Resources bank (Editor + player builds). No Data/ folder duplicate.
        /// </summary>
        public static BulletVfxBank LoadDefault()
        {
            // --- One asset only (Resources) ---
            var bank = Resources.Load<BulletVfxBank>(ResourcesLoadName);
#if UNITY_EDITOR
            if (bank == null)
                bank = UnityEditor.AssetDatabase.LoadAssetAtPath<BulletVfxBank>(ResourcesAssetPath);
#endif
            return bank;
        }

        /// <summary>
        /// Picks a prefab from category <paramref name="index"/> whose name contains the team color token.
        /// Falls back to first non-null prefab in the category.
        /// </summary>
        public GameObject GetBankPrefab(int index, TeamId team)
        {
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
            GameObject bankPrefab = GetBankPrefab(index, team);
            GameObject impact = TryGetSciFiParticlePrefab(bankPrefab, "impactParticle");
            return impact != null ? impact : fallbackImpactPrefab;
        }

        /// <summary>Gameplay profile paired with VFX category index (damage multipliers, burn, etc.).</summary>
        public bool TryGetProfile(int index, out BulletBankProfile profile)
        {
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

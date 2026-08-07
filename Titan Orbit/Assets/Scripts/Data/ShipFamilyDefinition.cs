using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Baseline ability stats used when a <see cref="ShipFamilyDefinition"/> leaves
    /// <see cref="ShipFamilyDefinition.defaultFallbackStats"/> all zero. Paired with
    /// <see cref="ShipComponentAbilityStatsMath.WithZeroStatFallbacks"/> so every chassis still has
    /// playable movement, health, and weapon numbers after prefab scan.
    /// </summary>
    public static class ShipFamilyDefaultFallbackStats
    {
        /// <summary>Returns the project-wide default stat block for a generic level-1 ship.</summary>
        public static ShipComponentAbilityStats CreateBaseline()
        {
            return new ShipComponentAbilityStats
            {
                firePower = 3f,
                firePowerPerAbilityLevel = 0.75f,
                bulletSpeed = 12f,
                bulletSpeedPerAbilityLevel = 3f,
                // [TITAN-ORBIT] Matches ShipWeaponConfig.DefaultBulletMaxDistance; +25%/level like most offense stats.
                bulletRange = 30f,
                bulletRangePerAbilityLevel = 7.5f,
                fireRate = 3f,
                fireRatePerAbilityLevel = 0f,
                rammingPower = 1f,
                rammingPowerPerAbilityLevel = 0.25f,
                healthCap = 6.3f,
                healthCapPerAbilityLevel = 1.575f,
                healthRegen = 0.225f,
                healthRegenPerAbilityLevel = 0.05625f,
                energyCap = 18f,
                energyCapPerAbilityLevel = 4.5f,
                energyRegen = 3f,
                energyRegenPerAbilityLevel = 0.75f,
                moveSpeed = 9f,
                moveSpeedPerAbilityLevel = 1.8f,
                accelerationCap = 2.4f,
                accelerationCapPerAbilityLevel = 0.6f,
                turnSpeed = 14f,
                turnSpeedPerAbilityLevel = 3.5f,
                maxGems = 8f,
                maxGemsPerAbilityLevel = 2f,
                tractorBeamDistance = 3f,
                tractorBeamDistancePerAbilityLevel = 0.75f,
                tractorBeamPower = 4f,
                tractorBeamPowerPerAbilityLevel = 1f,
                maxPeople = 2f,
                maxPeoplePerAbilityLevel = 0f,
            };
        }
    }

    /// <summary>
    /// ScriptableObject (designer asset) that defines one ship family — AstroEagle, Cosmic Shark, etc.
    /// Holds per-component ability stats, the upgrade-tree chassis tiers, team materials,
    /// optional <see cref="planetaryDefense"/> turret recipe, optional
    /// <see cref="cameraFollowSettings"/> gameplay camera profile, optional
    /// <see cref="damageSmokeSettings"/> hull-damage VFX profile, and lookup helpers.
    /// Read by <see cref="ShipFamilyStatsCalculator"/>, <see cref="PlanetShipFamilyConfig"/>, and orbit-station UI
    /// to resolve prefabs, power bars, and moon-dock component prices. Does not run in ECS sim directly.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipFamily", menuName = "Titan Orbit/Ship Family Definition")]
    public class ShipFamilyDefinition : ScriptableObject
    {
        static readonly Regex CloneSuffixRegex = new Regex(@"\(Clone\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex PropulsionIdUnderscoreFormRegex = new Regex(@"^(Engine|Thruster)_(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex PropulsionIdCompactFormRegex = new Regex(@"^(Engine|Thruster)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string familyId;

        [Header("Ship Tier Growth")]
        [Tooltip(
            "Per ship level above 1, each chassis base stat grows by this fraction of its level-1 value " +
            "(0.10 = +10% of base per ship level). Applies in GetEffectiveStatsAtShipLevel. " +
            "Component *PerAbilityLevel fields are for bottom-HUD ability upgrades, not ship tier.")]
        [Min(0f)]
        public float shipLevelStatGrowthFraction = DefaultShipLevelStatGrowthFraction;

        /// <summary>Default +10% of base per ship level above 1 (family-tunable).</summary>
        public const float DefaultShipLevelStatGrowthFraction = 0.10f;

        /// <summary>
        /// Resolves this family's ship-tier growth fraction (falls back to default when unset / negative).
        /// </summary>
        public float ResolveShipLevelStatGrowthFraction() =>
            shipLevelStatGrowthFraction > 0.0001f
                ? shipLevelStatGrowthFraction
                : DefaultShipLevelStatGrowthFraction;

        [Header("Default Stat Fallbacks")]
        public ShipComponentAbilityStats defaultFallbackStats;

        [Header("Family Special Bonuses")]
        [Tooltip("Multipliers applied after component sum. Identity = all 1s. This is how families differ from shared part profiles.")]
        public ShipFamilySpecialBonuses specialBonuses = ShipFamilySpecialBonuses.Identity;

        [Header("Bullets")]
        public int bulletPrefabIndex = 0;

        [Header("Planetary Defense")]
        [Tooltip(
            "Turret recipe for planets of this family (mesh prefab, Level 1→6 combat ranges, " +
            "bullet bank). Primary authoring site — leave empty to use Resources/PlanetaryDefenseConfig. " +
            "PlanetShipFamilyConfig.ShipFamilyEntry.defenseConfig can still override per list slot.")]
        public PlanetaryDefenseConfig planetaryDefense;

        [Header("Gameplay Camera")]
        [Tooltip(
            "In-game CameraFollowEcs profile for ships of this family (height zoom, look-ahead, FOV). " +
            "Create via Assets → Create → Titan Orbit → Camera Follow Settings. " +
            "Leave empty to keep the Main Camera's default profile. " +
            "Applied when the local player's ShipFamilyConfigIndex resolves to this family " +
            "(team spawn or moon-dock ship purchase).")]
        public CameraFollowSettings cameraFollowSettings;

        [Header("Damage Smoke VFX")]
        [Tooltip(
            "Client-only hull-damage smoke profile for this family. " +
            "Create via Assets → Create → Titan Orbit → Ship Damage Smoke Settings. " +
            "Families may share one asset today; assign a unique asset later for per-family VFX. " +
            "Leave empty to fall back to Resources/ShipDamageSmokeSettings.")]
        public ShipDamageSmokeSettings damageSmokeSettings;

        [Header("Menu Preview Camera")]
        [Tooltip("Field of view for theatrical (3/4 hero) menu preview renders.")]
        [Range(20f, 55f)]
        public float menuPreviewTheatricalFieldOfView = 35f;

        [Tooltip("Bounds padding multiplier when framing menu preview cameras (top-down and theatrical).")]
        [Min(1f)]
        public float menuPreviewBoundsPadding = 1.15f;

        [Tooltip("Solid clear color behind menu preview renders (alpha usually 0 for transparent PNGs).")]
        public Color menuPreviewBackgroundColor = new Color(0f, 0f, 0f, 0f);

        [Header("Components")]
        public List<ShipFamilyComponentEntry> components = new List<ShipFamilyComponentEntry>();

        [Header("Upgrade Tree")]
        public List<ShipFamilyChassisTierEntry> upgradeTree = new List<ShipFamilyChassisTierEntry>();

        [Header("Team Materials")]
        public List<ShipFamilyTeamMaterialSet> teamMaterials = new List<ShipFamilyTeamMaterialSet>();

        [Header("Upgrade Card Deck (optional)")]
        [Tooltip("Optional authored card deck. When null, runtime uses procedural defaults.")]
        public CardDeckDefinition upgradeCardDeck;

        [Header("Mass (editor)")]
        [Tooltip("Sum of average localScale across chassis parts — used by editor mass tools.")]
        public float TotalComponentMass;

        [NonSerialized] bool _lookupBuilt;
        [NonSerialized] readonly Dictionary<string, ShipComponentAbilityStats> _lookup = new Dictionary<string, ShipComponentAbilityStats>(StringComparer.OrdinalIgnoreCase);
        [NonSerialized] List<CardData> _runtimeProceduralCards;

        /// <summary>Default hull mass scale matching legacy Starship HUD (component mass × 0.7).</summary>
        public const float DefaultHullMassScale = 0.7f;

        /// <summary>
        /// Returns authored fallback stats, or the global baseline when the asset field is all zero.
        /// </summary>
        public ShipComponentAbilityStats GetEffectiveDefaultFallbackStats()
        {
            return ShipComponentAbilityStatsMath.IsAllZero(defaultFallbackStats)
                ? ShipFamilyDefaultFallbackStats.CreateBaseline()
                : defaultFallbackStats;
        }

        /// <summary>
        /// Fills any zero fields in <paramref name="summedStats"/> from this family's effective defaults.
        /// Called after prefab stat scan so missing component entries do not zero out the hull.
        /// </summary>
        public ShipComponentAbilityStats ApplyStatFallbacks(ShipComponentAbilityStats summedStats)
        {
            return ShipComponentAbilityStatsMath.WithZeroStatFallbacks(summedStats, GetEffectiveDefaultFallbackStats());
        }

        /// <summary>Applies <see cref="specialBonuses"/> multipliers (identity when unset).</summary>
        public ShipComponentAbilityStats ApplySpecialBonuses(ShipComponentAbilityStats summedStats)
        {
            return specialBonuses.Apply(summedStats);
        }

        /// <summary>Resets <see cref="defaultFallbackStats"/> to the global baseline.</summary>
        public void ResetDefaultFallbackStatsToBaseline()
        {
            defaultFallbackStats = ShipFamilyDefaultFallbackStats.CreateBaseline();
            InvalidateComponentStatsLookup();
        }

        /// <summary>Resets family special bonuses to 1× identity.</summary>
        public void ResetSpecialBonusesToIdentity()
        {
            specialBonuses = ShipFamilySpecialBonuses.Identity;
        }

        /// <summary>
        /// Zeroes disallowed ability fields per Stat Categories × part type.
        /// Called from editor OnValidate and after Scan / Populate.
        /// </summary>
        public void EnforceComponentStatCategories()
        {
            if (components == null)
                return;

            for (int i = 0; i < components.Count; i++)
            {
                ShipFamilyComponentEntry entry = components[i];
                if (entry == null)
                    continue;
                entry.EnsureStatCategories();
                if (entry.statCategories.Count == 0)
                    entry.statCategories = ShipFamilyComponentPartKey.InferDefaultStatCategories(entry.componentId);
                entry.stats = ShipComponentAbilityStats.KeepOnlyAuthoringFields(
                    entry.stats,
                    entry.statCategories,
                    entry.componentId);
            }

            InvalidateComponentStatsLookup();
        }

        /// <summary>Component mass from a chassis prefab hierarchy (average localScale sum).</summary>
        public float ComputeComponentMassFromPrefab(GameObject prefab)
        {
            if (prefab == null)
                return 0f;
            string prefix = !string.IsNullOrWhiteSpace(familyId)
                ? familyId.Trim()
                : prefab.name;
            return ChassisComponentStats.ComputeComponentMassFromTransform(prefab.transform, prefix);
        }

        /// <summary>HUD-style hull mass at level 1 with empty cargo.</summary>
        public float ComputeHudHullMassFromPrefab(GameObject prefab) =>
            Mathf.Max(0.5f, ComputeComponentMassFromPrefab(prefab) * DefaultHullMassScale);

        /// <summary>HUD hull mass from the cached <see cref="TotalComponentMass"/>.</summary>
        public float ComputeHudHullMassFromTotal() =>
            Mathf.Max(0.5f, TotalComponentMass * DefaultHullMassScale);

        /// <summary>Recomputes <see cref="TotalComponentMass"/> from the first upgrade-tree prefab with a hull.</summary>
        public void RecalculateTotalComponentMass()
        {
            TotalComponentMass = 0f;
            if (upgradeTree == null)
                return;
            for (int i = 0; i < upgradeTree.Count; i++)
            {
                var tier = upgradeTree[i];
                if (tier?.prefab == null)
                    continue;
                TotalComponentMass = ComputeComponentMassFromPrefab(tier.prefab);
                return;
            }
        }

        /// <summary>Team-tinted hull materials for visual proxies, or null when none are configured.</summary>
        public List<Material> GetMaterialsForTeam(TeamId team)
        {
            if (teamMaterials == null || teamMaterials.Count == 0)
                return null;

            for (int i = 0; i < teamMaterials.Count; i++)
            {
                var set = teamMaterials[i];
                if (set == null || set.materials == null || set.materials.Count == 0)
                    continue;
                if (set.team == team)
                    return set.materials;
            }

            return null;
        }

        /// <summary>Authored upgrade card deck when assigned; otherwise a procedural deck for this family.</summary>
        public IReadOnlyList<CardData> GetUpgradeCards()
        {
            if (upgradeCardDeck != null && upgradeCardDeck.cards != null && upgradeCardDeck.cards.Count > 0)
                return upgradeCardDeck.cards;
            return _runtimeProceduralCards ??= CardDeckRuntimeDefaults.CreateProceduralDeck(familyId);
        }

        /// <summary>Clears the cached component-id → stats dictionary (editor OnValidate calls this).</summary>
        public void InvalidateComponentStatsLookup() => _lookupBuilt = false;

        /// <summary>
        /// Looks up authored stats for a prefab child name such as <c>Weapon_1</c> or <c>Engine_2</c>.
        /// Tries raw id, normalized id, and alternate propulsion naming (<c>Engine1</c> ↔ <c>Engine_1</c>).
        /// </summary>
        public bool TryGetStatsForComponent(string componentId, out ShipComponentAbilityStats stats)
        {
            EnsureLookup();
            stats = default;
            if (string.IsNullOrWhiteSpace(componentId))
                return false;

            string raw = componentId.Trim();
            if (_lookup.TryGetValue(raw, out stats))
                return true;
            string canonical = NormalizeComponentId(raw);
            if (!string.IsNullOrEmpty(canonical) && _lookup.TryGetValue(canonical, out stats))
                return true;
            string alternate = GetAlternateComponentIdForm(raw);
            return !string.IsNullOrEmpty(alternate) && _lookup.TryGetValue(alternate, out stats);
        }

        /// <summary>Full component row (stats, preview sprite, categories) for a component id, or false.</summary>
        public bool TryGetComponentEntry(string componentId, out ShipFamilyComponentEntry entry)
        {
            entry = null;
            if (components == null || string.IsNullOrWhiteSpace(componentId))
                return false;

            string id = componentId.Trim();
            string canonical = NormalizeComponentId(id);
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] == null) continue;
                string test = components[i].componentId?.Trim();
                if (string.Equals(test, id, StringComparison.OrdinalIgnoreCase))
                {
                    entry = components[i];
                    return true;
                }
                if (!string.IsNullOrEmpty(canonical) &&
                    string.Equals(NormalizeComponentId(test), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    entry = components[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>Menu thumbnail for a purchasable component; team variant when available.</summary>
        public Sprite GetMenuPreviewSpriteForComponent(string componentId, TeamManager.Team team = TeamManager.Team.None)
        {
            return TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry) && entry != null
                ? entry.GetMenuPreviewSprite(team)
                : null;
        }

        /// <summary>
        /// Chooses the visual chassis prefab for a home-planet level. Prefers an exact locked-in tier match,
        /// else the highest tier whose <c>minHomePlanetLevel</c> is ≤ <paramref name="shipLevel"/>.
        /// </summary>
        public bool TryGetVisualPrefabForLevel(int shipLevel, out GameObject prefab)
        {
            prefab = null;
            if (upgradeTree == null || upgradeTree.Count == 0)
                return false;

            shipLevel = Mathf.Max(1, shipLevel);

            // --- Exact locked-in tier (e.g. level-3 slot baked to a specific hull) ---
            for (int i = 0; i < upgradeTree.Count; i++)
            {
                var tier = upgradeTree[i];
                if (tier?.prefab == null)
                    continue;
                if (tier.lockedInUpgradeTree && tier.minHomePlanetLevel == shipLevel)
                {
                    prefab = tier.prefab;
                    return true;
                }
            }

            // --- Best tier at or below requested level ---
            ShipFamilyChassisTierEntry best = null;
            for (int i = 0; i < upgradeTree.Count; i++)
            {
                var tier = upgradeTree[i];
                if (tier?.prefab == null)
                    continue;
                if (tier.minHomePlanetLevel > shipLevel)
                    continue;
                if (best == null || tier.minHomePlanetLevel > best.minHomePlanetLevel)
                    best = tier;
            }

            if (best != null)
            {
                prefab = best.prefab;
                return true;
            }

            // [STANDARD] Last resort — first prefab in the tree so callers never get null when any tier exists.
            for (int i = 0; i < upgradeTree.Count; i++)
            {
                if (upgradeTree[i]?.prefab != null)
                {
                    prefab = upgradeTree[i].prefab;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Strips Unity clone suffixes and mirrored-part markers so prefab child names match authored ids.
        /// </summary>
        public static string NormalizeComponentId(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                return string.Empty;

            string s = rawId.Trim();
            s = CloneSuffixRegex.Replace(s, string.Empty);
            if (s.EndsWith("_Mirrored", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - "_Mirrored".Length);
            return s.Trim();
        }

        /// <summary>
        /// Converts between <c>Engine_1</c> and <c>Engine1</c> forms so lookups survive inconsistent naming.
        /// </summary>
        public static string GetAlternateComponentIdForm(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                return string.Empty;

            string s = NormalizeComponentId(componentId);
            Match underscored = PropulsionIdUnderscoreFormRegex.Match(s);
            if (underscored.Success)
                return underscored.Groups[1].Value + underscored.Groups[2].Value;

            Match compact = PropulsionIdCompactFormRegex.Match(s);
            if (compact.Success)
                return compact.Groups[1].Value + "_" + compact.Groups[2].Value;

            return string.Empty;
        }

        void EnsureLookup()
        {
            if (_lookupBuilt) return;
            _lookup.Clear();
            if (components != null)
            {
                foreach (var entry in components)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.componentId))
                        continue;
                    string raw = entry.componentId.Trim();
                    RegisterLookupKey(raw, entry.stats);
                    RegisterLookupKey(NormalizeComponentId(raw), entry.stats);
                    RegisterLookupKey(GetAlternateComponentIdForm(raw), entry.stats);
                }
            }

            _lookupBuilt = true;
        }

        void RegisterLookupKey(string key, ShipComponentAbilityStats stats)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            _lookup[key.Trim()] = stats;
        }

        static float s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored = -1f;

        /// <summary>
        /// Highest level-1 turn speed among upgrade-tree chassis tiers
        /// (from <see cref="ShipFamilyChassisTierEntry.powerScoreBreakdown"/>).
        /// </summary>
        public static float GetMaxUpgradeTreeTurnSpeedAuthoredUnits(IEnumerable<ShipFamilyDefinition> families)
        {
            float max = 0f;
            if (families == null)
                return max;

            foreach (ShipFamilyDefinition def in families)
            {
                if (def?.upgradeTree == null)
                    continue;

                for (int i = 0; i < def.upgradeTree.Count; i++)
                {
                    ShipFamilyChassisTierEntry tier = def.upgradeTree[i];
                    if (tier == null)
                        continue;
                    max = Mathf.Max(max, tier.powerScoreBreakdown.turnSpeed);
                }
            }

            return max;
        }

        /// <summary>
        /// Cached global max turn speed (authored units, level 1) across every loaded ship family upgrade tree.
        /// </summary>
        public static float GetGlobalMaxUpgradeTreeTurnSpeedAuthoredUnits()
        {
            if (s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored >= 0f)
                return s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored;

            ShipFamilyDefinition[] all = Resources.FindObjectsOfTypeAll<ShipFamilyDefinition>();
            s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored = GetMaxUpgradeTreeTurnSpeedAuthoredUnits(all);

            return s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored > 0f
                ? s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored
                : ShipPropulsionAggregation.VisualBankReferenceMaxTurnSpeedAuthoredUnits;
        }

        /// <summary>Clears cached global max turn speed (call after upgrade-tree stat scans in the editor).</summary>
        public static void InvalidateGlobalMaxUpgradeTreeTurnSpeedCache()
        {
            s_cachedGlobalMaxUpgradeTreeTurnSpeedAuthored = -1f;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            // Fresh serialized specialBonuses are all 0 — show 1× in the Inspector (Apply already treats 0 as 1).
            if (specialBonuses.moveSpeedMul == 0f
                && specialBonuses.accelerationMul == 0f
                && specialBonuses.firePowerMul == 0f
                && specialBonuses.maxGemsMul == 0f
                && specialBonuses.healthCapMul == 0f)
            {
                specialBonuses = ShipFamilySpecialBonuses.Identity;
            }

            EnforceComponentStatCategories();
            InvalidateComponentStatsLookup();
            InvalidateGlobalMaxUpgradeTreeTurnSpeedCache();
            _runtimeProceduralCards = null;
        }
#endif
    }

    /// <summary>One upgrade-tree slot: chassis id, prefab, unlock level, and baked power breakdown.</summary>
    [Serializable]
    public class ShipFamilyChassisTierEntry
    {
        public string chassisId;
        public string upgradeTreeShipName;
        public GameObject prefab;
        public Sprite menuPreviewSprite;
        public List<ShipFamilyTeamMenuPreview> teamMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
        public Sprite theatricalMenuPreviewSprite;
        public List<ShipFamilyTeamMenuPreview> teamTheatricalMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();
        public int minHomePlanetLevel = 1;
        public float powerScore;
        public float powerScoreAtMaxLevel;
        public ShipFamilyPowerScoreBreakdown powerScoreBreakdown;
        public float componentMass;
        public bool lockedInUpgradeTree;

        /// <summary>Top-down menu sprite, preferring a team tint when available.</summary>
        public Sprite GetMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None)
        {
            if (team != TeamManager.Team.None && teamMenuPreviewSprites != null)
            {
                for (int i = 0; i < teamMenuPreviewSprites.Count; i++)
                {
                    var entry = teamMenuPreviewSprites[i];
                    if (entry != null && entry.team == team && entry.sprite != null)
                        return entry.sprite;
                }
            }

            return menuPreviewSprite;
        }

        /// <summary>Theatrical menu sprite, preferring a team tint when available.</summary>
        public Sprite GetTheatricalMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None)
        {
            if (team != TeamManager.Team.None && teamTheatricalMenuPreviewSprites != null)
            {
                for (int i = 0; i < teamTheatricalMenuPreviewSprites.Count; i++)
                {
                    var entry = teamTheatricalMenuPreviewSprites[i];
                    if (entry != null && entry.team == team && entry.sprite != null)
                        return entry.sprite;
                }
            }

            return theatricalMenuPreviewSprite != null ? theatricalMenuPreviewSprite : GetMenuPreviewSprite(team);
        }
    }

    /// <summary>Team-specific menu sprite override for a chassis tier.</summary>
    [Serializable]
    public class ShipFamilyTeamMenuPreview
    {
        public string variantName;
        public TeamManager.Team team = TeamManager.Team.None;
        public Sprite sprite;
    }

    /// <summary>Material list applied to hull proxies for one team color variant.</summary>
    [Serializable]
    public class ShipFamilyTeamMaterialSet
    {
        public string variantName;
        public TeamId team = TeamId.None;
        public List<Material> materials = new List<Material>();
    }
}

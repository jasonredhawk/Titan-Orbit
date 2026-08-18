using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One MEGA hull in the match-wide pool. Index is stable for the life of the asset so
    /// ghosted <c>PlanetMegaShipSlotElement.CatalogIndex</c> can resolve the same prefab on
    /// every client. Chassis id is <c>MEGA_{index:000}</c> (e.g. MEGA_007).
    /// </summary>
    [Serializable]
    public class MegaShipCatalogEntry
    {
        /// <summary>0-based index into <see cref="MegaShipCatalog.entries"/>.</summary>
        public ushort catalogIndex;

        /// <summary>Visual line this hull was drawn from (drives L7 branch 0/1/2).</summary>
        public MegaShipVisualFamily visualFamily;

        /// <summary>Orbit-menu label (prefab name when empty).</summary>
        public string displayName;

        /// <summary>Visual + mount chassis. Not a NetCode ghost — StarshipGhost stays the only ship ghost.</summary>
        public GameObject prefab;

        /// <summary>
        /// Theatrical 3/4 hero thumbnail for the Orbit Menu ship upgrade tree.
        /// TeamA / first fallback when <see cref="teamMenuPreviewSprites"/> has no match.
        /// </summary>
        public Sprite menuPreviewSprite;

        /// <summary>
        /// Per-team theatrical thumbs (Team A–E). Same type as regular family chassis previews.
        /// </summary>
        public List<ShipFamilyTeamMenuPreview> teamMenuPreviewSprites = new List<ShipFamilyTeamMenuPreview>();

        /// <summary>Theatrical menu sprite, preferring a team tint when available.</summary>
        public Sprite GetMenuPreviewSprite(TeamManager.Team team = TeamManager.Team.None)
        {
            if (team != TeamManager.Team.None && teamMenuPreviewSprites != null)
            {
                for (int i = 0; i < teamMenuPreviewSprites.Count; i++)
                {
                    var row = teamMenuPreviewSprites[i];
                    if (row != null && row.team == team && row.sprite != null)
                        return row.sprite;
                }
            }

            return menuPreviewSprite;
        }

        /// <summary>
        /// How many times each unique part name appears on this hull. Stats live on
        /// <see cref="MegaShipCatalog.uniqueComponents"/> — this list has no numbers.
        /// </summary>
        [Tooltip("Unique part names on this hull and how many copies. Edit stats on the catalog Unique Components list.")]
        public List<MegaShipComponentCount> componentCounts = new List<MegaShipComponentCount>();

        /// <summary>
        /// Sum of unique-component stats × counts on this prefab. Written by refresh.
        /// </summary>
        [Tooltip("Sum of unique component stats × how many times each name appears. Rewritten by Refresh.")]
        public MegaShipPartStats summedStats;

        /// <summary>
        /// True when a non-firepower summed stat is still 0. Written by refresh for
        /// tooling; the inspector hides this and uses row color instead.
        /// </summary>
        [HideInInspector]
        public bool hasMissingStats;
    }

    /// <summary>
    /// Shared MEGA component-stat table plus the 90-hull pool.
    /// Regular families (AstroEagle, …) do not own these numbers — MEGAs are static
    /// (<see cref="MegaShipPartStats"/> has no Extra Level fields) and are not bottom-bar upgradable.
    /// <para>
    /// [UNITY] Loaded from <c>Resources/MegaShipCatalog</c>. Editor menu
    /// <c>Titan Orbit / MEGA Ships / Rebuild Catalog From Folders</c> fills the hull list.
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "MegaShipCatalog", menuName = "Titan Orbit/MEGA Ship Catalog")]
    public class MegaShipCatalog : ScriptableObject
    {
        /// <summary>Resources path used by <see cref="Load"/>.</summary>
        public const string ResourcesPath = "MegaShipCatalog";

        /// <summary>Chassis-id prefix so MEGA ids never collide with <c>AstroEagle_01</c>.</summary>
        public const string ChassisIdPrefix = "MEGA_";

        /// <summary>Contributed-gem cost to buy any MEGA (gem cap is 0 so 2×cap cannot price them).</summary>
        public const float DefaultPurchaseGemCost = 1200f;

        /// <summary>
        /// Hard cap on MEGA bullet travel (and intercept-lead extension).
        /// Shorter live rounds cut BulletSimulationSystem + tracer cost on volleys.
        /// </summary>
        public const float MaxBulletTravelDistance = 28f;

        /// <summary>Default acquire + travel range for rapid MEGA guns (world units). Written by Apply Default Type-Table Stats.</summary>
        public const float DefaultBulletAcquireRange = 20f;

        /// <summary>Default acquire + travel range for MEGA cannons (world units).</summary>
        public const float DefaultCannonAcquireRange = 24f;

        /// <summary>Default acquire + travel range for MEGA missile launchers (world units).</summary>
        public const float DefaultMissileAcquireRange = 22f;

        /// <summary>Default acquire + travel range for MEGA snipers (world units).</summary>
        public const float DefaultSniperAcquireRange = 28f;

        /// <summary>Minimum cruise speed after summing a hull (thruster-only prefabs).</summary>
        public const float MinHullMoveSpeed = 12f;

        /// <summary>Minimum acceleration after summing a hull.</summary>
        public const float MinHullAcceleration = 8f;

        /// <summary>Minimum health after summing a hull so a MEGA is never a glass cannon.</summary>
        public const float MinHullHealth = 800f;

        /// <summary>Minimum energy cap — several multi-gun salvos, not a 3000 tank.</summary>
        public const float MinHullEnergy = 800f;

        /// <summary>Default energy cap when the catalog sum is still 0.</summary>
        public const float DefaultHullEnergy = 1400f;

        /// <summary>Hard cap so stale catalog sums cannot return to ~3000 energy.</summary>
        public const float MaxHullEnergy = 2200f;

        /// <summary>Minimum energy regen after resolve.</summary>
        public const float MinHullEnergyRegen = 22f;

        /// <summary>Default energy regen when the catalog sum is still 0.</summary>
        public const float DefaultHullEnergyRegen = 36f;

        /// <summary>Hard cap on regen — full volley still drains, but the bar recovers between bursts.</summary>
        public const float MaxHullEnergyRegen = 50f;

        /// <summary>Floor on MEGA PhysicsMass / ramming mass so rocks cannot shove the hull.</summary>
        public const float MinHullCollisionMass = 160f;

        /// <summary>Authored motor.Mass for MEGAs (SkipMassTax still keeps cruise accel).</summary>
        public const float DefaultHullCollisionMass = 220f;

        /// <summary>Asteroid bounce coefficient for MEGAs — grind, do not rebound.</summary>
        public const float AsteroidBounceRestitution = 0.06f;

        /// <summary>
        /// Bump when MEGA hull collider bake changes so already-spawned hulls rebuild once.
        /// </summary>
        public const int HullColliderRevision = 4;

        /// <summary>Minimum troop cap after resolve.</summary>
        public const float MinHullPeople = 400f;

        /// <summary>Default troop cap when the catalog sum is still 0.</summary>
        public const float DefaultHullPeople = 600f;

        /// <summary>Default extra world radius around a MEGA when framing the gameplay camera.</summary>
        public const float DefaultCameraHullViewPadding = 8f;

        /// <summary>Hard cap on MEGA camera height so tracers stay readable.</summary>
        public const float DefaultCameraMaxHeight = 90f;

        /// <summary>Default extra propulsion cruise contribution (2% of every engine/thruster past the fastest).</summary>
        public const float DefaultExtraEngineSpeedPercent = 0.02f;

        /// <summary>Default MEGA turret traverse when a weapon row leaves weaponRotationSpeed at 0.</summary>
        public const float DefaultWeaponRotationSpeed = 90f;

        /// <summary>Floor on MEGA turret traverse after defaults (degrees/sec).</summary>
        public const float MinWeaponRotationSpeed = 25f;

        /// <summary>Default thruster/engine jet scale boost so MEGA VFX stays visible at globalScale 0.2.</summary>
        public const float DefaultThrusterVfxScale = 5f;

        /// <summary>
        /// Whole-hull multiplier vs the previous MEGA size (tier-7 transform scale).
        /// 0.2 is about 5× smaller.
        /// </summary>
        public const float DefaultGlobalScale = 0.2f;

        static MegaShipCatalog s_instance;

        [Header("Unique components")]
        [Tooltip("One row per part name across all MEGA prefabs (Armor1, TurretBarrel, …). Edit here; every ship that uses the name picks it up on Refresh.")]
        public List<MegaShipComponentEntry> uniqueComponents = new List<MegaShipComponentEntry>();

        [Header("Hull pool")]
        [Tooltip("All MEGA prefabs. CatalogIndex must match list index after a rebuild.")]
        public List<MegaShipCatalogEntry> entries = new List<MegaShipCatalogEntry>();

        [Header("Economy")]
        [Tooltip("Contributed gems to purchase a level-7 MEGA. Server and UI must agree.")]
        public float purchaseGemCost = DefaultPurchaseGemCost;

        [Header("Presentation")]
        [Tooltip("Whole-hull scale for every MEGA. 1 = previous size, 0.2 ≈ 5× smaller. Applied to LocalTransform.Scale (visuals, colliders, gun pads).")]
        [Min(0.05f)]
        public float globalScale = DefaultGlobalScale;

        [Tooltip("Cruise speed = fastest engine or thruster + this fraction of every other propulsion part's moveSpeed. Default 0.02 (2%).")]
        [Range(0f, 1f)]
        public float extraEngineSpeedPercent = DefaultExtraEngineSpeedPercent;

        [Tooltip("Optional 5-team material sets for theatrical menu previews. Empty = reuse regular family teamMaterials (same in-game MEGA tint).")]
        public List<ShipFamilyTeamMaterialSet> teamMaterials = new List<ShipFamilyTeamMaterialSet>();

        [Tooltip("Jet VFX local scale multiplier on MEGA hulls. Compensates for globalScale (0.2 → use ~5).")]
        [Min(0.25f)]
        public float thrusterVfxScale = DefaultThrusterVfxScale;

        [Tooltip("Field of view for theatrical (3/4 hero) orbit-menu preview renders.")]
        [Range(20f, 55f)]
        public float menuPreviewTheatricalFieldOfView = 35f;

        [Tooltip("Bounds padding when framing the theatrical menu preview camera.")]
        [Min(1f)]
        public float menuPreviewBoundsPadding = 1.15f;

        [Tooltip("Clear color behind theatrical menu preview PNGs. Default is opaque black.")]
        public Color menuPreviewBackgroundColor = Color.black;

        [Header("Shared MEGA part stats (static — no Extra Level)")]
        [Tooltip("Rapid guns / turret barrels.")]
        public MegaShipPartStats weaponBulletStats;

        [Tooltip("Turret cannons — slower, heavier than guns.")]
        public MegaShipPartStats weaponCannonStats;

        [Tooltip("Missile / rocket launchers.")]
        public MegaShipPartStats weaponMissileStats;

        [Tooltip("Sniper — high damage, high bullet speed, very slow fire rate.")]
        public MegaShipPartStats weaponSniperStats;

        [Tooltip("Cockpit / bridge — troop cap lives here.")]
        public MegaShipPartStats cockpitStats;

        [Tooltip("Wings — extra troops, no gems.")]
        public MegaShipPartStats wingStats;

        [Tooltip("Engines — slow cruise + energy pool.")]
        public MegaShipPartStats engineStats;

        [Tooltip("Thrusters — slow turn.")]
        public MegaShipPartStats thrusterStats;

        [Tooltip("Tail / fins — extra turn.")]
        public MegaShipPartStats tailStats;

        [Tooltip("Armor / body / leftover parts.")]
        public MegaShipPartStats hullStats;

        [Header("In-game default stats")]
        [Tooltip("Used in-game when a hull's summed stat is 0. Firepower stays 0. Catalog sums keep the raw 0.")]
        public MegaShipPartStats runtimeDefaultStats;

        [Header("In-game minimum stats")]
        [Tooltip("Floor applied in-game after defaults. Non-zero sums below this are raised. Firepower is never floored.")]
        public MegaShipPartStats runtimeMinimumStats;

        [Header("Camera")]
        [Tooltip("Optional taller follow profile while flying a MEGA. Empty = keep family camera.")]
        public CameraFollowSettings cameraFollowSettings;

        [Tooltip("Extra world radius around a MEGA hull when framing the gameplay camera.")]
        [Min(0f)]
        public float cameraHullViewPadding = DefaultCameraHullViewPadding;

        [Tooltip("Hard cap on MEGA camera height so tracers stay readable.")]
        [Min(20f)]
        public float cameraMaxHeight = DefaultCameraMaxHeight;

        /// <summary>Loads <c>Resources/MegaShipCatalog</c> once per session.</summary>
        public static MegaShipCatalog Load()
        {
            if (s_instance == null)
                s_instance = Resources.Load<MegaShipCatalog>(ResourcesPath);
            return s_instance;
        }

        /// <summary>Clears the cached instance after editor rebuilds.</summary>
        public static void InvalidateCache() => s_instance = null;

        /// <summary>Formats the stable chassis id for a catalog index (<c>MEGA_007</c>).</summary>
        public static string FormatChassisId(int catalogIndex)
        {
            return ChassisIdPrefix + catalogIndex.ToString("000");
        }

        /// <summary>True when <paramref name="chassisId"/> is a MEGA hull id.</summary>
        public static bool IsMegaChassisId(string chassisId)
        {
            return !string.IsNullOrEmpty(chassisId)
                   && chassisId.StartsWith(ChassisIdPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Parses <c>MEGA_007</c> into catalog index 7.</summary>
        public static bool TryParseCatalogIndex(string chassisId, out ushort catalogIndex)
        {
            catalogIndex = 0;
            if (!IsMegaChassisId(chassisId))
                return false;

            string tail = chassisId.Substring(ChassisIdPrefix.Length);
            if (!int.TryParse(tail, out int parsed) || parsed < 0 || parsed > ushort.MaxValue)
                return false;

            catalogIndex = (ushort)parsed;
            return true;
        }

        /// <summary>Looks up a hull by catalog index.</summary>
        public bool TryGetEntry(int catalogIndex, out MegaShipCatalogEntry entry)
        {
            entry = null;
            if (entries == null || catalogIndex < 0 || catalogIndex >= entries.Count)
                return false;

            entry = entries[catalogIndex];
            return entry != null;
        }

        /// <summary>Looks up a hull by <c>MEGA_###</c> chassis id.</summary>
        public bool TryGetEntryByChassisId(string chassisId, out MegaShipCatalogEntry entry)
        {
            entry = null;
            if (!TryParseCatalogIndex(chassisId, out ushort index))
                return false;
            return TryGetEntry(index, out entry);
        }

        /// <summary>Prefab for a MEGA chassis id, or null.</summary>
        public GameObject GetPrefabByChassisId(string chassisId)
        {
            return TryGetEntryByChassisId(chassisId, out MegaShipCatalogEntry entry) ? entry.prefab : null;
        }

        /// <summary>Display name for tree UI (authored name, else prefab name, else chassis id).</summary>
        public string GetDisplayName(int catalogIndex)
        {
            if (!TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry) || entry == null)
                return FormatChassisId(catalogIndex);

            if (!string.IsNullOrWhiteSpace(entry.displayName))
                return entry.displayName.Trim();

            if (entry.prefab != null)
                return entry.prefab.name;

            return FormatChassisId(catalogIndex);
        }

        /// <summary>Part-profile stats for MEGA summing. Unknown types use hull.</summary>
        public MegaShipPartStats GetStatsForPartType(string partType)
        {
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponSniper, StringComparison.OrdinalIgnoreCase))
                return weaponSniperStats;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponMissile, StringComparison.OrdinalIgnoreCase))
                return weaponMissileStats;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponCannon, StringComparison.OrdinalIgnoreCase))
                return weaponCannonStats;
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponBullet, StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "Weapon", StringComparison.OrdinalIgnoreCase))
                return weaponBulletStats;
            if (string.Equals(partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase))
                return cockpitStats;
            if (string.Equals(partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
                return wingStats;
            if (string.Equals(partType, ShipFamilyPartTypes.Engine, StringComparison.OrdinalIgnoreCase))
                return engineStats;
            if (string.Equals(partType, ShipFamilyPartTypes.Thruster, StringComparison.OrdinalIgnoreCase))
                return thrusterStats;
            if (string.Equals(partType, ShipFamilyPartTypes.Tail, StringComparison.OrdinalIgnoreCase))
                return tailStats;
            return hullStats;
        }

        /// <summary>Looks up a unique part by prefab child name (Armor1, TurretBarrel, …).</summary>
        public bool TryGetUniqueComponent(string displayName, out MegaShipComponentEntry entry)
        {
            entry = null;
            if (uniqueComponents == null || string.IsNullOrEmpty(displayName))
                return false;

            for (int i = 0; i < uniqueComponents.Count; i++)
            {
                var row = uniqueComponents[i];
                if (row == null || string.IsNullOrEmpty(row.displayName))
                    continue;
                if (!string.Equals(row.displayName, displayName, StringComparison.OrdinalIgnoreCase))
                    continue;
                entry = row;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Static power-bar breakdown for a MEGA hull: sum the catalogued component stats
        /// (or walk the prefab with the type table if the list is empty), then force gem cap to 0.
        /// </summary>
        public ShipFamilyPowerScoreBreakdown GetPowerBreakdown(int catalogIndex)
        {
            if (!TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry) || entry == null)
                return default;

            MegaShipStatsCalculator.SumFromEntry(entry, this, out ShipComponentAbilityStats summed);
            summed.maxGems = 0f;
            return ShipFamilyPowerScoreBreakdown.FromSummedShipStats(summed);
        }

        /// <summary>Purchase cost (catalog field, fallback 1200).</summary>
        public float GetPurchaseGemCost()
        {
            return purchaseGemCost > 0.01f ? purchaseGemCost : DefaultPurchaseGemCost;
        }

        /// <summary>
        /// Whole-hull MEGA scale. Existing assets that never serialized this field
        /// (0) fall back to <see cref="DefaultGlobalScale"/>.
        /// </summary>
        public float GetGlobalScale()
        {
            return globalScale > 0.001f ? globalScale : DefaultGlobalScale;
        }

        /// <summary>Extra propulsion cruise fraction (0.02 = 2% of every engine/thruster past the fastest).</summary>
        public float GetExtraEngineSpeedPercent()
        {
            return extraEngineSpeedPercent > 0f ? extraEngineSpeedPercent : DefaultExtraEngineSpeedPercent;
        }

        /// <summary>Theatrical menu sprite for a hull, preferring a team tint when available.</summary>
        public Sprite GetMenuPreviewSprite(int catalogIndex, TeamManager.Team team = TeamManager.Team.None)
        {
            return TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry) && entry != null
                ? entry.GetMenuPreviewSprite(team)
                : null;
        }

        /// <summary>MEGA jet VFX scale boost (default 5).</summary>
        public float GetThrusterVfxScale()
        {
            return thrusterVfxScale > 0.01f ? thrusterVfxScale : DefaultThrusterVfxScale;
        }

        /// <summary>Extra radius around a MEGA hull for gameplay camera framing.</summary>
        public float GetCameraHullViewPadding()
        {
            return cameraHullViewPadding > 0f ? cameraHullViewPadding : DefaultCameraHullViewPadding;
        }

        /// <summary>Max gameplay camera height while flying a MEGA.</summary>
        public float GetCameraMaxHeight()
        {
            return cameraMaxHeight > 1f ? cameraMaxHeight : DefaultCameraMaxHeight;
        }

        /// <summary>
        /// In-game stats from a raw catalog sum: zeros become listed defaults, then
        /// non-firepower values are raised to listed minimums. Firepower may stay 0.
        /// </summary>
        public MegaShipPartStats ResolveRuntimeStats(in MegaShipPartStats raw)
        {
            MegaShipPartStats defaults = IsUnsetRuntimeBlock(runtimeDefaultStats)
                ? CreateBuiltInRuntimeDefaults()
                : runtimeDefaultStats;
            MegaShipPartStats mins = IsUnsetRuntimeBlock(runtimeMinimumStats)
                ? CreateBuiltInRuntimeMinimums()
                : runtimeMinimumStats;
            // Existing catalog assets may have seeded health/move defaults with traverse still 0.
            if (defaults.maxPeople < DefaultHullPeople)
                defaults.maxPeople = DefaultHullPeople;
            if (mins.maxPeople < MinHullPeople)
                mins.maxPeople = MinHullPeople;
            if (defaults.energyCap < DefaultHullEnergy)
                defaults.energyCap = DefaultHullEnergy;
            if (defaults.energyRegen < DefaultHullEnergyRegen)
                defaults.energyRegen = DefaultHullEnergyRegen;
            if (mins.energyCap < MinHullEnergy)
                mins.energyCap = MinHullEnergy;
            if (mins.energyRegen < MinHullEnergyRegen)
                mins.energyRegen = MinHullEnergyRegen;
            return MegaShipPartStats.ApplyRuntimeDefaultsAndMinimums(raw, defaults, mins);
        }

        static bool IsUnsetRuntimeBlock(in MegaShipPartStats s)
        {
            return s.healthCap <= 0.01f
                   && s.moveSpeed <= 0.01f
                   && s.accelerationCap <= 0.01f
                   && s.turnSpeed <= 0.01f;
        }

        static MegaShipPartStats CreateBuiltInRuntimeDefaults()
        {
            return CreateStatic(
                firePower: 0f, bulletSpeed: 12f, bulletRange: DefaultBulletAcquireRange, fireRate: 1f, ramming: 8f,
                health: MinHullHealth, healthRegen: 2f, energy: DefaultHullEnergy, energyRegen: DefaultHullEnergyRegen,
                move: MinHullMoveSpeed, accel: MinHullAcceleration, turn: 4f, gems: 0f, people: DefaultHullPeople,
                weaponRotationSpeed: DefaultWeaponRotationSpeed);
        }

        static MegaShipPartStats CreateBuiltInRuntimeMinimums()
        {
            return CreateStatic(
                firePower: 0f, bulletSpeed: 8f, bulletRange: 10f, fireRate: 0.25f, ramming: 2f,
                health: MinHullHealth, healthRegen: 0.5f, energy: MinHullEnergy, energyRegen: MinHullEnergyRegen,
                move: MinHullMoveSpeed, accel: MinHullAcceleration, turn: 3f, gems: 0f, people: MinHullPeople,
                weaponRotationSpeed: MinWeaponRotationSpeed);
        }

        /// <summary>
        /// True when this hull may enter a match (slots, purchase, spawn).
        /// Armed = raw <see cref="MegaShipCatalogEntry.summedStats"/> firepower &gt; 0.01,
        /// or a unique-component weapon still has firepower when the stored sum is stale/zero.
        /// Does not use <see cref="ResolveRuntimeStats"/> — firepower is never defaulted from 0.
        /// </summary>
        public bool IsEligibleForMatch(int catalogIndex)
        {
            return TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry) && IsEligibleForMatch(entry);
        }

        /// <summary>
        /// Same gate as <see cref="IsEligibleForMatch(int)"/> for an already-resolved hull.
        /// Null entries are never playable. Unarmed hulls stay in <see cref="entries"/> for editors.
        /// </summary>
        public bool IsEligibleForMatch(MegaShipCatalogEntry entry)
        {
            if (entry == null)
                return false;

            // --- Primary: catalog sum ---
            // [TITAN-ORBIT] summedStats.firePower is the raw unique-component total. Runtime
            // defaults never fill firepower, so a stored 0 really means "no guns yet."
            if (entry.summedStats.firePower > 0.01f)
                return true;

            // --- Fallback: stale sum, live unique rows ---
            // Refresh can lag a hand-edit on uniqueComponents. If any counted part still
            // has firepower, treat the hull as armed so a designer tweak is not ignored.
            return HasArmedUniqueComponentFirepower(entry);
        }

        /// <summary>
        /// Walks this hull's <see cref="MegaShipCatalogEntry.componentCounts"/> against
        /// <see cref="uniqueComponents"/>. True when any counted part has firepower &gt; 0.01.
        /// </summary>
        bool HasArmedUniqueComponentFirepower(MegaShipCatalogEntry entry)
        {
            if (entry.componentCounts == null || uniqueComponents == null)
                return false;

            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry row) || row == null)
                    continue;
                if (row.stats.firePower > 0.01f)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Every non-null catalog index — editor rebuild, unique-component refresh,
        /// theatrical previews, and visual-catalog bake. Match start uses
        /// <see cref="CollectMatchIndices"/> so unarmed hulls are not rolled in.
        /// </summary>
        public void CollectAllIndices(List<ushort> into)
        {
            into.Clear();
            if (entries == null)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] == null)
                    continue;
                into.Add((ushort)i);
            }
        }

        /// <summary>
        /// Armed hulls only (firepower &gt; 0). Used by match roll, purchase, and debug spawn.
        /// Empty result means leave planet slots unassigned — do not fall back to unarmed hulls.
        /// </summary>
        public void CollectMatchIndices(List<ushort> into)
        {
            into.Clear();
            if (entries == null)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                if (!IsEligibleForMatch(entries[i]))
                    continue;
                into.Add((ushort)i);
            }
        }

        /// <summary>Collects catalog indices for one visual family (editor / debug).</summary>
        public void CollectIndicesForVisualFamily(MegaShipVisualFamily family, List<ushort> into)
        {
            into.Clear();
            if (entries == null)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null || e.visualFamily != family)
                    continue;
                into.Add((ushort)i);
            }
        }

        /// <summary>
        /// Fills designer-default static MEGA stats (slow, tanky, no gems, high people)
        /// and seeds <see cref="runtimeDefaultStats"/> / <see cref="runtimeMinimumStats"/>.
        /// After clicking this in the inspector, run Refresh Unique Components so stored
        /// hull <c>summedStats</c> stay raw (zeros are not baked in).
        /// Called by the editor rebuild when the asset is first created.
        /// </summary>
        public void ApplyDefaultStaticStats()
        {
            purchaseGemCost = DefaultPurchaseGemCost;
            if (globalScale <= 0.001f)
                globalScale = DefaultGlobalScale;
            if (extraEngineSpeedPercent <= 0f)
                extraEngineSpeedPercent = DefaultExtraEngineSpeedPercent;
            if (thrusterVfxScale <= 0.01f)
                thrusterVfxScale = DefaultThrusterVfxScale;

            weaponBulletStats = CreateStatic(
                firePower: 6f, bulletSpeed: 14f, bulletRange: DefaultBulletAcquireRange, fireRate: 2f, ramming: 0f,
                health: 0f, healthRegen: 0f, energy: 4f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 0f, gems: 0f, people: 0f,
                weaponRotationSpeed: DefaultWeaponRotationSpeed);

            weaponCannonStats = CreateStatic(
                firePower: 14f, bulletSpeed: 12f, bulletRange: DefaultCannonAcquireRange, fireRate: 0.7f, ramming: 0f,
                health: 0f, healthRegen: 0f, energy: 6f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 0f, gems: 0f, people: 0f,
                weaponRotationSpeed: DefaultWeaponRotationSpeed);

            weaponMissileStats = CreateStatic(
                firePower: 18f, bulletSpeed: 10f, bulletRange: DefaultMissileAcquireRange, fireRate: 0.45f, ramming: 0f,
                health: 0f, healthRegen: 0f, energy: 8f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 0f, gems: 0f, people: 0f,
                weaponRotationSpeed: DefaultWeaponRotationSpeed);

            weaponSniperStats = CreateStatic(
                firePower: 32f, bulletSpeed: 28f, bulletRange: DefaultSniperAcquireRange, fireRate: 0.22f, ramming: 0f,
                health: 0f, healthRegen: 0f, energy: 10f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 0f, gems: 0f, people: 0f,
                weaponRotationSpeed: DefaultWeaponRotationSpeed);

            cockpitStats = CreateStatic(
                firePower: 0f, bulletSpeed: 0f, bulletRange: 0f, fireRate: 0f, ramming: 4f,
                health: 220f, healthRegen: 3.5f, energy: 600f, energyRegen: 18f,
                move: 0f, accel: 0f, turn: 0f, gems: 0f, people: 400f);

            wingStats = CreateStatic(
                firePower: 0f, bulletSpeed: 0f, bulletRange: 0f, fireRate: 0f, ramming: 0f,
                health: 36f, healthRegen: 0.4f, energy: 0f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 0.4f, gems: 0f, people: 50f);

            engineStats = CreateStatic(
                firePower: 0f, bulletSpeed: 0f, bulletRange: 0f, fireRate: 0f, ramming: 0f,
                health: 40f, healthRegen: 0.3f, energy: 140f, energyRegen: 5f,
                move: 12f, accel: 8f, turn: 0f, gems: 0f, people: 0f);

            thrusterStats = CreateStatic(
                firePower: 0f, bulletSpeed: 0f, bulletRange: 0f, fireRate: 0f, ramming: 0f,
                health: 12f, healthRegen: 0.1f, energy: 0f, energyRegen: 0f,
                move: 5f, accel: 3.5f, turn: 2.2f, gems: 0f, people: 0f);

            tailStats = CreateStatic(
                firePower: 0f, bulletSpeed: 0f, bulletRange: 0f, fireRate: 0f, ramming: 0f,
                health: 16f, healthRegen: 0.1f, energy: 0f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 1.8f, gems: 0f, people: 0f);

            hullStats = CreateStatic(
                firePower: 0f, bulletSpeed: 0f, bulletRange: 0f, fireRate: 0f, ramming: 1.2f,
                health: 32f, healthRegen: 0.45f, energy: 0f, energyRegen: 0f,
                move: 0f, accel: 0f, turn: 0f, gems: 0f, people: 0f);

            runtimeDefaultStats = CreateBuiltInRuntimeDefaults();
            runtimeMinimumStats = CreateBuiltInRuntimeMinimums();
            ApplyTypeTableBulletRangesToUniqueWeapons();
            ApplyTypeTableVitalsToUniqueParts();
            MegaShipComponentInventory.RecalcAllShipSums(this);
            if (cameraHullViewPadding <= 0f)
                cameraHullViewPadding = DefaultCameraHullViewPadding;
            if (cameraMaxHeight <= 1f)
                cameraMaxHeight = DefaultCameraMaxHeight;
        }

        /// <summary>
        /// Writes type-table <c>bulletRange</c> onto unique weapon rows so Apply Default + Refresh
        /// updates the authored catalog (no runtime 2× multiplier). Other unique-row stats stay.
        /// </summary>
        void ApplyTypeTableBulletRangesToUniqueWeapons()
        {
            if (uniqueComponents == null)
                return;

            for (int i = 0; i < uniqueComponents.Count; i++)
            {
                MegaShipComponentEntry row = uniqueComponents[i];
                if (row == null)
                    continue;
                if (!row.isWeapon && !ShipFamilyPartTypes.IsWeapon(row.partType))
                    continue;

                MegaShipPartStats table = GetStatsForPartType(row.partType);
                if (table.bulletRange <= 0.5f)
                    continue;

                MegaShipPartStats stats = row.stats;
                stats.bulletRange = table.bulletRange;
                row.stats = stats;
            }
        }

        /// <summary>
        /// Writes type-table energy / people onto cockpit, engine, and wing unique rows.
        /// </summary>
        void ApplyTypeTableVitalsToUniqueParts()
        {
            if (uniqueComponents == null)
                return;

            for (int i = 0; i < uniqueComponents.Count; i++)
            {
                MegaShipComponentEntry row = uniqueComponents[i];
                if (row == null)
                    continue;
                if (!string.Equals(row.partType, ShipFamilyPartTypes.Cockpit, StringComparison.OrdinalIgnoreCase)
                    && !ShipFamilyPartTypes.IsEngineProfile(row.partType)
                    && !string.Equals(row.partType, ShipFamilyPartTypes.Wing, StringComparison.OrdinalIgnoreCase))
                    continue;

                MegaShipPartStats table = GetStatsForPartType(row.partType);
                MegaShipPartStats stats = row.stats;
                if (table.energyCap > 0.5f)
                    stats.energyCap = table.energyCap;
                if (table.energyRegen > 0.01f)
                    stats.energyRegen = table.energyRegen;
                if (table.maxPeople > 0.5f)
                    stats.maxPeople = table.maxPeople;
                row.stats = stats;
            }
        }

        static MegaShipPartStats CreateStatic(
            float firePower, float bulletSpeed, float bulletRange, float fireRate, float ramming,
            float health, float healthRegen, float energy, float energyRegen,
            float move, float accel, float turn, float gems, float people,
            float weaponRotationSpeed = 0f)
        {
            _ = gems;
            return new MegaShipPartStats
            {
                firePower = firePower,
                bulletSpeed = bulletSpeed,
                bulletRange = bulletRange,
                fireRate = fireRate,
                rammingPower = ramming,
                healthCap = health,
                healthRegen = healthRegen,
                energyCap = energy,
                energyRegen = energyRegen,
                moveSpeed = move,
                accelerationCap = accel,
                turnSpeed = turn,
                weaponRotationSpeed = weaponRotationSpeed,
                maxPeople = people,
            };
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (weaponBulletStats.firePower <= 0.01f && hullStats.healthCap <= 0.01f)
                ApplyDefaultStaticStats();
        }
#endif
    }
}

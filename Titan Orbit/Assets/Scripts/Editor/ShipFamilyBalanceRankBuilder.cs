#if UNITY_EDITOR
using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Scans every family upgrade tree and the MEGA catalog, then writes
    /// a <see cref="ShipFamilyBalanceRankCatalog"/> snapshot.
    /// <para>
    /// Regular hulls use the same Extra Level + all-gun DPS path as the Orbit
    /// Menu power bar (<see cref="ShipFamilyPowerBarNorm.TryBuildBreakdown"/>).
    /// MEGAs use <see cref="MegaShipCatalog.ComputeSustainedDps"/> plus each
    /// weapon type's <see cref="BulletVfxBank"/> Fire Power / Rate multipliers.
    /// Combat never calls this — it is only the Refresh Snapshot button.
    /// </para>
    /// Menu: TitanOrbit → Ship Families → Open Balance Rank Catalog.
    /// </summary>
    public static class ShipFamilyBalanceRankBuilder
    {
        /// <summary>
        /// Unity project path for the shared worksheet asset (next to
        /// <c>ShipFamilyDefinitionCatalog</c>).
        /// </summary>
        public const string CatalogAssetPath =
            "Assets/Prefabs/Ships/ShipFamilyBalanceRankCatalog.asset";

        const string FamilySearchFolder = "Assets/Prefabs/Ships";

        /// <summary>
        /// Loads the catalog at <see cref="CatalogAssetPath"/>, or creates it.
        /// Does not refresh numbers — call <see cref="RefreshSnapshot"/> after.
        /// </summary>
        /// <returns>The worksheet asset. Never null after a successful create.</returns>
        public static ShipFamilyBalanceRankCatalog FindOrCreateCatalog()
        {
            // --- Find or create ---
            // [UNITY] AssetDatabase paths are relative to the Unity project root
            // (the Titan Orbit folder), not the git repo root.
            var existing = AssetDatabase.LoadAssetAtPath<ShipFamilyBalanceRankCatalog>(CatalogAssetPath);
            if (existing != null)
                return existing;

            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyBalanceRankCatalog");
            if (guids != null && guids.Length > 0)
            {
                string foundPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                var found = AssetDatabase.LoadAssetAtPath<ShipFamilyBalanceRankCatalog>(foundPath);
                if (found != null)
                    return found;
            }

            var created = ScriptableObject.CreateInstance<ShipFamilyBalanceRankCatalog>();
            AssetDatabase.CreateAsset(created, CatalogAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ShipFamilyBalanceRank] Created " + CatalogAssetPath);
            return created;
        }

        /// <summary>
        /// Walks every <see cref="ShipFamilyDefinition"/> under Prefabs/Ships and
        /// every armed MEGA, then writes both lists onto <paramref name="catalog"/>.
        /// Shows a cancelable progress bar because prefab summing instantiates
        /// each chassis.
        /// </summary>
        /// <param name="catalog">Worksheet to overwrite. Null is ignored.</param>
        /// <returns>True when the snapshot was written. False on cancel or missing catalog.</returns>
        public static bool RefreshSnapshot(ShipFamilyBalanceRankCatalog catalog)
        {
            if (catalog == null)
                return false;

            var regular = new List<ShipFamilyBalanceRankRow>(256);
            var mega = new List<ShipFamilyBalanceRankRow>(96);

            try
            {
                // --- Regular families ---
                if (!AppendRegularRows(regular))
                    return false;

                // --- Armed MEGAs ---
                if (!AppendMegaRows(mega))
                    return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // --- Persist ---
            regular.Sort(CompareScanOrder);
            mega.Sort(CompareScanOrder);
            Undo.RecordObject(catalog, "Refresh Balance Rank Snapshot");
            catalog.ReplaceSnapshot(regular, mega);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log(
                $"[ShipFamilyBalanceRank] Snapshot {regular.Count} regular + {mega.Count} MEGA hulls.");
            return true;
        }

        /// <summary>
        /// Adds one row per family chassis that has a prefab and is not a leftover
        /// L7 / MEGA_### tree slot. Returns false when the user cancels the bar.
        /// </summary>
        static bool AppendRegularRows(List<ShipFamilyBalanceRankRow> dest)
        {
            string[] guids = AssetDatabase.FindAssets(
                "t:ShipFamilyDefinition", new[] { FamilySearchFolder });
            if (guids == null || guids.Length == 0)
                return true;

            int familyCount = guids.Length;
            for (int f = 0; f < familyCount; f++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[f]);
                var family = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (family?.upgradeTree == null)
                    continue;

                if (EditorUtility.DisplayCancelableProgressBar(
                        "Balance Rank Catalog",
                        "Family " + (family.familyId ?? family.name),
                        (float)f / familyCount))
                    return false;

                for (int t = 0; t < family.upgradeTree.Count; t++)
                {
                    ShipFamilyChassisTierEntry tier = family.upgradeTree[t];
                    if (tier?.prefab == null)
                        continue;

                    int level = Mathf.Max(1, tier.minHomePlanetLevel);
                    if (ShipFamilyPowerBarNorm.IsMegaTreeLevel(level)
                        || MegaShipCatalog.IsMegaChassisId(tier.chassisId))
                        continue;

                    ShipFamilyBalanceRankRow row = BuildRegularRow(family, tier, level);
                    if (row != null)
                        dest.Add(row);
                }
            }

            return true;
        }

        /// <summary>
        /// Extra Level at the tree slot with every HUD ability maxed, then stamp
        /// all-gun DPS and the family's default bullet bank.
        /// </summary>
        static ShipFamilyBalanceRankRow BuildRegularRow(
            ShipFamilyDefinition family,
            ShipFamilyChassisTierEntry tier,
            int shipLevel)
        {
            ShipAbilityLevelCounts maxed = ShipAbilityLevelCounts.Maxed(shipLevel);
            if (!ShipFamilyStatsCalculator.TrySumFromPrefab(
                    tier.prefab, family, shipLevel, in maxed,
                    out ShipComponentAbilityStats stats,
                    out ShipFamilyStatsCalculator.SumResult raw))
                return null;

            // --- Same DPS as the Orbit Menu bar ---
            float hullDps = ShipWeaponDpsMath.SumAllGunDps(
                raw.MatchedComponentIds, raw.PerComponentStats, shipLevel, in maxed);
            hullDps = ShipWeaponDpsMath.ApplyFamilyOffenseMuls(hullDps, family);
            ShipFamilyPowerScoreBreakdown breakdown =
                ShipFamilyPowerScoreBreakdown.FromEvaluatedHull(stats, hullDps);

            int bankIndex = BulletBankProfileUtility.ResolveBankIndexForFamily(family);
            TryReadBank(bankIndex, out string bankName, out float fpMul, out float rateMul, out float speedMul);

            var row = new ShipFamilyBalanceRankRow
            {
                familyId = family.familyId,
                chassisId = tier.chassisId,
                displayName = !string.IsNullOrWhiteSpace(tier.upgradeTreeShipName)
                    ? tier.upgradeTreeShipName.Trim()
                    : tier.chassisId,
                treeLevel = shipLevel,
                gunCount = CountRegularGuns(in raw),
                bankName = bankName,
                bankFirePowerMul = fpMul,
                bankFireRateMul = rateMul,
                bankBulletSpeedMul = speedMul,
                hullDps = hullDps,
                bankDps = hullDps * fpMul * rateMul,
                firePower = breakdown.firePower,
                fireRate = breakdown.fireRate,
                healthCap = breakdown.healthCap,
                healthRegen = breakdown.healthRegen,
                energyCap = breakdown.energyCap,
                energyRegen = breakdown.energyRegen,
                moveSpeed = breakdown.moveSpeed,
                turnSpeed = breakdown.turnSpeed,
                bulletSpeed = breakdown.bulletSpeed,
                family = family,
                prefab = tier.prefab,
            };
            return row;
        }

        /// <summary>
        /// Weapon mounts that <see cref="ShipWeaponDpsMath.SumAllGunDps"/> would
        /// actually score (not cosmetics, not unarmed parts).
        /// </summary>
        static int CountRegularGuns(in ShipFamilyStatsCalculator.SumResult raw)
        {
            if (raw.MatchedComponentIds == null || raw.PerComponentStats == null)
                return 0;

            int n = Mathf.Min(raw.MatchedComponentIds.Count, raw.PerComponentStats.Count);
            int guns = 0;
            for (int i = 0; i < n; i++)
            {
                string id = raw.MatchedComponentIds[i];
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (ShipFamilyPartCalcProfileSet.IsCosmeticPartName(id))
                    continue;
                if (!ShipComponentAbilityStatsMath.IsWeaponComponent(id))
                    continue;

                ShipComponentAbilityStats s = raw.PerComponentStats[i];
                if (s.firePower <= 0.01f && s.firePowerPerExtraLevel <= 0.01f)
                    continue;

                // Extra Level can still produce a 0-damage gun; count the mount anyway
                // so a designer sees "this prefab has N weapon children."
                guns++;
            }

            return guns;
        }

        /// <summary>
        /// Adds one row per armed MEGA. Unarmed editor hulls stay out so a 0-gun
        /// placeholder cannot sit at the top of a DPS sort.
        /// </summary>
        static bool AppendMegaRows(List<ShipFamilyBalanceRankRow> dest)
        {
            MegaShipCatalog catalog = LoadMegaCatalog();
            if (catalog?.entries == null)
                return true;

            int count = catalog.entries.Count;
            for (int i = 0; i < count; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Balance Rank Catalog",
                        "MEGA " + i + " / " + count,
                        (float)i / Mathf.Max(1, count)))
                    return false;

                if (!catalog.IsEligibleForMatch(i))
                    continue;

                ShipFamilyBalanceRankRow row = BuildMegaRow(catalog, i);
                if (row != null)
                    dest.Add(row);
            }

            return true;
        }

        /// <summary>
        /// Static MEGA breakdown plus per-weapon-type bank DPS.
        /// MEGAs have no Extra Level — numbers match the L7 power-bar pool.
        /// </summary>
        static ShipFamilyBalanceRankRow BuildMegaRow(MegaShipCatalog catalog, int catalogIndex)
        {
            if (!catalog.TryGetEntry(catalogIndex, out MegaShipCatalogEntry entry) || entry == null)
                return null;

            ShipFamilyPowerScoreBreakdown breakdown = catalog.GetPowerBreakdown(catalogIndex);
            CollectMegaBankDps(
                catalog, entry,
                out int gunCount,
                out float bankDps,
                out string bankName,
                out float fpMul,
                out float rateMul,
                out float speedMul);

            var row = new ShipFamilyBalanceRankRow
            {
                familyId = "MEGA",
                chassisId = MegaShipCatalog.FormatChassisId(catalogIndex),
                displayName = catalog.GetDisplayName(catalogIndex),
                treeLevel = ShipFamilyPowerBarNorm.MegaTreeLevel,
                gunCount = gunCount,
                bankName = bankName,
                bankFirePowerMul = fpMul,
                bankFireRateMul = rateMul,
                bankBulletSpeedMul = speedMul,
                hullDps = breakdown.GetDisplayDps(),
                bankDps = bankDps,
                firePower = breakdown.firePower,
                fireRate = breakdown.fireRate,
                healthCap = breakdown.healthCap,
                healthRegen = breakdown.healthRegen,
                energyCap = breakdown.energyCap,
                energyRegen = breakdown.energyRegen,
                moveSpeed = breakdown.moveSpeed,
                turnSpeed = breakdown.turnSpeed,
                bulletSpeed = breakdown.bulletSpeed,
                prefab = entry.prefab,
                megaCatalog = catalog,
            };
            return row;
        }

        /// <summary>
        /// Sums each unique gun's <c>count × FP × RoF × that gun's bank muls</c>.
        /// Also builds the bank-name label (comma-separated unique categories).
        /// </summary>
        static void CollectMegaBankDps(
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry,
            out int gunCount,
            out float bankDps,
            out string bankName,
            out float firstFpMul,
            out float firstRateMul,
            out float firstSpeedMul)
        {
            gunCount = 0;
            bankDps = 0f;
            firstFpMul = 1f;
            firstRateMul = 1f;
            firstSpeedMul = 1f;
            var names = new List<string>(4);
            bool haveFirst = false;

            if (entry?.componentCounts == null)
            {
                bankName = string.Empty;
                return;
            }

            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry row)
                    || row == null)
                    continue;
                if (!row.isWeapon && !ShipFamilyPartTypes.IsWeapon(row.partType))
                    continue;

                MegaShipPartStats stats = catalog.ResolveRuntimeStats(row.stats);
                if (stats.firePower <= 0.01f)
                    continue;

                gunCount += count.count;
                int bankIndex = catalog.ResolveWeaponBankIndex(row);
                TryReadBank(bankIndex, out string name, out float fpMul, out float rateMul, out float speedMul);
                if (!haveFirst)
                {
                    firstFpMul = fpMul;
                    firstRateMul = rateMul;
                    firstSpeedMul = speedMul;
                    haveFirst = true;
                }

                if (!string.IsNullOrEmpty(name) && !ContainsName(names, name))
                    names.Add(name);

                float hull = count.count * ShipFamilyPowerScoreBreakdown.ComputeSustainedDps(
                    stats.firePower, stats.fireRate);
                bankDps += hull * fpMul * rateMul;
            }

            bankName = names.Count == 0 ? string.Empty : string.Join(", ", names);
        }

        /// <summary>Case-insensitive name already in the MEGA bank label list.</summary>
        static bool ContainsName(List<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Reads a <see cref="BulletVfxBank"/> category's display name and
        /// authored multipliers. Zero / unset muls become 1 (same as combat).
        /// </summary>
        static void TryReadBank(
            int bankIndex,
            out string name,
            out float firePowerMul,
            out float fireRateMul,
            out float bulletSpeedMul)
        {
            name = string.Empty;
            firePowerMul = 1f;
            fireRateMul = 1f;
            bulletSpeedMul = 1f;

            var bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return;

            if (bank.TryGetCategoryName(bankIndex, out string category) && !string.IsNullOrEmpty(category))
                name = category;

            if (!bank.TryGetProfile(bankIndex, out BulletBankProfile profile) || profile == null)
                return;

            BulletBankStatModifiers m = profile.statModifiers;
            firePowerMul = SafeMul(m.firePowerMultiplier);
            fireRateMul = SafeMul(m.fireRateMultiplier);
            bulletSpeedMul = SafeMul(m.bulletSpeedMultiplier);
        }

        /// <summary>Combat treat 0 as "unset → 1", never as "deal no damage."</summary>
        static float SafeMul(float authored) => authored > 0f ? authored : 1f;

        /// <summary>
        /// Resources load first (same as play mode), then an AssetDatabase hunt
        /// if the Resources copy is missing in this Editor session.
        /// </summary>
        static MegaShipCatalog LoadMegaCatalog()
        {
            MegaShipCatalog loaded = MegaShipCatalog.Load();
            if (loaded != null)
                return loaded;

            string[] guids = AssetDatabase.FindAssets("t:MegaShipCatalog");
            if (guids == null || guids.Length == 0)
                return null;
            return AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        /// <summary>Stable snapshot order: family id, then tree level, then chassis id.</summary>
        static int CompareScanOrder(ShipFamilyBalanceRankRow a, ShipFamilyBalanceRankRow b)
        {
            int family = string.CompareOrdinal(a?.familyId, b?.familyId);
            if (family != 0)
                return family;
            int level = (a?.treeLevel ?? 0).CompareTo(b?.treeLevel ?? 0);
            if (level != 0)
                return level;
            return string.CompareOrdinal(a?.chassisId, b?.chassisId);
        }
    }
}
#endif

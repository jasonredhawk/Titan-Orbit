using System;
using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Editor snapshot of every regular-family chassis and every armed MEGA hull,
    /// ranked by combat and mobility numbers so a designer can find outliers
    /// (too many guns, huge DPS, tiny health, and so on).
    /// <para>
    /// [TITAN-ORBIT] Combat, NetCode, and the Orbit Menu power bar never read this
    /// asset. It is the same kind of authoring hub as
    /// <see cref="ShipFamilyDefinitionCatalog"/> — a personal rebalance worksheet,
    /// not a runtime table. Refresh it from the custom inspector after you edit
    /// a prefab or family.
    /// </para>
    /// Paired with editor builder <c>ShipFamilyBalanceRankBuilder</c> and
    /// inspector <c>ShipFamilyBalanceRankCatalogEditor</c>.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipFamilyBalanceRankCatalog",
        menuName = "Titan Orbit/Ship Family Balance Rank Catalog")]
    public class ShipFamilyBalanceRankCatalog : ScriptableObject
    {
        /// <summary>
        /// When the last Refresh Snapshot finished (local editor clock).
        /// Empty until the first refresh.
        /// </summary>
        [Tooltip("Set by Refresh Snapshot. Not used in play mode.")]
        public string lastRefreshedLocal;

        /// <summary>How many regular + MEGA rows the last refresh wrote.</summary>
        [Tooltip("Set by Refresh Snapshot so you can see the scan completed.")]
        public int lastRefreshRowCount;

        /// <summary>
        /// Regular family upgrade-tree hulls (levels 1–6). Never mixed with MEGAs
        /// so a 90-gun MEGA cannot hide which Astro Eagle is the strongest.
        /// </summary>
        [Tooltip("One row per family chassis. Rebuilt by Refresh Snapshot.")]
        public List<ShipFamilyBalanceRankRow> regularRows = new List<ShipFamilyBalanceRankRow>();

        /// <summary>
        /// Armed MEGA catalog hulls only. Separate list so you rebalance MEGAs
        /// against other MEGAs, not against family starters.
        /// </summary>
        [Tooltip("One row per armed MEGA. Rebuilt by Refresh Snapshot.")]
        public List<ShipFamilyBalanceRankRow> megaRows = new List<ShipFamilyBalanceRankRow>();

        /// <summary>
        /// Replaces both lists and stamps the refresh time.
        /// Called from the editor builder after a full project scan.
        /// </summary>
        /// <param name="regular">New family rows. Null becomes an empty list.</param>
        /// <param name="mega">New MEGA rows. Null becomes an empty list.</param>
        public void ReplaceSnapshot(
            List<ShipFamilyBalanceRankRow> regular,
            List<ShipFamilyBalanceRankRow> mega)
        {
            // --- Write the worksheet ---
            // [EDITOR] The inspector sorts a display copy. These lists stay in scan
            // order (family name, then tree level) so a refresh is reproducible.
            regularRows = regular ?? new List<ShipFamilyBalanceRankRow>();
            megaRows = mega ?? new List<ShipFamilyBalanceRankRow>();
            lastRefreshRowCount = regularRows.Count + megaRows.Count;
            lastRefreshedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>
    /// One hull on the balance worksheet: identity, gun count, family/MEGA bank
    /// multipliers, hull DPS vs bank-adjusted DPS, and object links so you can
    /// ping the prefab and delete extra guns.
    /// </summary>
    [Serializable]
    public class ShipFamilyBalanceRankRow
    {
        /// <summary>Family folder id (<c>AstroEagle</c>) or <c>MEGA</c>.</summary>
        [Tooltip("AstroEagle, SpaceExcalibur, MEGA, …")]
        public string familyId;

        /// <summary>Upgrade-tree or catalog chassis id (<c>SpaceExcalibur_16</c>, <c>MEGA_007</c>).</summary>
        public string chassisId;

        /// <summary>Orbit-menu label when authored; otherwise the chassis id.</summary>
        public string displayName;

        /// <summary>Tree level 1–6 for family hulls. MEGAs use 7.</summary>
        public int treeLevel;

        /// <summary>
        /// How many weapon mounts the prefab (or MEGA part list) actually has.
        /// This is the column that catches an 18-gun Space Excalibur.
        /// </summary>
        [Tooltip("Weapon mounts with Fire Power. Cosmetic children are skipped.")]
        public int gunCount;

        /// <summary>
        /// BulletVfxBank category name(s). Families have one default bank.
        /// MEGAs may list several (Bullets, Plasma, Laser) when gun types differ.
        /// </summary>
        public string bankName;

        /// <summary>
        /// Bank Fire Power multiplier (1 = unchanged). MEGA rows use the first
        /// armed weapon's bank; <see cref="bankDps"/> still applies each gun's
        /// own bank.
        /// </summary>
        public float bankFirePowerMul = 1f;

        /// <summary>Bank shots-per-second multiplier (1 = unchanged).</summary>
        public float bankFireRateMul = 1f;

        /// <summary>Bank projectile-speed multiplier (1 = unchanged).</summary>
        public float bankBulletSpeedMul = 1f;

        /// <summary>
        /// All-gun DPS after Extra Level, before the bullet bank
        /// (<c>Σ firePower × fireRate</c>). Same score as the Orbit Menu bar.
        /// </summary>
        public float hullDps;

        /// <summary>
        /// <see cref="hullDps"/> with bank <c>firePower × fireRate</c>
        /// multipliers. Families use one bank; MEGAs multiply per weapon type.
        /// </summary>
        public float bankDps;

        /// <summary>Primary / summed Fire Power after Extra Level (damage per shot).</summary>
        public float firePower;

        /// <summary>Primary / damage-weighted Fire Rate (shots per second).</summary>
        public float fireRate;

        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float turnSpeed;
        public float bulletSpeed;

        /// <summary>
        /// Family asset that owns this chassis. Click in the inspector to open
        /// the upgrade tree. Null on MEGA rows.
        /// </summary>
        public ShipFamilyDefinition family;

        /// <summary>Chassis prefab — ping this, then delete extra Weapon children.</summary>
        public GameObject prefab;

        /// <summary>MEGA catalog asset. Null on regular-family rows.</summary>
        public MegaShipCatalog megaCatalog;

        /// <summary>
        /// Number used when the inspector sorts a display copy.
        /// Strings (family / chassis / bank) sort alphabetically via a hash-free
        /// compare in the editor; this method is for numeric columns only.
        /// </summary>
        /// <param name="key">Which column the inspector dropdown selected.</param>
        /// <returns>Sort value. Missing rows compare as 0.</returns>
        public float GetNumericSortValue(ShipFamilyBalanceRankSortKey key)
        {
            switch (key)
            {
                case ShipFamilyBalanceRankSortKey.BankDps: return bankDps;
                case ShipFamilyBalanceRankSortKey.HullDps: return hullDps;
                case ShipFamilyBalanceRankSortKey.Guns: return gunCount;
                case ShipFamilyBalanceRankSortKey.FirePower: return firePower;
                case ShipFamilyBalanceRankSortKey.FireRate: return fireRate;
                case ShipFamilyBalanceRankSortKey.HealthCap: return healthCap;
                case ShipFamilyBalanceRankSortKey.HealthRegen: return healthRegen;
                case ShipFamilyBalanceRankSortKey.EnergyCap: return energyCap;
                case ShipFamilyBalanceRankSortKey.EnergyRegen: return energyRegen;
                case ShipFamilyBalanceRankSortKey.MoveSpeed: return moveSpeed;
                case ShipFamilyBalanceRankSortKey.TurnSpeed: return turnSpeed;
                case ShipFamilyBalanceRankSortKey.BulletSpeed: return bulletSpeed;
                case ShipFamilyBalanceRankSortKey.TreeLevel: return treeLevel;
                default: return bankDps;
            }
        }
    }

    /// <summary>
    /// How a hull's Bank DPS sits versus the list average. Regular and MEGA
    /// pools never share an average — a MEGA should not paint every family
    /// row "too weak."
    /// </summary>
    public enum ShipFamilyBalanceRankOutlierKind
    {
        /// <summary>Inside the average band (not an outlier).</summary>
        Typical = 0,
        /// <summary>Bank DPS is at least <see cref="ShipFamilyBalanceRankDpsStats.StrongMul"/> × the list average.</summary>
        TooStrong = 1,
        /// <summary>Bank DPS is at most <see cref="ShipFamilyBalanceRankDpsStats.WeakMul"/> × the list average.</summary>
        TooWeak = 2,
    }

    /// <summary>
    /// Average Bank DPS and outlier cutoffs for one worksheet list
    /// (regular families or MEGAs). The inspector colors rows from this.
    /// <para>
    /// [TITAN-ORBIT] Cutoffs are ratios of the mean, not standard deviation,
    /// so "too strong" means "this hull deals 1.5× the typical DPS in this
    /// pool" — easy to read while you delete extra guns.
    /// </para>
    /// </summary>
    public struct ShipFamilyBalanceRankDpsStats
    {
        /// <summary>Bank DPS ≥ this × average is too strong (red).</summary>
        public const float StrongMul = 1.5f;

        /// <summary>Bank DPS ≤ this × average is too weak (blue).</summary>
        public const float WeakMul = 0.5f;

        /// <summary>How many rows went into <see cref="averageBankDps"/>.</summary>
        public int sampleCount;

        /// <summary>Mean Bank DPS of the list. 0 when the list is empty.</summary>
        public float averageBankDps;

        /// <summary><see cref="averageBankDps"/> × <see cref="StrongMul"/>.</summary>
        public float strongThreshold;

        /// <summary><see cref="averageBankDps"/> × <see cref="WeakMul"/>.</summary>
        public float weakThreshold;

        /// <summary>
        /// Averages Bank DPS across every non-null row. Empty / all-null lists
        /// stay at 0 so the inspector does not paint false outliers.
        /// </summary>
        /// <param name="rows">Regular or MEGA snapshot. Null is treated as empty.</param>
        /// <returns>Pool stats used to color and label rows.</returns>
        public static ShipFamilyBalanceRankDpsStats Compute(IReadOnlyList<ShipFamilyBalanceRankRow> rows)
        {
            var stats = new ShipFamilyBalanceRankDpsStats();
            if (rows == null || rows.Count == 0)
                return stats;

            // --- Mean Bank DPS ---
            // We include 0-DPS hulls so an unarmed family row pulls the average
            // down and still classifies as too weak.
            double sum = 0d;
            int n = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                ShipFamilyBalanceRankRow row = rows[i];
                if (row == null)
                    continue;
                sum += row.bankDps;
                n++;
            }

            if (n <= 0)
                return stats;

            stats.sampleCount = n;
            stats.averageBankDps = (float)(sum / n);
            stats.strongThreshold = stats.averageBankDps * StrongMul;
            stats.weakThreshold = stats.averageBankDps * WeakMul;
            return stats;
        }

        /// <summary>
        /// Classifies one hull against this pool. A missing average (empty list)
        /// is always Typical so we do not color a blank worksheet.
        /// </summary>
        /// <param name="bankDps">That hull's bank-adjusted all-gun DPS.</param>
        public ShipFamilyBalanceRankOutlierKind Classify(float bankDps)
        {
            if (sampleCount <= 0 || averageBankDps <= 0.0001f)
                return ShipFamilyBalanceRankOutlierKind.Typical;
            if (bankDps + 0.0001f >= strongThreshold)
                return ShipFamilyBalanceRankOutlierKind.TooStrong;
            if (bankDps <= weakThreshold + 0.0001f)
                return ShipFamilyBalanceRankOutlierKind.TooWeak;
            return ShipFamilyBalanceRankOutlierKind.Typical;
        }

        /// <summary>
        /// How many times the average this DPS is (<c>2.4</c> means 240% of the
        /// mean). 0 when the average is missing.
        /// </summary>
        public float RatioToAverage(float bankDps)
        {
            if (averageBankDps <= 0.0001f)
                return 0f;
            return bankDps / averageBankDps;
        }
    }

    /// <summary>
    /// Inspector sort columns for the balance worksheet.
    /// Default is <see cref="BankDps"/> so the strongest gunboat is on top.
    /// </summary>
    public enum ShipFamilyBalanceRankSortKey
    {
        BankDps = 0,
        HullDps = 1,
        Guns = 2,
        FirePower = 3,
        FireRate = 4,
        HealthCap = 5,
        HealthRegen = 6,
        EnergyCap = 7,
        EnergyRegen = 8,
        MoveSpeed = 9,
        TurnSpeed = 10,
        BulletSpeed = 11,
        TreeLevel = 12,
        FamilyId = 13,
        ChassisId = 14,
        BankName = 15,
    }
}

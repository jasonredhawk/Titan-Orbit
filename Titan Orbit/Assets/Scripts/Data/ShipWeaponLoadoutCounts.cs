using System;
using System.Collections.Generic;
using System.Text;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// How many barrels of each player-facing weapon class sit on one hull.
    /// Used by Orbit Menu upgrade-tree cards so a shopper can read
    /// <c>FIREBALLS ×4</c> / <c>LASER ×2</c> without opening the chassis.
    /// <para>
    /// [TITAN-ORBIT] Four classes match combat: rapid <see cref="Gun"/>
    /// (card prints the hull's bullet-type name, same string as planet labels),
    /// hitscan / cannon <see cref="Laser"/> (the card says LASER even though
    /// the catalog type is Weapon Cannon), homing <see cref="Missile"/>, and
    /// high-speed <see cref="Sniper"/>. Zero counts stay off the card so a
    /// two-gun family hull does not print empty missile / sniper rows.
    /// </para>
    /// </summary>
    public struct ShipWeaponLoadoutCounts : IEquatable<ShipWeaponLoadoutCounts>
    {
        /// <summary>Rapid projectile barrels (Weapon Bullet / Gun tag).</summary>
        public int Gun;

        /// <summary>
        /// Card label for <see cref="Gun"/> (FIREBALLS, RIFT, …). Empty means fall back to GUN.
        /// </summary>
        public string GunLabel;

        /// <summary>Cannon lasers — <c>isLaser</c> or Weapon Cannon part type.</summary>
        public int Laser;

        /// <summary>Homing / rocket barrels.</summary>
        public int Missile;

        /// <summary>High-speed sniper barrels.</summary>
        public int Sniper;

        /// <summary>Sum of every class. 0 means the card should hide the roster.</summary>
        public int Total => Gun + Laser + Missile + Sniper;

        /// <summary>True when at least one barrel was classified.</summary>
        public bool HasAny => Total > 0;

        /// <summary>How many telemetry lines the card should reserve (types with count &gt; 0).</summary>
        public int VisibleLineCount
        {
            get
            {
                int n = 0;
                if (Gun > 0) n++;
                if (Laser > 0) n++;
                if (Missile > 0) n++;
                if (Sniper > 0) n++;
                return n;
            }
        }

        /// <summary>True when every slot matches <paramref name="other"/>.</summary>
        public bool Equals(ShipWeaponLoadoutCounts other)
        {
            return Gun == other.Gun
                   && Laser == other.Laser
                   && Missile == other.Missile
                   && Sniper == other.Sniper
                   && string.Equals(GunLabel, other.GunLabel, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ShipWeaponLoadoutCounts other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            int h = (Gun * 397) ^ (Laser * 31) ^ (Missile * 17) ^ Sniper;
            if (!string.IsNullOrEmpty(GunLabel))
                h ^= StringComparer.Ordinal.GetHashCode(GunLabel);
            return h;
        }
    }

    /// <summary>
    /// Builds <see cref="ShipWeaponLoadoutCounts"/> for an Orbit Menu chassis id.
    /// MEGA hulls walk the catalog unique-component table (same rows combat uses).
    /// Regular family hulls walk the chassis prefab's weapon assemblies — one
    /// turret / gun root is one barrel, matching <see cref="MegaShipPartClassifier.CollectWeaponAssemblies"/>.
    /// <para>
    /// Counts are cached per chassis id. Orbit Menu refresh is not a sim tick, but
    /// the tree paints many cards at once; a dictionary lookup avoids walking the
    /// same prefab again on every moon-dock pulse. Editor catalog rebuilds call
    /// <see cref="InvalidateCache"/>.
    /// </para>
    /// </summary>
    public static class ShipWeaponLoadout
    {
        /// <summary>Fallback when a hull has guns but no named VFX bank.</summary>
        public const string LabelGun = "GUN";

        /// <summary>Cannon class. Card says LASER so it matches the hitscan beam the player sees.</summary>
        public const string LabelLaser = "LASER";

        /// <summary>Homing / rocket class.</summary>
        public const string LabelMissile = "MISSILE";

        /// <summary>High-speed class.</summary>
        public const string LabelSniper = "SNIPER";

        /// <summary>Chassis id → last counted roster. Cleared on catalog / family rebake.</summary>
        static readonly Dictionary<string, ShipWeaponLoadoutCounts> s_cache =
            new Dictionary<string, ShipWeaponLoadoutCounts>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Scratch list for <see cref="MegaShipPartClassifier.CollectWeaponAssemblies"/> (main thread only).</summary>
        static readonly List<Transform> s_assemblies = new List<Transform>(16);

        /// <summary>Shared format buffer so Orbit Menu refresh does not new a StringBuilder per card.</summary>
        static readonly StringBuilder s_format = new StringBuilder(64);

        /// <summary>
        /// Drops the chassis-id cache after an editor rebake or catalog rebuild
        /// so the next Orbit Menu paint re-walks hulls.
        /// </summary>
        public static void InvalidateCache()
        {
            s_cache.Clear();
        }

        /// <summary>
        /// Resolves the roster for a tree-card chassis id (<c>AstroEagle_03</c> or <c>MEGA_007</c>).
        /// Empty / unknown ids return a zero roster (card hides the row).
        /// </summary>
        /// <param name="chassisId">Upgrade-tree chassis id. Null / blank returns default.</param>
        public static ShipWeaponLoadoutCounts ForChassisId(string chassisId, int planetOrHullBankIndex = -1)
        {
            // --- Guard ---
            if (string.IsNullOrWhiteSpace(chassisId))
                return default;

            // --- Cache ---
            // [TITAN-ORBIT] Same chassis is painted on every tree refresh while docked.
            // Key includes the planet gun so CosmicShark_01 / Fireballs and / Rift stay distinct.
            string cacheKey = planetOrHullBankIndex >= 0
                ? chassisId + "|" + planetOrHullBankIndex.ToString()
                : chassisId;
            if (s_cache.TryGetValue(cacheKey, out ShipWeaponLoadoutCounts cached))
                return cached;

            ShipWeaponLoadoutCounts counts = CountUncached(chassisId, planetOrHullBankIndex);
            s_cache[cacheKey] = counts;
            return counts;
        }

        /// <summary>
        /// Counts barrels on a family (or preview) prefab without a chassis-id cache key.
        /// Editor tree preview uses this when the tier has a prefab but no live store slot.
        /// </summary>
        /// <param name="prefab">Chassis prefab asset. Null returns default.</param>
        /// <param name="family">Optional family so the gun line can print Fireballs / Rift / ….</param>
        public static ShipWeaponLoadoutCounts CountFromPrefab(
            GameObject prefab,
            ShipFamilyDefinition family = null,
            int planetOrHullBankIndex = -1)
        {
            ShipWeaponLoadoutCounts counts = CountFromPrefabAssemblies(prefab);
            if (counts.Gun > 0)
                counts.GunLabel = FormatGunCardLabel(
                    BulletBankProfileUtility.FormatFamilyBulletTypeName(family, planetOrHullBankIndex));
            return counts;
        }

        /// <summary>
        /// Walks one MEGA catalog hull: each unique weapon row × how many copies
        /// that name has on the prefab. <c>isLaser</c> wins (same as combat resolve).
        /// </summary>
        /// <param name="catalog">Loaded <see cref="MegaShipCatalog"/>. Null returns default.</param>
        /// <param name="entry">One hull row. Null returns default.</param>
        public static ShipWeaponLoadoutCounts CountFromMegaEntry(
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry)
        {
            ShipWeaponLoadoutCounts counts = default;
            if (catalog == null || entry?.componentCounts == null)
                return counts;

            for (int i = 0; i < entry.componentCounts.Count; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;

                // --- Unique row (authored type + isLaser) ---
                if (catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry row)
                    && row != null)
                {
                    if (!row.isWeapon && !ShipFamilyPartTypes.IsWeapon(row.partType))
                        continue;
                    Add(ref counts, row.partType, row.isLaser, count.count);
                    continue;
                }

                // --- Stale catalog: classify the part name the same way discovery does ---
                string fallbackType = MegaShipPartClassifier.ResolvePartType(count.displayName);
                if (!ShipFamilyPartTypes.IsWeapon(fallbackType))
                    continue;
                Add(ref counts, fallbackType, isLaser: false, count.count);
            }

            if (counts.Gun > 0)
                counts.GunLabel = ResolveMegaGunLabel(catalog, entry);
            return counts;
        }

        /// <summary>
        /// Builds the card string (<c>FIREBALLS ×4</c> then a newline per present type).
        /// Empty roster returns an empty string so the UI can hide the row.
        /// </summary>
        /// <param name="counts">Counted barrels.</param>
        public static string FormatTelemetry(in ShipWeaponLoadoutCounts counts)
        {
            // --- Format ---
            // [STANDARD] Reuse one StringBuilder; ToString still allocates the result
            // once. Callers dirty-check so TMP.text is not rewritten every refresh.
            s_format.Length = 0;
            string gunLabel = !string.IsNullOrEmpty(counts.GunLabel) ? counts.GunLabel : LabelGun;
            AppendLine(s_format, gunLabel, counts.Gun);
            AppendLine(s_format, LabelLaser, counts.Laser);
            AppendLine(s_format, LabelMissile, counts.Missile);
            AppendLine(s_format, LabelSniper, counts.Sniper);
            return s_format.Length > 0 ? s_format.ToString() : string.Empty;
        }

        /// <summary>Picks MEGA catalog vs family prefab from the chassis-id prefix.</summary>
        static ShipWeaponLoadoutCounts CountUncached(string chassisId, int planetOrHullBankIndex = -1)
        {
            // --- MEGA catalog ---
            if (MegaShipCatalog.IsMegaChassisId(chassisId))
            {
                MegaShipCatalog mega = MegaShipCatalog.Load();
                if (mega != null && mega.TryGetEntryByChassisId(chassisId, out MegaShipCatalogEntry entry))
                    return CountFromMegaEntry(mega, entry);
                return default;
            }

            // --- Regular family prefab ---
            // [TITAN-ORBIT] PlanetShipFamilyConfig is the same ladder the tree already
            // uses for names and power bars. We only need the prefab to count mounts.
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            GameObject prefab = config != null ? config.GetPrefabByChassisId(chassisId) : null;
            ShipFamilyDefinition family = config != null
                ? config.GetShipFamilyDefinitionForChassisId(chassisId)
                : null;
            return CountFromPrefab(prefab, family, planetOrHullBankIndex);
        }

        /// <summary>
        /// One combat mount = one barrel. Tagged MEGA weapons stop at the tagged
        /// GameObject; family hulls use the first gun / turret / launcher name in
        /// each branch so nested barrels are not double-counted.
        /// </summary>
        static ShipWeaponLoadoutCounts CountFromPrefabAssemblies(GameObject prefab)
        {
            ShipWeaponLoadoutCounts counts = default;
            if (prefab == null)
                return counts;

            Transform root = prefab.transform;
            s_assemblies.Clear();
            MegaShipPartClassifier.CollectWeaponAssemblies(root, s_assemblies);

            // --- Fallback when the prefab has no tagged / named assembly ---
            // Some older family hulls only match via familyId_Weapon component ids.
            if (s_assemblies.Count == 0)
                CollectFamilyWeaponRoots(root, s_assemblies);

            for (int i = 0; i < s_assemblies.Count; i++)
            {
                Transform t = s_assemblies[i];
                if (t == null)
                    continue;

                string partType = MegaShipPartClassifier.ResolvePartType(t);
                if (!ShipFamilyPartTypes.IsWeapon(partType))
                {
                    // Name did not map to a weapon profile — still count a mount
                    // that the family catalog treats as a gun.
                    if (!ShipComponentAbilityStatsMath.IsWeaponComponent(t.name)
                        && !ShipComponentAbilityStatsMath.IsWeaponComponent(
                            MegaShipPartClassifier.GetPrefabAssetName(t)))
                        continue;
                    partType = ShipFamilyPartTypes.WeaponBullet;
                }

                Add(ref counts, partType, isLaser: false, 1);
            }

            return counts;
        }

        /// <summary>
        /// Walks family-style children and keeps assembly roots only (no nested barrels).
        /// Used when <see cref="MegaShipPartClassifier.CollectWeaponAssemblies"/> found nothing.
        /// </summary>
        static void CollectFamilyWeaponRoots(Transform root, List<Transform> into)
        {
            if (root == null)
                return;

            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == root)
                    continue;
                if (!MegaShipPartClassifier.IsWeaponAssemblyRoot(t, root)
                    && !IsFamilyWeaponComponentRoot(t, root))
                    continue;
                into.Add(t);
            }
        }

        /// <summary>
        /// True when this child is a family-catalog weapon (AstroEagle_Weapon) and no
        /// ancestor under the hull is already a weapon root.
        /// </summary>
        static bool IsFamilyWeaponComponentRoot(Transform t, Transform hull)
        {
            if (t == null)
                return false;
            if (!ShipComponentAbilityStatsMath.IsWeaponComponent(t.name)
                && !ShipComponentAbilityStatsMath.IsWeaponComponent(
                    MegaShipPartClassifier.GetPrefabAssetName(t)))
                return false;

            Transform parent = t.parent;
            while (parent != null && parent != hull)
            {
                if (MegaShipPartClassifier.IsWeaponMountTransform(parent)
                    || ShipComponentAbilityStatsMath.IsWeaponComponent(parent.name))
                    return false;
                parent = parent.parent;
            }

            return true;
        }

        /// <summary>
        /// Adds <paramref name="count"/> barrels to the matching class.
        /// <paramref name="isLaser"/> wins (hitscan cannon), then part-type missile /
        /// sniper / cannon, then gun.
        /// </summary>
        static void Add(ref ShipWeaponLoadoutCounts counts, string partType, bool isLaser, int count)
        {
            if (count <= 0)
                return;

            if (isLaser || ShipFamilyPartTypes.IsWeaponCannonProfile(partType))
            {
                counts.Laser += count;
                return;
            }

            if (string.Equals(partType, ShipFamilyPartTypes.WeaponMissile, StringComparison.OrdinalIgnoreCase))
            {
                counts.Missile += count;
                return;
            }

            if (string.Equals(partType, ShipFamilyPartTypes.WeaponSniper, StringComparison.OrdinalIgnoreCase))
            {
                counts.Sniper += count;
                return;
            }

            counts.Gun += count;
        }

        /// <summary>
        /// First gun-class bank on this MEGA, else the type-table Weapon Bullet bank.
        /// Same category name the HUD / ram sparks use for that hull's rapid guns.
        /// </summary>
        static string ResolveMegaGunLabel(MegaShipCatalog catalog, MegaShipCatalogEntry entry)
        {
            if (catalog == null)
                return LabelGun;

            if (catalog.TryGetFirstGunBankIndex(entry, out int gunBank))
            {
                return FormatGunCardLabel(
                    BulletBankProfileUtility.FormatBankCategoryName(gunBank));
            }

            return FormatGunCardLabel(
                BulletBankProfileUtility.FormatBankCategoryName(
                    catalog.GetTypeTableBankIndex(ShipFamilyPartTypes.WeaponBullet)));
        }

        /// <summary>
        /// Card telemetry is ALL CAPS. SplitCamelCase keeps EnergySpheres readable
        /// as ENERGY SPHERES — same bank name planet labels show as Fireballs / Rift.
        /// </summary>
        static string FormatGunCardLabel(string bankName)
        {
            if (string.IsNullOrWhiteSpace(bankName))
                return LabelGun;
            string spaced = DisplayNameFormatting.SplitCamelCase(bankName.Trim());
            return string.IsNullOrWhiteSpace(spaced) ? LabelGun : spaced.ToUpperInvariant();
        }

        /// <summary>Appends <c>LABEL ×N</c> when <paramref name="count"/> is greater than 0.</summary>
        static void AppendLine(StringBuilder sb, string label, int count)
        {
            if (count <= 0 || sb == null)
                return;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(label);
            sb.Append(" ×");
            sb.Append(count);
        }
    }
}

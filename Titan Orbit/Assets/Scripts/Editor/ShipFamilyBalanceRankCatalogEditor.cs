#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Custom inspector for <see cref="ShipFamilyBalanceRankCatalog"/>.
    /// Refresh rebuilds both lists from prefabs. Sort and filter only reorder a
    /// display copy — the serialized snapshot stays in scan order so you can
    /// refresh after editing SpaceExcalibur_16 and see the new gun count.
    /// <para>
    /// Each list computes its own average Bank DPS. Rows at or above 1.5× that
    /// average paint red (too strong). Rows at or below 0.5× paint blue (too
    /// weak). Regular and MEGA never share an average. Combat does not read
    /// this inspector.
    /// </para>
    /// </summary>
    [CustomEditor(typeof(ShipFamilyBalanceRankCatalog))]
    public class ShipFamilyBalanceRankCatalogEditor : UnityEditor.Editor
    {
        const string PrefsSort = "TitanOrbit.BalanceRank.SortKey";
        const string PrefsDesc = "TitanOrbit.BalanceRank.Descending";

        /// <summary>Too-strong row tint (warm red). HelpBox uses GUI.backgroundColor.</summary>
        static readonly Color TooStrongTint = new Color(0.92f, 0.34f, 0.28f, 1f);
        /// <summary>Too-weak row tint (cool blue) so the two outlier kinds never look alike.</summary>
        static readonly Color TooWeakTint = new Color(0.32f, 0.52f, 0.88f, 1f);
        /// <summary>Near-average rows stay a quiet slate so color is reserved for outliers.</summary>
        static readonly Color TypicalTint = new Color(0.22f, 0.24f, 0.28f, 1f);

        ShipFamilyBalanceRankSortKey _sortKey = ShipFamilyBalanceRankSortKey.BankDps;
        bool _descending = true;
        string _filter = string.Empty;
        Vector2 _regularScroll;
        Vector2 _megaScroll;
        bool _showRawLists;

        /// <summary>Loads last sort prefs and draws the worksheet.</summary>
        void OnEnable()
        {
            _sortKey = (ShipFamilyBalanceRankSortKey)EditorPrefs.GetInt(
                PrefsSort, (int)ShipFamilyBalanceRankSortKey.BankDps);
            _descending = EditorPrefs.GetBool(PrefsDesc, true);
        }

        /// <summary>
        /// Toolbar, then two scroll lists. Does not draw the default serialized
        /// lists (hundreds of rows would freeze the Inspector).
        /// </summary>
        public override void OnInspectorGUI()
        {
            var catalog = target as ShipFamilyBalanceRankCatalog;
            if (catalog == null)
                return;

            DrawHelp();
            DrawToolbar(catalog);
            EditorGUILayout.Space(8);
            DrawListSection(
                "Regular family hulls (L1–L6)",
                catalog.regularRows,
                ref _regularScroll);
            EditorGUILayout.Space(8);
            DrawListSection(
                "MEGA catalog hulls (armed only)",
                catalog.megaRows,
                ref _megaScroll);

            EditorGUILayout.Space(12);
            _showRawLists = EditorGUILayout.Foldout(_showRawLists, "Raw serialized lists (debug)");
            if (_showRawLists)
            {
                serializedObject.Update();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("regularRows"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("megaRows"), true);
                serializedObject.ApplyModifiedProperties();
            }
        }

        /// <summary>Short reminder that this asset is a designer worksheet only.</summary>
        void DrawHelp()
        {
            EditorGUILayout.HelpBox(
                "Editor-only rebalance worksheet. Combat and the Orbit Menu do not read this.\n\n" +
                "Refresh Snapshot walks every family prefab and every armed MEGA, " +
                "sums all-gun DPS (Fire Power × Rate of Fire), then multiplies the " +
                "family / per-weapon bullet-bank Fire Power and Rate modifiers.\n\n" +
                "Sort by Bank DPS or Guns to find hulls with too many mounts. " +
                "Click Prefab, then edit that chassis.\n\n" +
                "Colors compare Bank DPS to that list's average " +
                $"(red ≥ {ShipFamilyBalanceRankDpsStats.StrongMul:0.#}× avg = too strong, " +
                $"blue ≤ {ShipFamilyBalanceRankDpsStats.WeakMul:0.#}× avg = too weak). " +
                "Regular and MEGA each have their own average.",
                MessageType.Info);
        }

        /// <summary>
        /// Refresh, sort dropdown, ascending/descending, and a name filter.
        /// Sort prefs persist across domain reload.
        /// </summary>
        void DrawToolbar(ShipFamilyBalanceRankCatalog catalog)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Balance Rank Catalog", EditorStyles.boldLabel);

            string stamp = string.IsNullOrEmpty(catalog.lastRefreshedLocal)
                ? "never"
                : catalog.lastRefreshedLocal;
            EditorGUILayout.LabelField(
                $"Last refresh: {stamp}   ({catalog.lastRefreshRowCount} rows)");

            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.82f, 1f, 1f);
            if (GUILayout.Button("Refresh Snapshot", GUILayout.Height(32)))
            {
                ShipFamilyBalanceRankBuilder.RefreshSnapshot(catalog);
                GUIUtility.ExitGUI();
            }

            GUI.backgroundColor = prev;

            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            _sortKey = (ShipFamilyBalanceRankSortKey)EditorGUILayout.EnumPopup("Sort by", _sortKey);
            _descending = EditorGUILayout.Toggle("Descending (strongest first)", _descending);
            _filter = EditorGUILayout.TextField("Filter (family / chassis / bank)", _filter);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PrefsSort, (int)_sortKey);
                EditorPrefs.SetBool(PrefsDesc, _descending);
            }

            DrawOutlierLegend();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Swatch row so red / blue are labeled before you scroll the lists.
        /// Averages themselves sit on each list header (regular vs MEGA).
        /// </summary>
        static void DrawOutlierLegend()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            DrawLegendSwatch(TooStrongTint, "TOO STRONG");
            DrawLegendSwatch(TooWeakTint, "TOO WEAK");
            DrawLegendSwatch(TypicalTint, "NEAR AVERAGE");
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>One colored chip plus its label in the toolbar legend.</summary>
        static void DrawLegendSwatch(Color tint, string label)
        {
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            GUILayout.Box(label, EditorStyles.miniButton, GUILayout.Height(20f));
            GUI.backgroundColor = prev;
        }

        /// <summary>
        /// Copies, filters, sorts, then draws a compact row for each hull.
        /// Outlier colors use the full list average (not the filtered subset)
        /// so hiding Space Excalibur does not re-center Astro Eagle as "typical."
        /// </summary>
        void DrawListSection(
            string title,
            List<ShipFamilyBalanceRankRow> source,
            ref Vector2 scroll)
        {
            ShipFamilyBalanceRankDpsStats pool = ShipFamilyBalanceRankDpsStats.Compute(source);
            var display = BuildDisplayList(source);
            int strong = 0;
            int weak = 0;
            CountOutliers(source, in pool, out strong, out weak);

            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                display.Count + " shown   •   avg Bank DPS " + pool.averageBankDps.ToString("0.#") +
                "   •   too strong ≥ " + pool.strongThreshold.ToString("0.#") +
                "   •   too weak ≤ " + pool.weakThreshold.ToString("0.#") +
                "   •   " + strong + " red / " + weak + " blue");

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(220f), GUILayout.MaxHeight(480f));
            for (int i = 0; i < display.Count; i++)
                DrawRow(i + 1, display[i], in pool);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Tallies outlier kinds on the full (unfiltered) snapshot list.</summary>
        static void CountOutliers(
            List<ShipFamilyBalanceRankRow> source,
            in ShipFamilyBalanceRankDpsStats pool,
            out int strong,
            out int weak)
        {
            strong = 0;
            weak = 0;
            if (source == null)
                return;
            for (int i = 0; i < source.Count; i++)
            {
                ShipFamilyBalanceRankRow row = source[i];
                if (row == null)
                    continue;
                ShipFamilyBalanceRankOutlierKind kind = pool.Classify(row.bankDps);
                if (kind == ShipFamilyBalanceRankOutlierKind.TooStrong)
                    strong++;
                else if (kind == ShipFamilyBalanceRankOutlierKind.TooWeak)
                    weak++;
            }
        }

        /// <summary>Filter by substring, then sort by the toolbar column.</summary>
        List<ShipFamilyBalanceRankRow> BuildDisplayList(List<ShipFamilyBalanceRankRow> source)
        {
            var list = new List<ShipFamilyBalanceRankRow>();
            if (source == null)
                return list;

            string needle = string.IsNullOrWhiteSpace(_filter) ? null : _filter.Trim();
            for (int i = 0; i < source.Count; i++)
            {
                ShipFamilyBalanceRankRow row = source[i];
                if (row == null)
                    continue;
                if (needle != null && !RowMatchesFilter(row, needle))
                    continue;
                list.Add(row);
            }

            list.Sort(CompareDisplay);
            return list;
        }

        /// <summary>Family id, chassis id, display name, or bank name contains the filter.</summary>
        static bool RowMatchesFilter(ShipFamilyBalanceRankRow row, string needle)
        {
            return Contains(row.familyId, needle)
                   || Contains(row.chassisId, needle)
                   || Contains(row.displayName, needle)
                   || Contains(row.bankName, needle);
        }

        static bool Contains(string hay, string needle) =>
            !string.IsNullOrEmpty(hay)
            && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Numeric columns use <see cref="ShipFamilyBalanceRankRow.GetNumericSortValue"/>.
        /// Name columns use ordinal string compare. Descending flips the sign.
        /// </summary>
        int CompareDisplay(ShipFamilyBalanceRankRow a, ShipFamilyBalanceRankRow b)
        {
            int cmp;
            switch (_sortKey)
            {
                case ShipFamilyBalanceRankSortKey.FamilyId:
                    cmp = string.CompareOrdinal(a?.familyId, b?.familyId);
                    break;
                case ShipFamilyBalanceRankSortKey.ChassisId:
                    cmp = string.CompareOrdinal(a?.chassisId, b?.chassisId);
                    break;
                case ShipFamilyBalanceRankSortKey.BankName:
                    cmp = string.CompareOrdinal(a?.bankName, b?.bankName);
                    break;
                default:
                    cmp = (a?.GetNumericSortValue(_sortKey) ?? 0f)
                        .CompareTo(b?.GetNumericSortValue(_sortKey) ?? 0f);
                    break;
            }

            if (cmp == 0)
                cmp = string.CompareOrdinal(a?.chassisId, b?.chassisId);
            return _descending ? -cmp : cmp;
        }

        /// <summary>
        /// One hull: rank, guns, both DPS numbers, vitals, then clickable links.
        /// Background tint is red / blue when Bank DPS is an outlier vs
        /// <paramref name="pool"/>, slate when it sits near the average.
        /// </summary>
        void DrawRow(int rank, ShipFamilyBalanceRankRow row, in ShipFamilyBalanceRankDpsStats pool)
        {
            if (row == null)
                return;

            ShipFamilyBalanceRankOutlierKind kind = pool.Classify(row.bankDps);
            float ratio = pool.RatioToAverage(row.bankDps);
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = TintForKind(kind);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // --- Identity + guns ---
            string title = $"#{rank}  L{row.treeLevel}  {row.displayName}";
            if (!string.Equals(row.displayName, row.chassisId, StringComparison.Ordinal))
                title += $"  ({row.chassisId})";
            title += "   " + FormatOutlierCaption(kind, ratio);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"{row.familyId}   guns {row.gunCount}   bank {row.bankName}   " +
                $"FP×{row.bankFirePowerMul:0.##}  RoF×{row.bankFireRateMul:0.##}  Spd×{row.bankBulletSpeedMul:0.##}");

            // --- Combat / vitals ---
            EditorGUILayout.LabelField(
                $"Hull DPS {row.hullDps:0.#}   Bank DPS {row.bankDps:0.#}  ({ratio:0.00}× avg {pool.averageBankDps:0.#})   " +
                $"FP {row.firePower:0.##}   RoF {row.fireRate:0.##}   Bullet {row.bulletSpeed:0.#}");
            EditorGUILayout.LabelField(
                $"HP {row.healthCap:0.#}  regen {row.healthRegen:0.##}   " +
                $"EN {row.energyCap:0.#}  regen {row.energyRegen:0.##}   " +
                $"Move {row.moveSpeed:0.##}   Turn {row.turnSpeed:0.##}");

            // --- Links ---
            // [UNITY] ObjectField lets you click the thumbnail / name to ping and
            // open the asset. Ping buttons do the same when the field is empty.
            EditorGUILayout.BeginHorizontal();
            // ObjectField click pings / opens the asset. Changing the reference
            // only edits this snapshot — Refresh Snapshot overwrites it.
            if (row.family != null)
                EditorGUILayout.ObjectField(row.family, typeof(ShipFamilyDefinition), false);
            if (row.megaCatalog != null)
                EditorGUILayout.ObjectField(row.megaCatalog, typeof(MegaShipCatalog), false);
            EditorGUILayout.ObjectField(row.prefab, typeof(GameObject), false);

            if (GUILayout.Button("Ping Prefab", GUILayout.Width(92)))
                Ping(row.prefab != null ? (UnityEngine.Object)row.prefab : row.family);
            if (row.family != null && GUILayout.Button("Family", GUILayout.Width(60)))
                Ping(row.family);
            if (row.megaCatalog != null && GUILayout.Button("Catalog", GUILayout.Width(64)))
                Ping(row.megaCatalog);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            GUI.backgroundColor = prevBg;
        }

        /// <summary>HelpBox background for this outlier kind.</summary>
        static Color TintForKind(ShipFamilyBalanceRankOutlierKind kind)
        {
            switch (kind)
            {
                case ShipFamilyBalanceRankOutlierKind.TooStrong: return TooStrongTint;
                case ShipFamilyBalanceRankOutlierKind.TooWeak: return TooWeakTint;
                default: return TypicalTint;
            }
        }

        /// <summary>
        /// Short badge on the row title so you do not have to read the tint
        /// alone. Typical rows stay quiet (just the × average).
        /// </summary>
        static string FormatOutlierCaption(ShipFamilyBalanceRankOutlierKind kind, float ratio)
        {
            switch (kind)
            {
                case ShipFamilyBalanceRankOutlierKind.TooStrong:
                    return "TOO STRONG  " + ratio.ToString("0.00") + "× avg";
                case ShipFamilyBalanceRankOutlierKind.TooWeak:
                    return "TOO WEAK  " + ratio.ToString("0.00") + "× avg";
                default:
                    return ratio.ToString("0.00") + "× avg";
            }
        }

        /// <summary>Selects and frames the asset in the Project window.</summary>
        static void Ping(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        /// <summary>
        /// Finds or creates the worksheet asset, selects it, and pings it.
        /// Menu: TitanOrbit → Ship Families → Open Balance Rank Catalog.
        /// </summary>
        [MenuItem("TitanOrbit/Ship Families/Open Balance Rank Catalog")]
        public static void OpenCatalogMenu()
        {
            ShipFamilyBalanceRankCatalog catalog = ShipFamilyBalanceRankBuilder.FindOrCreateCatalog();
            if (catalog == null)
            {
                EditorUtility.DisplayDialog(
                    "Balance Rank Catalog",
                    "Could not find or create Assets/Prefabs/Ships/ShipFamilyBalanceRankCatalog.asset.",
                    "OK");
                return;
            }

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }
    }
}
#endif

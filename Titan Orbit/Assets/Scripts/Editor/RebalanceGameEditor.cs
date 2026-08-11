using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Hub Inspector for <see cref="RebalanceGame"/>: auto-find assets, export Cursor
    /// rebalance prompts, run the local seed pipeline, and refresh in-asset review
    /// (aggregates / outliers / economy) — primary balance UX (CSV menus are secondary).
    /// </summary>
    [CustomEditor(typeof(RebalanceGame))]
    public class RebalanceGameEditor : UnityEditor.Editor
    {
        bool _showAggregates = true;
        bool _showOutliers = true;
        bool _showEconomy = true;
        Vector2 _outlierScroll;
        int _outlierPreviewCount = 30;

        /// <summary>Draws hub tools + cached review tables above the default property list.</summary>
        public override void OnInspectorGUI()
        {
            var hub = (RebalanceGame)target;
            if (hub == null)
                return;

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "RebalanceGame is the balance hub.\n" +
                "1) Auto-Find References → 2) Edit balancing requests → 3) Export For Cursor (AI updates assets) → " +
                "4) Apply Local Pipeline (optional) → 5) Refresh Review to see outliers / economy here.",
                MessageType.Info);

            // --- Primary actions ---
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-Find References", GUILayout.Height(28)))
            {
                RebalanceGameReviewBuilder.AutoFindReferences(hub);
                EditorUtility.DisplayDialog(
                    "RebalanceGame",
                    $"Linked {hub.shipFamilies?.Count ?? 0} ship families + Resources balance assets.\n" +
                    "Default balance requests seeded if the list was empty.",
                    "OK");
            }

            if (GUILayout.Button("Refresh Review", GUILayout.Height(28)))
            {
                if (RebalanceGameReviewBuilder.RefreshReview(hub, out string err))
                {
                    serializedObject.Update();
                    EditorUtility.DisplayDialog(
                        "Review refreshed",
                        $"{hub.lastChassisCount} chassis · {hub.outliers?.Count ?? 0} outliers.\n" +
                        "Scroll down for tables (also under Cached review).",
                        "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Review failed", err ?? "Unknown error.", "OK");
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Export For Cursor", GUILayout.Height(28)))
            {
                hub.EnsureDefaultBalanceRequests();
                string path = RebalanceGameReviewBuilder.ExportCursorPrompt(hub);
                EditorUtility.RevealInFinder(path);
                EditorUtility.DisplayDialog(
                    "Cursor prompt ready",
                    "Prompt copied to clipboard and written to:\n" + path +
                    "\n\nPaste into Cursor and ask agents to rebalance the linked assets from the requests.",
                    "OK");
            }

            if (GUILayout.Button("Apply Local Pipeline", GUILayout.Height(28)))
            {
                if (EditorUtility.DisplayDialog(
                        "Apply Local Pipeline",
                        "Reset PartCalcProfileSet from code seeds, rebalance all ship families, " +
                        "and refresh power scores (no upgrade-tree resort)?\n\n" +
                        "Use after Cursor edited seeds, or for a local recalculate.",
                        "Apply",
                        "Cancel"))
                {
                    int n = GameBalanceApplyPipelineMenu.RunPipeline(resortTrees: false, tuneAsteroids: false);
                    RebalanceGameReviewBuilder.RefreshReview(hub, out _);
                    serializedObject.Update();
                    EditorUtility.DisplayDialog(
                        "Pipeline done",
                        $"Updated {n} families. Review refreshed on this asset.",
                        "OK");
                }
            }

            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Apply Pipeline + Resort Trees + Refresh", GUILayout.Height(24)))
            {
                if (EditorUtility.DisplayDialog(
                        "Apply + Resort",
                        "Full local pipeline including upgrade-tree resort by power score?",
                        "Apply + Resort",
                        "Cancel"))
                {
                    int n = GameBalanceApplyPipelineMenu.RunPipeline(resortTrees: true, tuneAsteroids: false);
                    RebalanceGameReviewBuilder.RefreshReview(hub, out _);
                    serializedObject.Update();
                    EditorUtility.DisplayDialog("Done", $"{n} families updated + review refreshed.", "OK");
                }
            }

            EditorGUILayout.Space(6);
            DrawReviewPanels(hub);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Asset references & requests", EditorStyles.boldLabel);
            DrawDefaultInspector();
        }

        /// <summary>Draws cached economy / aggregate / outlier tables from the last Refresh Review.</summary>
        void DrawReviewPanels(RebalanceGame hub)
        {
            EditorGUILayout.LabelField("Cached review", EditorStyles.boldLabel);
            if (string.IsNullOrEmpty(hub.lastReviewUtc))
            {
                EditorGUILayout.HelpBox("No review yet — click Refresh Review.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"Last review (UTC): {hub.lastReviewUtc} · chassis {hub.lastChassisCount}");
            if (!string.IsNullOrEmpty(hub.lastReviewSummary))
            {
                EditorGUILayout.LabelField("Summary", EditorStyles.miniBoldLabel);
                EditorGUILayout.HelpBox(hub.lastReviewSummary, MessageType.None);
            }

            // --- Economy ---
            _showEconomy = EditorGUILayout.Foldout(_showEconomy, $"Economy checks ({hub.economyChecks?.Count ?? 0})", true);
            if (_showEconomy && hub.economyChecks != null)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < hub.economyChecks.Count; i++)
                {
                    var c = hub.economyChecks[i];
                    MessageType mt = c.status == "FAIL"
                        ? MessageType.Error
                        : (c.status == "WARN" ? MessageType.Warning : MessageType.None);
                    EditorGUILayout.HelpBox($"[{c.status}] {c.checkId} = {c.value}  ({c.targetOrNote})", mt);
                }

                EditorGUI.indentLevel--;
            }

            // --- Aggregates ---
            _showAggregates = EditorGUILayout.Foldout(
                _showAggregates, $"Fleet aggregates ({hub.fleetAggregates?.Count ?? 0})", true);
            if (_showAggregates && hub.fleetAggregates != null)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Metric", "Min / P10 / Median / Mean / P90 / Max", EditorStyles.miniLabel);
                for (int i = 0; i < hub.fleetAggregates.Count; i++)
                {
                    var a = hub.fleetAggregates[i];
                    EditorGUILayout.LabelField(
                        a.metricName,
                        $"{a.min:0.##} / {a.p10:0.##} / {a.median:0.##} / {a.mean:0.##} / {a.p90:0.##} / {a.max:0.##}");
                }

                EditorGUILayout.EndVertical();
            }

            // --- Outliers ---
            _showOutliers = EditorGUILayout.Foldout(_showOutliers, $"Outliers ({hub.outliers?.Count ?? 0})", true);
            if (_showOutliers && hub.outliers != null && hub.outliers.Count > 0)
            {
                _outlierPreviewCount = EditorGUILayout.IntSlider("Show top N", _outlierPreviewCount, 5, 80);
                _outlierScroll = EditorGUILayout.BeginScrollView(_outlierScroll, GUILayout.MaxHeight(320));
                int n = Mathf.Min(_outlierPreviewCount, hub.outliers.Count);
                for (int i = 0; i < n; i++)
                {
                    var o = hub.outliers[i];
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField(
                        $"#{i + 1}  sev {o.severity:0.##}  {o.familyId}/{o.chassisId}  L{o.shipLevel}",
                        EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        $"W{o.wings} E{o.engines} T{o.thrusters} guns{o.weapons}  " +
                        $"move {o.moveSpeed:0.##} dps {o.dps:0.##} gems {o.gemCap:0.##} people {o.peopleCap:0.##} " +
                        $"pwr {o.powerScore:0.##}");
                    EditorGUILayout.LabelField("Flags", o.flags);
                    EditorGUILayout.LabelField("Fix", o.fixClass);
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Optional: Export CSV copies to tools/balance/"))
            {
                GameBalanceReportMenus.ExportFleetCompositionReportSilent();
                GameBalanceReportMenus.ExportShipOutlierReportSilent();
                GameBalanceReportMenus.ExportEconomyCrossCheckSilent();
                EditorUtility.DisplayDialog(
                    "CSV exported",
                    "Wrote optional CSVs under Titan Orbit/tools/balance/.\n" +
                    "Primary review remains on this asset.",
                    "OK");
            }
        }
    }

    /// <summary>Menu helpers to create / open the shared RebalanceGame hub under Resources.</summary>
    public static class RebalanceGameMenu
    {
        const string ResourcesPath = "Assets/Resources/RebalanceGame.asset";

        /// <summary>Creates Resources/RebalanceGame.asset if missing, then selects it.</summary>
        [MenuItem("TitanOrbit/Balance/Open RebalanceGame Hub")]
        public static void OpenOrCreateHub()
        {
            var hub = AssetDatabase.LoadAssetAtPath<RebalanceGame>(ResourcesPath);
            if (hub == null)
            {
                hub = ScriptableObject.CreateInstance<RebalanceGame>();
                hub.EnsureDefaultBalanceRequests();
                AssetDatabase.CreateAsset(hub, ResourcesPath);
                AssetDatabase.SaveAssets();
                RebalanceGameReviewBuilder.AutoFindReferences(hub);
            }

            Selection.activeObject = hub;
            EditorGUIUtility.PingObject(hub);
        }
    }
}

using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;
using TitanOrbit.Entities;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Custom inspector for ShipFamilyStatsPreview that shows a compact, read-only summary
    /// of the aggregated ability stats from child components.
    /// </summary>
    [CustomEditor(typeof(ShipFamilyStatsPreview))]
    public class ShipFamilyStatsPreviewEditor : UnityEditor.Editor
    {
        private void OnEnable()
        {
            ShipFamilyStatsPreviewLiveRefresh.RegisterInspectorTarget(target as ShipFamilyStatsPreview);
        }

        private void OnDisable()
        {
            ShipFamilyStatsPreviewLiveRefresh.UnregisterInspectorTarget(target as ShipFamilyStatsPreview);
        }

        public override void OnInspectorGUI()
        {
            var preview = (ShipFamilyStatsPreview)target;

            // Config section
            EditorGUILayout.LabelField("Ship Family Config", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var familyAsset = (TitanOrbit.Data.ShipFamilyDefinition)EditorGUILayout.ObjectField(
                "Ship Family",
                serializedObject.FindProperty("shipFamily").objectReferenceValue,
                typeof(TitanOrbit.Data.ShipFamilyDefinition),
                false);

            string familyOverride = EditorGUILayout.TextField(
                new GUIContent("Family Id Override", "Optional: override the family id used to match child names."),
                serializedObject.FindProperty("familyIdOverride").stringValue);

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.FindProperty("shipFamily").objectReferenceValue = familyAsset;
                serializedObject.FindProperty("familyIdOverride").stringValue = familyOverride;
                serializedObject.ApplyModifiedProperties();

                if (preview != null)
                {
                    preview.RecalculateFromChildren();
                }
            }

            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Stats use the assigned Ship Family: each child Family_ComponentId is resolved from the Components list on that definition. " +
                "If the ShipFamilyDefinition inspector is open too, this preview refreshes when you change that asset; otherwise re-select this prefab or tweak Ship Family to recalc.",
                MessageType.None);

            if (preview == null || preview.TotalStats.Equals(default(TitanOrbit.Data.ShipComponentAbilityStats)))
            {
                EditorGUILayout.HelpBox(
                    "No stats found yet. Assign a ShipFamilyDefinition and ensure child names follow 'Family_ComponentId' (e.g. AstroEagle_Cockpit). " +
                    "Non-weapons: most stats scale by average scale (x+y+z)/3. Engines and thrusters use authored move speed and acceleration cap; thrusters also use turn speed — none scaled by part size. " +
                    "Weapons: fire power scales by average(x,y); fire rate by 1/z (smaller z = faster); bullet speed is not scaled by part size.",
                    MessageType.Info);
            }

            // Force a recalc in edit mode so the UI stays fresh while renaming parts.
            if (!Application.isPlaying && preview != null)
            {
                preview.RecalculateFromChildren();
            }

            var total = preview != null ? preview.TotalStats : default;

            EditorGUILayout.LabelField("Summed Ability Stats", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Offense", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Fire Power", total.firePower);
                EditorGUILayout.FloatField("Fire Power / Level", total.firePowerPerLevel);
                EditorGUILayout.FloatField("Bullet Speed", total.bulletSpeed);
                EditorGUILayout.FloatField("Bullet Speed / Level", total.bulletSpeedPerLevel);
                EditorGUILayout.FloatField("Fire Rate (shots/s)", total.fireRate);
                EditorGUILayout.FloatField("Fire Rate / Level", total.fireRatePerLevel);
                EditorGUILayout.FloatField("Ramming Power", total.rammingPower);
                EditorGUILayout.FloatField("Ramming Power / Level", total.rammingPowerPerLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Health", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Health Cap", total.healthCap);
                EditorGUILayout.FloatField("Health Cap / Level", total.healthCapPerLevel);
                EditorGUILayout.FloatField("Health Regen", total.healthRegen);
                EditorGUILayout.FloatField("Health Regen / Level", total.healthRegenPerLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Energy", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Energy Cap", total.energyCap);
                EditorGUILayout.FloatField("Energy Cap / Level", total.energyCapPerLevel);
                EditorGUILayout.FloatField("Energy Regen", total.energyRegen);
                EditorGUILayout.FloatField("Energy Regen / Level", total.energyRegenPerLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Movement", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField(
                    new GUIContent("Acceleration Cap (sum, all parts)", "Sum of every matched part’s Acceleration Cap (definition units)."),
                    total.accelerationCap);
                EditorGUILayout.FloatField(
                    new GUIContent("Acceleration Cap / Level (sum, all parts)", "Sum of per-level acceleration terms."),
                    total.accelerationCapPerLevel);
                EditorGUILayout.FloatField(
                    new GUIContent("Move Speed (aggregated)", "Shared engine/thruster pool: best base move speed once + half the sum of other parts' moveSpeedPerLevel."),
                    total.moveSpeed);
                EditorGUILayout.FloatField(
                    new GUIContent("Move Speed / Level (primary)", "Primary propulsion part's moveSpeedPerLevel after aggregation."),
                    total.moveSpeedPerLevel);
                if (preview != null)
                {
                    EditorGUILayout.LabelField("Propulsion (engines + thrusters)", EditorStyles.miniBoldLabel);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Acceleration Cap (sum)",
                            "Sum of Acceleration Cap on all engine/thruster parts — matches Starship thrust stacking at ship level 1 (before mass divides force)."),
                        preview.PreviewSumPropulsionAcceleration);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Acceleration Cap / Level (sum)",
                            "Sum of per-level acceleration on engine/thruster parts."),
                        preview.PreviewSumPropulsionAccelerationPerLevel);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Primary propulsion Move Speed",
                            "Best engine/thruster base move speed — counted once toward top speed cap."),
                        preview.PreviewPrimaryThrusterMoveSpeed);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Extra propulsion Move Speed",
                            "Half the sum of moveSpeedPerLevel from every other engine/thruster (not their full moveSpeed)."),
                        preview.PreviewExtraThrusterMoveSpeed);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Top speed cap",
                            "Primary move speed + extra propulsion move speed — matches in-game max speed / speedometer."),
                        preview.PreviewTopSpeedMoveSpeed);
                }
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Turn Speed",
                        "Sum of all matched parts (thrusters, wings, fins, etc.). Definition units; Starship converts to °/s when rotating."),
                    total.turnSpeed);
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Turn Speed / Level",
                        "Sum of per-level turn terms. Starship applies ship-level mobility scaling when rotating."),
                    total.turnSpeedPerLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Capacity", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Max Gems", total.maxGems);
                EditorGUILayout.FloatField("Max Gems / Level", total.maxGemsPerLevel);
                EditorGUILayout.FloatField("Max People", total.maxPeople);
                EditorGUILayout.FloatField("Max People / Level", total.maxPeoplePerLevel);
            }

            EditorGUILayout.Space();

            if (preview != null && preview.MatchedComponentIds != null && preview.MatchedComponentIds.Count > 0)
            {
                EditorGUILayout.LabelField("Matched Components (stats scaled by transform)", EditorStyles.boldLabel);
                var ids = preview.MatchedComponentIds;
                var scales = preview.MatchedScaleFactors;
                var perStats = preview.PerComponentStats;

                bool showPerComponent = EditorGUILayout.Foldout(true, "Per-Component Ability Breakdown");
                EditorGUI.indentLevel++;
                for (int i = 0; i < ids.Count; i++)
                {
                    string label = ids[i];
                    bool isWeapon = TitanOrbit.Data.ShipComponentAbilityStats.IsWeaponComponent(label);
                    bool isPropulsion = TitanOrbit.Data.ShipComponentAbilityStats.IsPropulsionComponent(label);
                    if (scales != null && i < scales.Count && scales[i] != 1f)
                        label += " (scale " + scales[i].ToString("F2") + "×)";
                    if (isWeapon)
                        label += " [weapon: xy=power, 1/z=rate; offense + energy]";
                    if (isPropulsion)
                        label += " [engine/thruster: one base move speed + half sum of others' moveSpeedPerLevel; accel sums]";
                    EditorGUILayout.LabelField("- " + label);

                    if (showPerComponent && perStats != null && i < perStats.Count)
                    {
                        var s = perStats[i];
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.LabelField("Offense", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Fire Power", s.firePower);
                            EditorGUILayout.FloatField("  Fire Power / Level", s.firePowerPerLevel);
                            EditorGUILayout.FloatField("  Bullet Speed", s.bulletSpeed);
                            EditorGUILayout.FloatField("  Bullet Speed / Level", s.bulletSpeedPerLevel);
                            EditorGUILayout.FloatField("  Fire Rate (shots/s)", s.fireRate);
                            EditorGUILayout.FloatField("  Fire Rate / Level", s.fireRatePerLevel);
                            EditorGUILayout.FloatField("  Ramming Power", s.rammingPower);
                            EditorGUILayout.FloatField("  Ramming Power / Level", s.rammingPowerPerLevel);

                            EditorGUILayout.LabelField("Health", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Health Cap", s.healthCap);
                            EditorGUILayout.FloatField("  Health Cap / Level", s.healthCapPerLevel);
                            EditorGUILayout.FloatField("  Health Regen", s.healthRegen);
                            EditorGUILayout.FloatField("  Health Regen / Level", s.healthRegenPerLevel);

                            EditorGUILayout.LabelField("Energy", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Energy Cap", s.energyCap);
                            EditorGUILayout.FloatField("  Energy Cap / Level", s.energyCapPerLevel);
                            EditorGUILayout.FloatField("  Energy Regen", s.energyRegen);
                            EditorGUILayout.FloatField("  Energy Regen / Level", s.energyRegenPerLevel);

                            EditorGUILayout.LabelField("Movement", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField(
                                isPropulsion
                                    ? new GUIContent("  Move Speed", "Authoritative for engines/thrusters; not multiplied by transform scale. Contributes to top-speed cap (max part).")
                                    : new GUIContent("  Move Speed"),
                                s.moveSpeed);
                            EditorGUILayout.FloatField(
                                isPropulsion
                                    ? new GUIContent("  Move Speed / Level", "Authoritative for engines/thrusters; not multiplied by transform scale.")
                                    : new GUIContent("  Move Speed / Level"),
                                s.moveSpeedPerLevel);
                            EditorGUILayout.FloatField(
                                isPropulsion
                                    ? new GUIContent(
                                        "  Acceleration Cap",
                                        "Summed across all engines and thrusters for thrust at runtime (ship level 1 base + per-level terms).")
                                    : new GUIContent("  Acceleration Cap"),
                                s.accelerationCap);
                            EditorGUILayout.FloatField(
                                isPropulsion
                                    ? new GUIContent(
                                        "  Acceleration Cap / Level",
                                        "Added per ship level for each engine/thruster; stacked with base acceleration cap.")
                                    : new GUIContent("  Acceleration Cap / Level"),
                                s.accelerationCapPerLevel);
                            EditorGUILayout.FloatField(
                                new GUIContent(
                                    "  Turn Speed",
                                    "Definition units. Starship converts to °/s only when rotating."),
                                s.turnSpeed);
                            EditorGUILayout.FloatField(
                                new GUIContent(
                                    "  Turn Speed / Level",
                                    "Definition units per ship level. Starship converts to °/s only when rotating."),
                                s.turnSpeedPerLevel);

                            EditorGUILayout.LabelField("Capacity", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Max Gems", s.maxGems);
                            EditorGUILayout.FloatField("  Max Gems / Level", s.maxGemsPerLevel);
                            EditorGUILayout.FloatField("  Max People", s.maxPeople);
                            EditorGUILayout.FloatField("  Max People / Level", s.maxPeoplePerLevel);
                            EditorGUI.indentLevel--;
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
        }
    }
}


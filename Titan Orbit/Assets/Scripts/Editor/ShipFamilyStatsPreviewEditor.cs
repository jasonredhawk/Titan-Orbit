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
                    "Non-weapons: most stats scale by average scale (x+y+z)/3. Engines and thrusters use authored move speed and acceleration cap; thrusters also use turn speed (with Tail/Fin) — none scaled by part size. Engines own Energy Cap/Regen. " +
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
                EditorGUILayout.FloatField("Fire Power / Extra Level", total.firePowerPerExtraLevel);
                EditorGUILayout.FloatField("Bullet Speed", total.bulletSpeed);
                EditorGUILayout.FloatField("Bullet Speed / Extra Level", total.bulletSpeedPerExtraLevel);
                EditorGUILayout.FloatField("Bullet Range", total.bulletRange);
                EditorGUILayout.FloatField("Bullet Range / Extra Level", total.bulletRangePerExtraLevel);
                EditorGUILayout.FloatField("Fire Rate (shots/s)", total.fireRate);
                EditorGUILayout.FloatField("Fire Rate / Extra Level", total.fireRatePerExtraLevel);
                EditorGUILayout.FloatField("Ramming Power", total.rammingPower);
                EditorGUILayout.FloatField("Ramming Power / Extra Level", total.rammingPowerPerExtraLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Health", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Health Cap", total.healthCap);
                EditorGUILayout.FloatField("Health Cap / Extra Level", total.healthCapPerExtraLevel);
                EditorGUILayout.FloatField("Health Regen", total.healthRegen);
                EditorGUILayout.FloatField("Health Regen / Extra Level", total.healthRegenPerExtraLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Energy", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Energy Cap", total.energyCap);
                EditorGUILayout.FloatField("Energy Cap / Extra Level", total.energyCapPerExtraLevel);
                EditorGUILayout.FloatField("Energy Regen", total.energyRegen);
                EditorGUILayout.FloatField("Energy Regen / Extra Level", total.energyRegenPerExtraLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Movement", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Acceleration Cap (aggregated)",
                        "Primary Accel only. Extras raise Extra Level via (N−1): Base + PerExtra × ((shipLv−1)+ability+(N−1))."),
                    total.accelerationCap);
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Acceleration Cap / Extra Level (aggregated)",
                        "Primary Accel PerExtraLevel step (copied through aggregation for tooltips)."),
                    total.accelerationCapPerExtraLevel);
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Move Speed (aggregated)",
                        "Primary Move only. Extras raise Extra Level via component count (same pool for engines+thrusters)."),
                    total.moveSpeed);
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Move Speed / Extra Level (aggregated)",
                        "Primary Move PerExtraLevel step (copied through aggregation for tooltips)."),
                    total.moveSpeedPerExtraLevel);
                if (preview != null)
                {
                    EditorGUILayout.LabelField("Propulsion (engines + thrusters)", EditorStyles.miniBoldLabel);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Acceleration Cap (stacked)",
                            "Primary Accel + PerExtra × ((shipLv−1)+(N−1)) — matches Extra Level flight math."),
                        preview.PreviewSumPropulsionAcceleration);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Acceleration Cap / Extra Level (stacked)",
                            "Primary Accel PerExtraLevel (one step per Extra Level)."),
                        preview.PreviewSumPropulsionAccelerationPerLevel);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Primary propulsion Move Speed",
                            "Best engine/thruster base move speed — Base in Extra Level formula."),
                        preview.PreviewPrimaryThrusterMoveSpeed);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Extra propulsion Move Speed",
                            "PerExtraLevel × (propulsionCount − 1) — count contribution from extras only."),
                        preview.PreviewExtraThrusterMoveSpeed);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Top speed cap",
                            "Primary Move Extra Level evaluate — matches in-game max speed / speedometer."),
                        preview.PreviewTopSpeedMoveSpeed);
                }
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Turn Speed",
                        "Sum of all matched parts (thrusters, wings, fins, etc.). Definition units; Starship converts to ┬░/s when rotating."),
                    total.turnSpeed);
                EditorGUILayout.FloatField(
                    new GUIContent(
                        "Turn Speed / Extra Level",
                        "Sum of per-level turn terms. Starship applies ship-level mobility scaling when rotating."),
                    total.turnSpeedPerExtraLevel);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Capacity", EditorStyles.miniBoldLabel);
                EditorGUILayout.FloatField("Max Gems", total.maxGems);
                EditorGUILayout.FloatField("Max Gems / Extra Level", total.maxGemsPerExtraLevel);
                EditorGUILayout.FloatField("Max People", total.maxPeople);
                EditorGUILayout.FloatField("Max People / Extra Level", total.maxPeoplePerExtraLevel);

                if (preview != null)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Mass", EditorStyles.miniBoldLabel);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Component Mass",
                            "Sum of part scale factors on this prefab ΓÇö matches speedometer MASS (before hullMassScale and gems)."),
                        preview.PreviewComponentMass);
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "HUD Hull Mass (est.)",
                            "Component mass ├ù 0.7 ΓÇö typical movement mass at level 1 with empty cargo."),
                        preview.PreviewHudHullMass);
                }
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
                        label += " (scale " + scales[i].ToString("F2") + "├ù)";
                    if (isWeapon)
                        label += " [weapon: xy=power, 1/z=rate; offense only]";
                    if (isPropulsion)
                        label += " [engine/thruster: primary Base; extras add Extra Level via count]";
                    EditorGUILayout.LabelField("- " + label);

                    if (showPerComponent && perStats != null && i < perStats.Count)
                    {
                        var s = perStats[i];
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.LabelField("Offense", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Fire Power", s.firePower);
                            EditorGUILayout.FloatField("  Fire Power / Extra Level", s.firePowerPerExtraLevel);
                            EditorGUILayout.FloatField("  Bullet Speed", s.bulletSpeed);
                            EditorGUILayout.FloatField("  Bullet Speed / Extra Level", s.bulletSpeedPerExtraLevel);
                            EditorGUILayout.FloatField("  Bullet Range", s.bulletRange);
                            EditorGUILayout.FloatField("  Bullet Range / Extra Level", s.bulletRangePerExtraLevel);
                            EditorGUILayout.FloatField("  Fire Rate (shots/s)", s.fireRate);
                            EditorGUILayout.FloatField("  Fire Rate / Extra Level", s.fireRatePerExtraLevel);
                            EditorGUILayout.FloatField("  Ramming Power", s.rammingPower);
                            EditorGUILayout.FloatField("  Ramming Power / Extra Level", s.rammingPowerPerExtraLevel);

                            EditorGUILayout.LabelField("Health", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Health Cap", s.healthCap);
                            EditorGUILayout.FloatField("  Health Cap / Extra Level", s.healthCapPerExtraLevel);
                            EditorGUILayout.FloatField("  Health Regen", s.healthRegen);
                            EditorGUILayout.FloatField("  Health Regen / Extra Level", s.healthRegenPerExtraLevel);

                            EditorGUILayout.LabelField("Energy", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Energy Cap", s.energyCap);
                            EditorGUILayout.FloatField("  Energy Cap / Extra Level", s.energyCapPerExtraLevel);
                            EditorGUILayout.FloatField("  Energy Regen", s.energyRegen);
                            EditorGUILayout.FloatField("  Energy Regen / Extra Level", s.energyRegenPerExtraLevel);

                            EditorGUILayout.LabelField("Movement", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField(
                                isPropulsion
                                    ? new GUIContent("  Move Speed", "Authoritative for engines/thrusters; not multiplied by transform scale. Contributes to top-speed cap (max part).")
                                    : new GUIContent("  Move Speed"),
                                s.moveSpeed);
                            EditorGUILayout.FloatField(
                                isPropulsion
                                    ? new GUIContent("  Move Speed / Extra Level", "Authoritative for engines/thrusters; not multiplied by transform scale.")
                                    : new GUIContent("  Move Speed / Extra Level"),
                                s.moveSpeedPerExtraLevel);
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
                                        "  Acceleration Cap / Extra Level",
                                        "Added per ship level for each engine/thruster; stacked with base acceleration cap.")
                                    : new GUIContent("  Acceleration Cap / Extra Level"),
                                s.accelerationCapPerExtraLevel);
                            EditorGUILayout.FloatField(
                                new GUIContent(
                                    "  Turn Speed",
                                    "Definition units. Starship converts to ┬░/s only when rotating."),
                                s.turnSpeed);
                            EditorGUILayout.FloatField(
                                new GUIContent(
                                    "  Turn Speed / Extra Level",
                                    "Definition units per ship level. Starship converts to ┬░/s only when rotating."),
                                s.turnSpeedPerExtraLevel);

                            EditorGUILayout.LabelField("Capacity", EditorStyles.miniBoldLabel);
                            EditorGUILayout.FloatField("  Max Gems", s.maxGems);
                            EditorGUILayout.FloatField("  Max Gems / Extra Level", s.maxGemsPerExtraLevel);
                            EditorGUILayout.FloatField("  Max People", s.maxPeople);
                            EditorGUILayout.FloatField("  Max People / Extra Level", s.maxPeoplePerExtraLevel);
                            EditorGUI.indentLevel--;
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
        }
    }
}


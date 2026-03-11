using UnityEditor;
using UnityEngine;
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

            if (preview == null || preview.TotalStats.Equals(default(TitanOrbit.Data.ShipComponentAbilityStats)))
            {
                EditorGUILayout.HelpBox(
                    "No stats found yet. Assign a ShipFamilyDefinition and ensure child names follow 'Family_ComponentId' (e.g. AstroEagle_Cockpit). " +
                    "Non-weapons: stats scale by x×y×z. Weapons: fire power & bullet scale by x×y (size); fire rate scales by 1/z (smaller z = faster).",
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
                EditorGUILayout.FloatField("Move Speed", total.moveSpeed);
                EditorGUILayout.FloatField("Move Speed / Level", total.moveSpeedPerLevel);
                EditorGUILayout.FloatField("Turn Speed", total.turnSpeed);
                EditorGUILayout.FloatField("Turn Speed / Level", total.turnSpeedPerLevel);

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
                    if (scales != null && i < scales.Count && scales[i] != 1f)
                        label += " (scale " + scales[i].ToString("F2") + "×)";
                    if (isWeapon)
                        label += " [weapon: xy=power, 1/z=rate]";
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
                            EditorGUILayout.FloatField("  Move Speed", s.moveSpeed);
                            EditorGUILayout.FloatField("  Move Speed / Level", s.moveSpeedPerLevel);
                            EditorGUILayout.FloatField("  Turn Speed", s.turnSpeed);
                            EditorGUILayout.FloatField("  Turn Speed / Level", s.turnSpeedPerLevel);

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


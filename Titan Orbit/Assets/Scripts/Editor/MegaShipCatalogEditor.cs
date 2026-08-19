#if UNITY_EDITOR
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Hull-pool row tint: muted cyan when firepower is 0 (unarmed — stays in the
    /// catalog, not rolled into matches), orange when a non-firepower stat is still 0.
    /// Cyan wins when both apply. Theatrical previews and in-game team colors are unchanged.
    /// </summary>
    [CustomPropertyDrawer(typeof(MegaShipCatalogEntry))]
    public class MegaShipCatalogEntryDrawer : PropertyDrawer
    {
        /// <summary>Missing non-firepower stats (existing orange).</summary>
        static readonly Color MissingNonFirepowerColor = new Color(1f, 0.55f, 0.35f, 1f);

        /// <summary>Raw summed firepower is 0 — unfinished / unarmed hull.</summary>
        static readonly Color UnarmedFirepowerColor = new Color(0.40f, 0.78f, 0.94f, 1f);

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, true);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var firePower = property.FindPropertyRelative("summedStats.firePower");
            var nameProp = property.FindPropertyRelative("displayName");
            string name = nameProp != null ? nameProp.stringValue : label.text;
            var content = new GUIContent(string.IsNullOrEmpty(name) ? label.text : name);

            bool unarmed = firePower != null && firePower.floatValue <= 0.01f;
            bool hasMissing = !unarmed && HasMissingNonFirepower(property);

            // --- Precedence: cyan (0 firepower) > orange (missing other stats) > default ---
            if (unarmed)
                content.tooltip = "Firepower is 0. This hull stays in the catalog but is not rolled into matches.";
            else if (hasMissing)
                content.tooltip = "One or more non-firepower stats are 0 in the catalog. In-game they use Default Stats, then Minimum Stats.";

            Color prev = GUI.color;
            if (unarmed)
                GUI.color = UnarmedFirepowerColor;
            else if (hasMissing)
                GUI.color = MissingNonFirepowerColor;
            EditorGUI.PropertyField(position, property, content, true);
            GUI.color = prev;
        }

        static bool HasMissingNonFirepower(SerializedProperty entry)
        {
            var summed = entry.FindPropertyRelative("summedStats");
            if (summed == null)
                return false;

            return FloatAtOrBelow(summed, "bulletSpeed")
                   || FloatAtOrBelow(summed, "bulletRange")
                   || FloatAtOrBelow(summed, "fireRate")
                   || FloatAtOrBelow(summed, "rammingPower")
                   || FloatAtOrBelow(summed, "healthCap")
                   || FloatAtOrBelow(summed, "healthRegen")
                   || FloatAtOrBelow(summed, "energyCap")
                   || FloatAtOrBelow(summed, "energyRegen")
                   || FloatAtOrBelow(summed, "moveSpeed")
                   || FloatAtOrBelow(summed, "accelerationCap")
                   || FloatAtOrBelow(summed, "turnSpeed")
                   || FloatAtOrBelow(summed, "maxPeople")
                   || FloatAtOrBelow(summed, "weaponRotationSpeed");
        }

        static bool FloatAtOrBelow(SerializedProperty summed, string field)
        {
            var prop = summed.FindPropertyRelative(field);
            return prop != null && prop.floatValue <= 0.01f;
        }
    }

    /// <summary>
    /// Inspector buttons to refresh the unique MEGA component library and rewrite hull sums.
    /// </summary>
    [CustomEditor(typeof(MegaShipCatalog))]
    public class MegaShipCatalogEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var catalog = target as MegaShipCatalog;
            if (catalog == null)
                return;

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Unique component library", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unique Components is one row per part name (Armor1, TurretBarrel, …) shared by every MEGA. " +
                "Ships only store how many copies they have and the summed totals.\n\n" +
                "Muted cyan hull-pool rows have firepower 0 (unarmed): they stay in the catalog but are not rolled into matches. " +
                "Orange rows have a raw 0 on a non-firepower stat. Those zeros stay in the catalog. " +
                "In-game, 0 becomes the listed Default Stats, then every non-firepower value is raised to Minimum Stats. " +
                "Cyan wins when a hull is both unarmed and missing other stats. " +
                "Cruise speed is fastest engine or thruster + Extra Engine Speed Percent of the rest.\n\n" +
                "Weapon Bullet Banks (Gun / Cannon / Missile / Sniper) pick the BulletVfxBank category those MEGA " +
                "weapon types fire. Unique weapon rows can override; Type table default inherits the type-table bank. " +
                "MEGAs no longer use the store planet's family bank.\n\n" +
                "Apply Default Type-Table Stats seeds the type table plus the In-game Default/Minimum Stats blocks " +
                "(move 12, accel 8, health 800, energy 1400, people 600, gun range 32, cannon 40, missile 36, sniper 48). " +
                "It also writes those ranges onto unique weapon rows, seeds inherit weapon banks from the type table, " +
                "and energy/people onto cockpit/engine/wing rows, then recalculates hull sums. " +
                "Then click Refresh Unique Components + Recalc Ship Sums so stored hull sums stay raw " +
                "(zeros stay 0; orange rows stay honest). Refresh adds new names and keeps hand-edited stats " +
                "except the ranges just written. " +
                "Reset overwrites every unique row from the type-table defaults, then rewrites all hull sums.",
                MessageType.Info);

            if (GUILayout.Button("Refresh Unique Components + Recalc Ship Sums", GUILayout.Height(28)))
            {
                Undo.RecordObject(catalog, "Refresh MEGA Unique Components");
                int components = MegaShipComponentInventory.RefreshAll(catalog, keepManualStats: true);
                EditorUtility.SetDirty(catalog);
                MegaShipCatalog.InvalidateCache();
                Debug.Log(
                    $"[MegaShipCatalog] Unique components={components}; recalculated {catalog.entries.Count} hull sums.");
            }

            if (GUILayout.Button("Reset Unique Components From Type Table", GUILayout.Height(24)))
            {
                Undo.RecordObject(catalog, "Reset MEGA Unique Components From Type Table");
                int components = MegaShipComponentInventory.RefreshAll(catalog, keepManualStats: false);
                EditorUtility.SetDirty(catalog);
                MegaShipCatalog.InvalidateCache();
                Debug.Log(
                    $"[MegaShipCatalog] Reset {components} unique components from the type table.");
            }

            if (GUILayout.Button("Apply Default Type-Table Stats", GUILayout.Height(24)))
            {
                Undo.RecordObject(catalog, "Apply Default MEGA Type-Table Stats");
                catalog.ApplyDefaultStaticStats();
                EditorUtility.SetDirty(catalog);
                Debug.Log("[MegaShipCatalog] Applied designer default type-table stats.");
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Orbit Menu previews", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Theatrical Menu Preview Images: 3/4 hero camera (same framing as ship-family theatrical thumbs). " +
                "Writes one PNG per team under Prefabs/MEGA_Ships/MenuPreviews/TeamA|TeamB|…/MEGA_000.png and " +
                "assigns teamMenuPreviewSprites (menuPreviewSprite = TeamA / first fallback). " +
                "Team tint uses catalog Team Materials when authored, otherwise the same 5-team family " +
                "material sets in-game MEGAs already apply from the store planet's gameplay family. " +
                "Background is always opaque black. Re-run after prefab, material, or camera-setting changes.",
                MessageType.Info);

            if (GUILayout.Button("Generate Theatrical Menu Preview Images", GUILayout.Height(28)))
            {
                Undo.RecordObject(catalog, "Generate MEGA Theatrical Menu Previews");
                MegaShipMenuPreviewGenerator.GenerateTheatricalForCatalog(catalog);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
#endif

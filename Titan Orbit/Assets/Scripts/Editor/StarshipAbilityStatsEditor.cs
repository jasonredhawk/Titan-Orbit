using UnityEditor;
using UnityEngine;
using TitanOrbit.Entities;

namespace TitanOrbit.Editor
{
    [CustomEditor(typeof(Starship))]
    public class StarshipAbilityStatsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var ship = (Starship)target;
            if (ship == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ability Stats (Effective)", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("Fire Power (x)", ship.EditorFirePowerMultiplier);
                EditorGUILayout.FloatField("Bullet Speed (x)", ship.EditorBulletSpeedMultiplier);
                EditorGUILayout.FloatField("Health Cap", ship.EditorHealthCap);
                EditorGUILayout.FloatField("Health Regen /s", ship.EditorHealthRegen);
                EditorGUILayout.FloatField("Energy Cap", ship.EditorEnergyCap);
                EditorGUILayout.FloatField("Energy Regen /s", ship.EditorEnergyRegen);
                EditorGUILayout.FloatField("Move Speed (max)", ship.EditorMoveSpeed);
                EditorGUILayout.FloatField("Turn Speed", ship.EditorTurnSpeed);
                EditorGUILayout.FloatField("Max Gems", ship.EditorMaxGems);
                EditorGUILayout.FloatField("Max People", ship.EditorMaxPeople);
            }
        }
    }
}


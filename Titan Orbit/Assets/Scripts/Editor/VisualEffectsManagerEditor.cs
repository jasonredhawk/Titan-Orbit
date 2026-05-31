#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Systems.Editor
{
    [CustomEditor(typeof(VisualEffectsManager))]
    public class VisualEffectsManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPropertiesExcluding(serializedObject, "m_Script", "floatingCountVisibility");

            var vfx = (VisualEffectsManager)target;
            var visibility = vfx.FloatingCountVisibility;
            if (visibility != null)
            {
                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Floating Count Visibility", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Each toggle controls one popup source. Changes apply immediately in Play Mode.",
                    MessageType.Info);

                EditorGUI.indentLevel++;
                visibility.gemPickup = EditorGUILayout.Toggle("Gem pickup", visibility.gemPickup);
                visibility.gemDeposit = EditorGUILayout.Toggle("Gem deposit", visibility.gemDeposit);
                visibility.damageAsteroid = EditorGUILayout.Toggle("Damage — asteroid", visibility.damageAsteroid);
                visibility.damageShipOrDrone = EditorGUILayout.Toggle("Damage — ship / drone", visibility.damageShipOrDrone);
                visibility.damageMoon = EditorGUILayout.Toggle("Damage — moon", visibility.damageMoon);
                visibility.healthChange = EditorGUILayout.Toggle("Health change", visibility.healthChange);
                visibility.peopleLoad = EditorGUILayout.Toggle("People — load", visibility.peopleLoad);
                visibility.peopleUnload = EditorGUILayout.Toggle("People — unload", visibility.peopleUnload);
                visibility.asteroidStatsOverlay = EditorGUILayout.Toggle("Asteroid stats overlay", visibility.asteroidStatsOverlay);
                visibility.asteroidImpactForce = EditorGUILayout.Toggle("Asteroid impact force", visibility.asteroidImpactForce);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            if (GUI.changed)
                EditorUtility.SetDirty(vfx);
        }
    }
}
#endif

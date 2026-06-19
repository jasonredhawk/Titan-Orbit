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
                    "Each toggle controls one popup source. Asteroid hits stack enabled lines into one grouped popup.",
                    MessageType.Info);

                EditorGUI.indentLevel++;
                visibility.gemPickup = EditorGUILayout.Toggle("Gem pickup", visibility.gemPickup);
                visibility.gemDeposit = EditorGUILayout.Toggle("Gem deposit", visibility.gemDeposit);
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Asteroid hit (stacked group)", EditorStyles.miniLabel);
                visibility.asteroidDamage = EditorGUILayout.Toggle("  Damage dealt", visibility.asteroidDamage);
                visibility.asteroidHealthRemaining = EditorGUILayout.Toggle("  HP remaining", visibility.asteroidHealthRemaining);
                visibility.asteroidGemsRemaining = EditorGUILayout.Toggle("  Gems remaining", visibility.asteroidGemsRemaining);
                visibility.asteroidImpactForce = EditorGUILayout.Toggle("  Impact force", visibility.asteroidImpactForce);
                EditorGUILayout.Space(4f);
                visibility.damageShipOrDrone = EditorGUILayout.Toggle("Damage — ship / drone", visibility.damageShipOrDrone);
                visibility.damageMoon = EditorGUILayout.Toggle("Damage — moon", visibility.damageMoon);
                visibility.healthChange = EditorGUILayout.Toggle("Health change", visibility.healthChange);
                visibility.peopleLoad = EditorGUILayout.Toggle("People — load", visibility.peopleLoad);
                visibility.peopleUnload = EditorGUILayout.Toggle("People — unload", visibility.peopleUnload);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            if (GUI.changed)
                EditorUtility.SetDirty(vfx);
        }
    }
}
#endif

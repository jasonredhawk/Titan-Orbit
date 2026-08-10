using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Menu command to add the mobile touch canvas (virtual steer + fire zones) beside an
    /// existing UI Canvas. Does not modify gameplay scenes wholesale — only inserts
    /// <see cref="MobileControlsEditorUtility.MobileTouchRootName"/>. Paired with
    /// <see cref="MobileControlsEditorUtility"/>.
    /// </summary>
    public static class MobileControlsMenu
    {
        private const string MenuPath = "Titan Orbit/Add Mobile Touch Canvas";

        /// <summary>Resolves target Canvas from selection or first Canvas in open scene, then adds controls.</summary>
        [MenuItem(MenuPath, false, 11)]
        public static void AddMobileControls()
        {
            // --- Resolve target canvas from selection or scene ---
            Canvas canvas = ResolveTargetCanvas();
            if (canvas == null)
            {
                EditorUtility.DisplayDialog(
                    "Add Mobile Controls",
                    "No Canvas found. Open your game scene, then either:\n" +
                    "• Select a GameObject under your UI Canvas, or\n" +
                    "• Deselect everything and ensure there is at least one Canvas in the scene.",
                    "OK");
                return;
            }

            // --- Build or replace mobile touch root ---
            Transform root = canvas.transform;
            Transform existing = root.parent != null ? root.parent.Find(MobileControlsEditorUtility.MobileTouchRootName) : root.Find(MobileControlsEditorUtility.MobileTouchRootName);
            if (existing == null)
                existing = root.Find(MobileControlsEditorUtility.MobileTouchRootName);
            if (existing != null)
            {
                if (!EditorUtility.DisplayDialog(
                        "Add Mobile Touch Canvas",
                        $"\"{MobileControlsEditorUtility.MobileTouchRootName}\" already exists.\n\nReplace it?",
                        "Replace",
                        "Cancel"))
                    return;
            }

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null)
                scaler = canvas.GetComponentInParent<CanvasScaler>();

            MobileControlsEditorUtility.AddMobileControlsToCanvas(root, scaler, destroyExistingRoot: true);
            EditorUtility.DisplayDialog(
                "Add Mobile Touch Canvas",
                $"Added \"{MobileControlsEditorUtility.MobileTouchRootName}\" next to your UI.\n\n" +
                "Steer on the left half of the screen; fire on the right. Assign Classic Hud Canvas on MobileControls to hide desktop HUD on phones.",
                "OK");
        }

        [MenuItem(MenuPath, true)]
        private static bool AddMobileControlsValidate()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static Canvas ResolveTargetCanvas()
        {
            GameObject sel = Selection.activeGameObject;
            if (sel != null)
            {
                Canvas c = sel.GetComponentInParent<Canvas>(true);
                if (c != null)
                    return c;
            }

            return Object.FindFirstObjectByType<Canvas>(FindObjectsInactive.Exclude);
        }
    }
}

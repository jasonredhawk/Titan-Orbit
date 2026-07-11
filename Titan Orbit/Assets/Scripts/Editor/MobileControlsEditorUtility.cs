using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.UI;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Builds a dedicated <see cref="Canvas"/> for mobile: touch brakes + <see cref="MobileControls"/> (left/right screen input).
    /// Optional classic HUD canvas is disabled on phones when wired.
    /// </summary>
    public static class MobileControlsEditorUtility
    {
        public const string MobileTouchRootName = "MobileTouchUI";

        /// <summary>Adds mobile touch UI as its own canvas (sibling-friendly). Wire <see cref="MobileControls.classicHudCanvas"/> in the inspector if you use a separate desktop-only HUD to hide on phones.</summary>
        public static GameObject AddMobileTouchCanvas(Transform parent, bool destroyExistingRoot)
        {
            // --- AddMobileTouchCanvas ---
            if (parent == null)
                return null;

            Transform existing = parent.Find(MobileTouchRootName);
            if (existing != null)
            {
                if (!destroyExistingRoot)
                    return existing.gameObject;
                Object.DestroyImmediate(existing.gameObject);
            }

            Sprite uiSprite = CreateWhiteSprite();
            Sprite shiftButtonSprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/Shift - Complete Sci-Fi UI/Textures/Border/Cut/Cut Frame Filled.png");
            if (shiftButtonSprite == null) shiftButtonSprite = uiSprite;

            GameObject root = new GameObject(MobileTouchRootName);
            root.transform.SetParent(parent, false);

            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();

            GameObject panel = new GameObject("MobileTouchPanel");
            panel.transform.SetParent(root.transform, false);
            RectTransform panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;
            CanvasGroup panelCg = panel.AddComponent<CanvasGroup>();
            panelCg.blocksRaycasts = false;
            panelCg.interactable = true;

            GameObject brakesBtn = CreateShiftButton(panel.transform, "AirBrakesToggle", "AIR BRAKES", shiftButtonSprite, new Color(0.2f, 0.45f, 0.55f, 0.95f));
            brakesBtn.AddComponent<MobileSpaceBrakesToggle>();
            RectTransform brakesRect = brakesBtn.GetComponent<RectTransform>();
            brakesRect.anchorMin = new Vector2(1f, 0f);
            brakesRect.anchorMax = new Vector2(1f, 0f);
            brakesRect.pivot = new Vector2(1f, 0f);
            brakesRect.anchoredPosition = new Vector2(-24f, 24f);
            brakesRect.sizeDelta = new Vector2(280f, 88f);

            GameObject steerObj = new GameObject("SteerVisual");
            steerObj.transform.SetParent(root.transform, false);
            steerObj.transform.SetAsLastSibling();
            RectTransform steerRt = steerObj.AddComponent<RectTransform>();
            steerRt.anchorMin = Vector2.zero;
            steerRt.anchorMax = Vector2.one;
            steerRt.offsetMin = Vector2.zero;
            steerRt.offsetMax = Vector2.zero;
            steerObj.AddComponent<MobileSteerVisualUI>();

            MobileControls mobileControls = root.AddComponent<MobileControls>();
            SerializedObject mobileControlsSO = new SerializedObject(mobileControls);
            mobileControlsSO.FindProperty("mobileControlsPanel").objectReferenceValue = panel;
            mobileControlsSO.FindProperty("mobileTouchCanvas").objectReferenceValue = canvas;
            mobileControlsSO.FindProperty("classicHudCanvas").objectReferenceValue = null;
            mobileControlsSO.FindProperty("disableClassicHudWhileMobileActive").boolValue = false;
            mobileControlsSO.FindProperty("shootZoneExclusions").ClearArray();
            mobileControlsSO.FindProperty("shootZoneExclusions").InsertArrayElementAtIndex(0);
            mobileControlsSO.FindProperty("shootZoneExclusions").GetArrayElementAtIndex(0).objectReferenceValue = brakesRect;
            mobileControlsSO.FindProperty("forceMobileControls").boolValue = false;
            mobileControlsSO.ApplyModifiedPropertiesWithoutUndo();

            Undo.RegisterCreatedObjectUndo(root, "Add Mobile Touch Canvas");
            return root;
        }

        /// <summary>Legacy entry: adds mobile touch canvas under the same transform as your main UI (sibling of content).</summary>
        public static GameObject AddMobileControlsToCanvas(Transform canvasTransform, CanvasScaler canvasScaler, bool destroyExistingRoot)
        {
            // --- AddMobileControlsToCanvas ---
            if (canvasTransform == null)
                return null;
            Transform parent = canvasTransform.parent != null ? canvasTransform.parent : canvasTransform;
            return AddMobileTouchCanvas(parent, destroyExistingRoot);
        }

        private static Sprite CreateWhiteSprite()
        {
            // --- Create instance ---
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        private static GameObject CreateShiftButton(Transform parent, string name, string label, Sprite sprite, Color? buttonColor = null)
        {
            // --- Create instance ---
            Color color = buttonColor ?? new Color(0.25f, 0.55f, 0.9f, 0.95f);
            GameObject btnObj = new GameObject(name);
            btnObj.transform.SetParent(parent, false);
            Image img = btnObj.AddComponent<Image>();
            img.color = color;
            img.sprite = sprite;
            img.raycastTarget = true;
            img.type = sprite != null && sprite.border.sqrMagnitude > 0 ? Image.Type.Sliced : Image.Type.Simple;

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(btnObj.transform, false);
            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 22;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(1f, 1f, 1f, 0.95f);
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(4, 4);
            textRect.offsetMax = new Vector2(-4, -4);

            return btnObj;
        }
    }
}

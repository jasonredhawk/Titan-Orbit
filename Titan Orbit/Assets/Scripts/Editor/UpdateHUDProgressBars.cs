using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.UI;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Helper script to add progress bars to an existing HUD setup
    /// </summary>
    public class UpdateHUDProgressBars : EditorWindow
    {
        [MenuItem("Titan Orbit/Update HUD Progress Bars")]
        public static void UpdateHUD()
        {
            // Find the HUD GameObject
            GameObject hudObj = GameObject.Find("HUD");
            if (hudObj == null)
            {
                Debug.LogError("HUD GameObject not found. Make sure the scene has been set up.");
                return;
            }

            HUDController hudController = hudObj.GetComponent<HUDController>();
            if (hudController == null)
            {
                Debug.LogError("HUDController component not found on HUD GameObject.");
                return;
            }

            // Find the ship stats panel
            GameObject shipPanel = GameObject.Find("ShipStatsPanel");
            if (shipPanel == null)
            {
                Debug.LogError("ShipStatsPanel not found.");
                return;
            }

            // Find the home planet panel
            GameObject homePanel = GameObject.Find("HomePlanetStatsPanel");
            if (homePanel == null)
            {
                Debug.LogError("HomePlanetStatsPanel not found.");
                return;
            }

            Sprite uiSprite = CreateWhiteSprite();

            // Create gem progress bar for ship
            Transform gemTextTransform = shipPanel.transform.Find("GemText");
            var hudSO = new SerializedObject(hudController);
            SerializedProperty gemBarProp = hudSO.FindProperty("gemBar");
            if (gemTextTransform != null && (gemBarProp == null || gemBarProp.objectReferenceValue == null))
            {
                GameObject gemBarObj = CreateProgressBar(shipPanel.transform, "GemBar", uiSprite, new Color(0.2f, 0.9f, 0.5f, 1f));
                RectTransform gemBarRect = gemBarObj.GetComponent<RectTransform>();
                gemBarRect.anchorMin = new Vector2(0, 1);
                gemBarRect.anchorMax = new Vector2(1, 1);
                gemBarRect.pivot = new Vector2(0.5f, 1);
                gemBarRect.anchoredPosition = new Vector2(0, -74);
                gemBarRect.offsetMin = new Vector2(12, 0);
                gemBarRect.offsetMax = new Vector2(-12, -20);
                gemBarObj.transform.SetSiblingIndex(gemTextTransform.GetSiblingIndex());
                
                hudSO = new SerializedObject(hudController);
                hudSO.FindProperty("gemBar").objectReferenceValue = gemBarObj.GetComponent<Slider>();
                hudSO.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("Created Gem progress bar for ship.");
            }

            // Create people progress bar for ship
            Transform peopleTextTransform = shipPanel.transform.Find("PeopleText");
            hudSO = new SerializedObject(hudController);
            SerializedProperty peopleBarProp = hudSO.FindProperty("peopleBar");
            if (peopleTextTransform != null && (peopleBarProp == null || peopleBarProp.objectReferenceValue == null))
            {
                GameObject peopleBarObj = CreateProgressBar(shipPanel.transform, "PeopleBar", uiSprite, new Color(0.4f, 0.6f, 0.9f, 1f));
                RectTransform peopleBarRect = peopleBarObj.GetComponent<RectTransform>();
                peopleBarRect.anchorMin = new Vector2(0, 1);
                peopleBarRect.anchorMax = new Vector2(1, 1);
                peopleBarRect.pivot = new Vector2(0.5f, 1);
                peopleBarRect.anchoredPosition = new Vector2(0, -96);
                peopleBarRect.offsetMin = new Vector2(12, 0);
                peopleBarRect.offsetMax = new Vector2(-12, -20);
                peopleBarObj.transform.SetSiblingIndex(peopleTextTransform.GetSiblingIndex());
                
                hudSO = new SerializedObject(hudController);
                hudSO.FindProperty("peopleBar").objectReferenceValue = peopleBarObj.GetComponent<Slider>();
                hudSO.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("Created People progress bar for ship.");
            }

            // Create gem progress bar for home planet
            Transform homeGemsTextTransform = homePanel.transform.Find("GemsText");
            hudSO = new SerializedObject(hudController);
            SerializedProperty homeGemBarProp = hudSO.FindProperty("homePlanetGemBar");
            if (homeGemsTextTransform != null && (homeGemBarProp == null || homeGemBarProp.objectReferenceValue == null))
            {
                GameObject homeGemBarObj = CreateProgressBar(homePanel.transform, "HomeGemBar", uiSprite, new Color(0.95f, 0.85f, 0.5f, 1f));
                RectTransform homeGemBarRect = homeGemBarObj.GetComponent<RectTransform>();
                homeGemBarRect.anchorMin = new Vector2(0, 1);
                homeGemBarRect.anchorMax = new Vector2(1, 1);
                homeGemBarRect.pivot = new Vector2(0.5f, 1);
                homeGemBarRect.anchoredPosition = new Vector2(0, -64);
                homeGemBarRect.offsetMin = new Vector2(12, 0);
                homeGemBarRect.offsetMax = new Vector2(-12, -24);
                homeGemBarObj.transform.SetSiblingIndex(homeGemsTextTransform.GetSiblingIndex());
                
                hudSO = new SerializedObject(hudController);
                hudSO.FindProperty("homePlanetGemBar").objectReferenceValue = homeGemBarObj.GetComponent<Slider>();
                hudSO.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("Created Gem progress bar for home planet.");
            }

            Debug.Log("HUD progress bars update complete!");
        }

        [MenuItem("Titan Orbit/Add Proximity Radar to HUD")]
        public static void AddProximityRadar()
        {
            GameObject hudObj = GameObject.Find("HUD");
            if (hudObj == null)
            {
                Debug.LogError("HUD GameObject not found. Make sure the scene has been set up.");
                return;
            }

            if (hudObj.transform.Find("ProximityRadar") != null)
            {
                Debug.Log("ProximityRadar already exists on HUD.");
                return;
            }

            GameObject proximityRadarObj = new GameObject("ProximityRadar");
            proximityRadarObj.transform.SetParent(hudObj.transform, false);
            proximityRadarObj.AddComponent<ProximityRadarHUD>();
            UnityEditor.Undo.RegisterCreatedObjectUndo(proximityRadarObj, "Add Proximity Radar");
            Debug.Log("ProximityRadar added to HUD.");
        }

        private static Sprite CreateWhiteSprite()
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        private static GameObject CreateProgressBar(Transform parent, string name, Sprite uiSprite, Color fillColor)
        {
            GameObject sliderObj = new GameObject(name);
            sliderObj.transform.SetParent(parent, false);
            Slider slider = sliderObj.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 1f;
            slider.wholeNumbers = false;

            GameObject bg = new GameObject("Background");
            bg.transform.SetParent(sliderObj.transform, false);
            Image bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.15f, 0.15f, 0.2f, 0.95f);
            bgImg.sprite = uiSprite;
            RectTransform bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            GameObject fillArea = new GameObject("Fill Area");
            fillArea.transform.SetParent(sliderObj.transform, false);
            RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.1f);
            fillAreaRect.anchorMax = new Vector2(1, 0.9f);
            fillAreaRect.offsetMin = new Vector2(4, 2);
            fillAreaRect.offsetMax = new Vector2(-4, -2);

            GameObject fill = new GameObject("Fill");
            fill.transform.SetParent(fillArea.transform, false);
            Image fillImg = fill.AddComponent<Image>();
            fillImg.color = fillColor;
            fillImg.sprite = uiSprite;
            RectTransform fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(1, 1);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            slider.fillRect = fillRect;
            slider.direction = Slider.Direction.LeftToRight;
            return sliderObj;
        }
    }
}

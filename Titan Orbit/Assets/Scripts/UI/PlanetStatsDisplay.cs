using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// World-space planet stats: Slider-based progress bars (like ShipStatsPanel) for population
    /// and gems/level on home planets. Card-style layout with header and label + slider + value rows.
    /// </summary>
    public class PlanetStatsDisplay : MonoBehaviour
    {
        private Planet planet;
        private HomePlanet homePlanet;
        private Canvas canvas;
        private RectTransform rootRect;
        private Slider popSlider;
        private Slider gemsSlider;
        private TextMeshProUGUI popValueText;
        private TextMeshProUGUI gemsValueText;
        private TextMeshProUGUI levelText;
        private GameObject gemsRow;
        private const float RefreshInterval = 0.2f;
        private float lastRefresh;

        public void Init(Planet p)
        {
            planet = p;
            homePlanet = p != null ? p.GetComponent<HomePlanet>() : null;
            if (planet == null) return;
        }

        private static Sprite GetWhiteSprite()
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }

        private void BuildCanvas()
        {
            if (planet == null || rootRect != null) return;
            if (!planet.IsClient || UnityEngine.Camera.main == null) return;

            var go = new GameObject("PlanetStatsCanvas");
            go.transform.SetParent(planet.transform, false);
            go.transform.localPosition = planet is HomePlanet ? new Vector3(0f, 0.65f, 0f) : new Vector3(0f, 0.55f, 0f);
            if (homePlanet != null)
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            else
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = Vector3.one;

            canvas = go.AddComponent<Canvas>();
            if (canvas == null) return;
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = UnityEngine.Camera.main;

            var rt = go.transform as RectTransform;
            if (rt == null)
                rt = go.AddComponent<RectTransform>();
            if (rt == null) return;

            // Slightly larger panel for clearer layout
            rt.sizeDelta = new Vector2(220f, homePlanet != null ? 88f : 48f);
            rt.localScale = new Vector3(0.003f, 0.003f, 0.003f);
            var scaler = go.AddComponent<CanvasScaler>();
            if (scaler != null)
                scaler.dynamicPixelsPerUnit = 10f;

            Sprite uiSprite = GetWhiteSprite();

            // Panel background (card style)
            var bg = new GameObject("Panel");
            bg.transform.SetParent(rt, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.06f, 0.06f, 0.1f, 0.92f);
            bgImage.sprite = uiSprite;
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            var vlg = bg.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4f;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // Header row (home: "Home Lv.X", other: optional or skip)
            if (homePlanet != null)
            {
                var headerRow = new GameObject("HeaderRow");
                headerRow.transform.SetParent(bg.transform, false);
                var headerHlg = headerRow.AddComponent<HorizontalLayoutGroup>();
                headerHlg.childAlignment = TextAnchor.MiddleCenter;
                headerHlg.childControlWidth = false;
                headerHlg.childControlHeight = true;
                var headerRect = headerRow.GetComponent<RectTransform>();
                headerRect.sizeDelta = new Vector2(0f, 14f);
                var headerLabel = AddText(headerRow.transform, "Home ", 9);
                headerLabel.color = new Color(0.75f, 0.8f, 0.9f);
                levelText = AddText(headerRow.transform, "Lv.1", 10);
                levelText.color = new Color(0.95f, 0.95f, 1f);
            }

            // Population row (label + Slider + value)
            AddSliderRow(bg.transform, "Pop", new Color(1f, 0.6f, 0.2f), uiSprite, out popSlider, out popValueText);

            if (homePlanet != null)
            {
                AddSliderRow(bg.transform, "Gems", new Color(0.95f, 0.85f, 0.5f), uiSprite, out gemsSlider, out gemsValueText, out gemsRow);
            }
            else
            {
                gemsRow = null;
            }

            rootRect = rt;
        }

        private void AddSliderRow(Transform parent, string label, Color fillColor, Sprite uiSprite,
            out Slider slider, out TextMeshProUGUI valueText, out GameObject rowGo)
        {
            rowGo = new GameObject(label + "Row");
            rowGo.transform.SetParent(parent, false);
            var hlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            var rowRect = rowGo.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0f, 16f);

            var labelText = AddText(rowGo.transform, label, 10);
            labelText.color = new Color(0.9f, 0.9f, 0.95f);
            var labelLe = labelText.GetComponent<RectTransform>();
            if (labelLe != null) labelLe.sizeDelta = new Vector2(32f, 14f);

            GameObject sliderObj = CreateSlider(rowGo.transform, label + "Bar", uiSprite, fillColor);
            var barLe = sliderObj.AddComponent<LayoutElement>();
            barLe.flexibleWidth = 1f;
            barLe.preferredWidth = 120f;
            barLe.preferredHeight = 10f;
            slider = sliderObj.GetComponent<Slider>();

            valueText = AddText(rowGo.transform, "0/100", 9);
            valueText.color = new Color(0.85f, 0.85f, 0.9f);
            var valueLe = valueText.GetComponent<RectTransform>();
            if (valueLe != null) valueLe.sizeDelta = new Vector2(44f, 14f);
        }

        private void AddSliderRow(Transform parent, string label, Color fillColor, Sprite uiSprite,
            out Slider slider, out TextMeshProUGUI valueText)
        {
            GameObject rowGo;
            AddSliderRow(parent, label, fillColor, uiSprite, out slider, out valueText, out rowGo);
        }

        private static GameObject CreateSlider(Transform parent, string name, Sprite uiSprite, Color fillColor)
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
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            slider.fillRect = fillRect;
            slider.direction = Slider.Direction.LeftToRight;
            return sliderObj;
        }

        private static TextMeshProUGUI AddText(Transform parent, string content, int fontSize)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = fontSize;
            text.color = Color.white;
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(36f, 12f);
            return text;
        }

        private void LateUpdate()
        {
            if (planet == null) return;
            if (rootRect == null)
            {
                BuildCanvas();
                return;
            }
            if (Time.time - lastRefresh < RefreshInterval) return;
            lastRefresh = Time.time;

            float maxPop = planet.MaxPopulation;
            float curPop = planet.CurrentPopulation;
            if (popSlider != null)
                popSlider.value = maxPop > 0 ? Mathf.Clamp01(curPop / maxPop) : 0f;
            if (popValueText != null)
                popValueText.text = $"{Mathf.RoundToInt(curPop)}/{Mathf.RoundToInt(maxPop)}";

            if (homePlanet != null && gemsSlider != null && gemsValueText != null && gemsRow != null)
            {
                gemsRow.SetActive(true);
                float maxGems = homePlanet.MaxGems;
                float curGems = homePlanet.CurrentGems;
                gemsSlider.value = maxGems > 0 ? Mathf.Clamp01(curGems / maxGems) : 0f;
                gemsValueText.text = $"{Mathf.RoundToInt(curGems)}/{Mathf.RoundToInt(maxGems)}";
                if (levelText != null)
                    levelText.text = "Lv." + homePlanet.HomePlanetLevel.ToString();
            }
            else if (gemsRow != null)
                gemsRow.SetActive(false);

            if (canvas != null && canvas.worldCamera == null)
                canvas.worldCamera = UnityEngine.Camera.main;
            if (homePlanet != null && rootRect != null)
                rootRect.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        public void Refresh()
        {
            lastRefresh = -999f;
        }
    }
}

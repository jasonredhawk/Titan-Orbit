using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Systems;

namespace TitanOrbit.UI
{
    /// <summary>
    /// World-space planet stats: family name plus population numbers (no progress bars or gem row).
    /// </summary>
    public class PlanetStatsDisplay : MonoBehaviour
    {
        private const float SurfacePaddingWorld = 1.2f;
        private Planet planet;
        private Canvas canvas;
        private RectTransform rootRect;
        private TextMeshProUGUI planetNameText;
        private TextMeshProUGUI popValueText;
        private TextMeshProUGUI popMaxText;
        private const float RefreshInterval = 0.2f;
        private float lastRefresh;

        public void Init(Planet p)
        {
            planet = p;
        }

        private void BuildCanvas()
        {
            if (planet == null || rootRect != null) return;
            if (!planet.IsClient || UnityEngine.Camera.main == null) return;

            bool home = planet is HomePlanet;
            var go = new GameObject("PlanetStatsCanvas");
            go.transform.SetParent(planet.transform, false);
            // Local Y is updated dynamically per-planet size after RectTransform setup.
            go.transform.localPosition = Vector3.zero;
            if (home)
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

            rt.sizeDelta = new Vector2(380f, 230f);
            // Make the rect anchor/pivot the true center so our number stays centered.
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localScale = new Vector3(0.003f, 0.003f, 0.003f);
            var scaler = go.AddComponent<CanvasScaler>();
            if (scaler != null)
                scaler.dynamicPixelsPerUnit = 10f;

            planetNameText = AddText(rt, string.Empty, 34);
            planetNameText.color = ResolvePlanetTitleColor();
            planetNameText.alignment = TextAlignmentOptions.Center;
            var nameRect = planetNameText.GetComponent<RectTransform>();
            if (nameRect != null)
            {
                nameRect.anchorMin = new Vector2(0.5f, 0.5f);
                nameRect.anchorMax = new Vector2(0.5f, 0.5f);
                nameRect.pivot = new Vector2(0.5f, 0.5f);
                nameRect.anchoredPosition = new Vector2(0f, 92f);
                nameRect.sizeDelta = new Vector2(360f, 40f);
            }
            ApplyOutline(planetNameText, Color.black, 0.25f);

            popValueText = AddText(rt, "0", 90);
            popValueText.color = new Color(1f, 0.92f, 0.25f); // yellow
            popValueText.alignment = TextAlignmentOptions.Center;
            var popRect = popValueText.GetComponent<RectTransform>();
            if (popRect != null)
            {
                popRect.anchorMin = new Vector2(0.5f, 0.5f);
                popRect.anchorMax = new Vector2(0.5f, 0.5f);
                popRect.pivot = new Vector2(0.5f, 0.5f);
                popRect.anchoredPosition = new Vector2(0f, -6f);
                popRect.sizeDelta = new Vector2(220f, 78f);
            }
            ApplyOutline(popValueText, Color.black, 0.25f);

            popMaxText = AddText(rt, "0", 45);
            popMaxText.color = new Color(1f, 0.92f, 0.25f); // yellow
            popMaxText.alignment = TextAlignmentOptions.Center;
            var maxRect = popMaxText.GetComponent<RectTransform>();
            if (maxRect != null)
            {
                maxRect.anchorMin = new Vector2(0.5f, 0.5f);
                maxRect.anchorMax = new Vector2(0.5f, 0.5f);
                maxRect.pivot = new Vector2(0.5f, 0.5f);
                maxRect.anchoredPosition = new Vector2(0f, -88f);
                maxRect.sizeDelta = new Vector2(130f, 52f);
            }
            ApplyOutline(popMaxText, Color.black, 0.25f);

            rootRect = rt;
            UpdatePanelPlacement();
        }

        private void UpdatePanelPlacement()
        {
            if (rootRect == null || planet == null) return;
            float size = Mathf.Max(0.01f, planet.PlanetSize);
            // Sphere radius in world ~= PlanetSize * 0.5. Convert desired world padding into local space.
            float localY = 0.5f + (SurfacePaddingWorld / size);
            rootRect.localPosition = new Vector3(0f, localY, 0f);
        }

        private static TextMeshProUGUI AddText(Transform parent, string content, int fontSize)
        {
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(parent, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = fontSize;
            text.fontStyle = FontStyles.Bold;
            text.fontWeight = FontWeight.Black;
            text.color = Color.white;
            var rect = textGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(36f, 12f);
            Material mat = text.fontMaterial;
            if (mat != null && mat.HasProperty("_FaceDilate"))
                mat.SetFloat("_FaceDilate", 0.25f);
            return text;
        }

        private static void ApplyOutline(TextMeshProUGUI text, Color outlineColor, float outlineWidth)
        {
            if (text == null) return;
            Material mat = text.fontMaterial;
            if (mat == null) return;
            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor")) mat.SetColor("_OutlineColor", outlineColor);
            if (mat.HasProperty("_OutlineWidth")) mat.SetFloat("_OutlineWidth", Mathf.Clamp01(outlineWidth));
            if (mat.HasProperty("_OutlineSoftness")) mat.SetFloat("_OutlineSoftness", 0.05f);
        }

        private void LateUpdate()
        {
            if (planet == null) return;
            if (rootRect == null)
            {
                BuildCanvas();
                return;
            }
            UpdatePanelPlacement();
            if (rootRect != null)
                rootRect.rotation = Quaternion.Euler(90f, 0f, 0f);

            if (Time.time - lastRefresh < RefreshInterval) return;
            lastRefresh = Time.time;

            float curPop = planet.CurrentPopulation;
            if (planetNameText != null)
            {
                string displayName = ResolvePlanetDisplayName();
                planetNameText.text = displayName;
                planetNameText.color = ResolvePlanetTitleColor();
                planetNameText.gameObject.SetActive(!string.IsNullOrEmpty(displayName));
            }
            if (popValueText != null)
                popValueText.text = Mathf.RoundToInt(curPop).ToString();
            if (popMaxText != null)
                popMaxText.text = Mathf.RoundToInt(planet.MaxPopulation).ToString();

            if (canvas != null && canvas.worldCamera == null)
                canvas.worldCamera = UnityEngine.Camera.main;
        }

        private string ResolvePlanetDisplayName()
        {
            if (planet == null)
                return string.Empty;
            if (CardShopSystem.Instance != null)
                return CardShopSystem.Instance.GetPlanetFamilyDisplayName(planet.PlanetId);
            return string.Empty;
        }

        private Color ResolvePlanetTitleColor()
        {
            if (planet == null)
                return Color.white;
            return TeamManager.GetTeamColor(planet.TeamOwnership);
        }

        public void Refresh()
        {
            lastRefresh = -999f;
        }
    }
}

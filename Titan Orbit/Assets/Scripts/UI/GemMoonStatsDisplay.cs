using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// World-space gem moon: single number for moon gem reservoir (synced from server).
    /// </summary>
    public class GemMoonStatsDisplay : MonoBehaviour
    {
        private const float MoonSurfacePaddingLocal = 0.12f;
        private PlanetGemMoon moon;
        private Canvas canvas;
        private RectTransform rootRect;
        private TextMeshProUGUI gemsText;
        private TextMeshProUGUI gemsMaxText;
        private const float RefreshInterval = 0.2f;
        private float lastRefresh;

        public void Init(PlanetGemMoon m)
        {
            moon = m;
        }

        private void BuildCanvas()
        {
            if (moon == null || rootRect != null) return;
            Planet p = moon.Planet;
            if (p == null || !p.IsClient || UnityEngine.Camera.main == null) return;

            var go = new GameObject("GemMoonStatsCanvas");
            go.transform.SetParent(moon.transform, false);
            // Local Y is updated dynamically per-moon size after RectTransform setup.
            go.transform.localPosition = Vector3.zero;
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

            rt.sizeDelta = new Vector2(250f, 95f);
            // Make the rect anchor/pivot the true center so our number stays centered.
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localScale = new Vector3(0.003f, 0.003f, 0.003f);
            var scaler = go.AddComponent<CanvasScaler>();
            if (scaler != null)
                scaler.dynamicPixelsPerUnit = 10f;

            gemsText = AddText(rt, "0", 36);
            gemsText.color = new Color(1f, 0.2f, 0.2f); // red
            gemsText.alignment = TextAlignmentOptions.Center;
            var gemsRect = gemsText.GetComponent<RectTransform>();
            if (gemsRect != null)
            {
                gemsRect.anchorMin = new Vector2(0.5f, 0.5f);
                gemsRect.anchorMax = new Vector2(0.5f, 0.5f);
                gemsRect.pivot = new Vector2(0.5f, 0.5f);
                gemsRect.anchoredPosition = new Vector2(0f, 14f);
                gemsRect.sizeDelta = new Vector2(130f, 70f);
            }
            ApplyOutline(gemsText, Color.white, 0.25f);

            gemsMaxText = AddText(rt, "0", 18);
            gemsMaxText.color = new Color(1f, 0.2f, 0.2f); // red
            gemsMaxText.alignment = TextAlignmentOptions.Center;
            var gemsMaxRect = gemsMaxText.GetComponent<RectTransform>();
            if (gemsMaxRect != null)
            {
                gemsMaxRect.anchorMin = new Vector2(0.5f, 0.5f);
                gemsMaxRect.anchorMax = new Vector2(0.5f, 0.5f);
                gemsMaxRect.pivot = new Vector2(0.5f, 0.5f);
                // Under the primary number, close but non-overlapping.
                gemsMaxRect.anchoredPosition = new Vector2(0f, -18f);
                gemsMaxRect.sizeDelta = new Vector2(90f, 28f);
            }
            ApplyOutline(gemsMaxText, Color.white, 0.25f);

            rootRect = rt;
            UpdatePanelPlacement();
        }

        private void UpdatePanelPlacement()
        {
            if (rootRect == null || moon == null) return;
            float bodyRadius = Mathf.Max(0.01f, moon.GetMoonBodyRadiusLocal());
            rootRect.localPosition = new Vector3(0f, bodyRadius + MoonSurfacePaddingLocal, 0f);
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
            if (moon == null) return;
            if (rootRect == null)
            {
                BuildCanvas();
                return;
            }
            UpdatePanelPlacement();
            if (rootRect != null)
                rootRect.localRotation = Quaternion.Euler(90f, 0f, 0f);
            if (Time.time - lastRefresh < RefreshInterval) return;
            lastRefresh = Time.time;

            if (gemsText != null)
            {
                Planet p = moon.Planet;
                float gems = p != null ? p.CurrentGems : 0f;
                gemsText.text = Mathf.RoundToInt(gems).ToString();
                if (gemsMaxText != null)
                {
                    float maxGems = p != null ? p.MaxGems : 0f;
                    gemsMaxText.text = Mathf.RoundToInt(maxGems).ToString();
                }
            }

            if (canvas != null && canvas.worldCamera == null)
                canvas.worldCamera = UnityEngine.Camera.main;
        }

        public void Refresh()
        {
            lastRefresh = -999f;
        }
    }
}

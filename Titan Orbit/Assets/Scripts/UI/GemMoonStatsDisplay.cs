using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// World-space gem moon: moon gem reservoir + shield points (synced from server).
    /// </summary>
    public class GemMoonStatsDisplay : MonoBehaviour
    {
        private const float MoonSurfacePaddingLocal = 0.12f;
        private const float StatIconSize = 17f;
        private const float StatIconCenterX = -62f;
        /// <summary>Vertical center between gems main and max lines (anchored Y 52 and 22).</summary>
        private const float GemsIconCenterY = 37f;
        /// <summary>Vertical center between shield main and max lines (anchored Y -24 and -50).</summary>
        private const float ShieldIconCenterY = -37f;

        private PlanetGemMoon moon;
        private Canvas canvas;
        private RectTransform rootRect;
        private TextMeshProUGUI gemsText;
        private TextMeshProUGUI gemsMaxText;
        private TextMeshProUGUI shieldText;
        private TextMeshProUGUI shieldMaxText;
        private Sprite gemIconSprite;
        private Sprite shieldIconSprite;
        private const float RefreshInterval = 0.2f;
        private float lastRefresh;
        private GameObject moonDockButtonsRoot;

        public void Init(PlanetGemMoon m, Sprite gemIcon, Sprite shieldIcon)
        {
            moon = m;
            gemIconSprite = gemIcon;
            shieldIconSprite = shieldIcon;
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
            go.AddComponent<GraphicRaycaster>();

            var rt = go.transform as RectTransform;
            if (rt == null)
                rt = go.AddComponent<RectTransform>();
            if (rt == null) return;

            rt.sizeDelta = new Vector2(430f, 165f);
            // Make the rect anchor/pivot the true center so our number stays centered.
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.localScale = new Vector3(0.003f, 0.003f, 0.003f);
            var scaler = go.AddComponent<CanvasScaler>();
            if (scaler != null)
                scaler.dynamicPixelsPerUnit = 10f;

            Color gemsColor = new Color(1f, 0.2f, 0.2f); // red
            Color shieldColor = new Color(0.25f, 0.95f, 1f); // cyan

            gemsText = AddText(rt, "0", 36);
            gemsText.color = gemsColor;
            gemsText.alignment = TextAlignmentOptions.Center;
            var gemsRect = gemsText.GetComponent<RectTransform>();
            if (gemsRect != null)
            {
                gemsRect.anchorMin = new Vector2(0.5f, 0.5f);
                gemsRect.anchorMax = new Vector2(0.5f, 0.5f);
                gemsRect.pivot = new Vector2(0.5f, 0.5f);
                gemsRect.anchoredPosition = new Vector2(0f, 52f);
                gemsRect.sizeDelta = new Vector2(180f, 58f);
            }
            ApplyOutline(gemsText, Color.white, 0.25f);

            gemsMaxText = AddText(rt, "0", 18);
            gemsMaxText.color = gemsColor;
            gemsMaxText.alignment = TextAlignmentOptions.Center;
            var gemsMaxRect = gemsMaxText.GetComponent<RectTransform>();
            if (gemsMaxRect != null)
            {
                gemsMaxRect.anchorMin = new Vector2(0.5f, 0.5f);
                gemsMaxRect.anchorMax = new Vector2(0.5f, 0.5f);
                gemsMaxRect.pivot = new Vector2(0.5f, 0.5f);
                gemsMaxRect.anchoredPosition = new Vector2(0f, 22f);
                gemsMaxRect.sizeDelta = new Vector2(160f, 26f);
            }
            ApplyOutline(gemsMaxText, Color.white, 0.25f);

            shieldText = AddText(rt, "0", 32);
            shieldText.color = shieldColor;
            shieldText.alignment = TextAlignmentOptions.Center;
            var shieldRect = shieldText.GetComponent<RectTransform>();
            if (shieldRect != null)
            {
                shieldRect.anchorMin = new Vector2(0.5f, 0.5f);
                shieldRect.anchorMax = new Vector2(0.5f, 0.5f);
                shieldRect.pivot = new Vector2(0.5f, 0.5f);
                shieldRect.anchoredPosition = new Vector2(0f, -24f);
                shieldRect.sizeDelta = new Vector2(180f, 52f);
            }
            ApplyOutline(shieldText, Color.white, 0.25f);

            shieldMaxText = AddText(rt, "0", 16);
            shieldMaxText.color = shieldColor;
            shieldMaxText.alignment = TextAlignmentOptions.Center;
            var shieldMaxRect = shieldMaxText.GetComponent<RectTransform>();
            if (shieldMaxRect != null)
            {
                shieldMaxRect.anchorMin = new Vector2(0.5f, 0.5f);
                shieldMaxRect.anchorMax = new Vector2(0.5f, 0.5f);
                shieldMaxRect.pivot = new Vector2(0.5f, 0.5f);
                shieldMaxRect.anchoredPosition = new Vector2(0f, -50f);
                shieldMaxRect.sizeDelta = new Vector2(160f, 24f);
            }
            ApplyOutline(shieldMaxText, Color.white, 0.25f);

            if (gemIconSprite != null)
                AddStatIcon(rt, "GemMoonGemIcon", gemIconSprite, new Vector2(StatIconCenterX, GemsIconCenterY), gemsColor);
            if (shieldIconSprite != null)
                AddStatIcon(rt, "GemMoonShieldIcon", shieldIconSprite, new Vector2(StatIconCenterX, ShieldIconCenterY), shieldColor);

            BuildMoonDockActionButtons(rt);

            rootRect = rt;
            UpdatePanelPlacement();
        }

        /// <summary>World-space chips to the right of the main gem count — opens orbit station Cards / Ships panels.</summary>
        private void BuildMoonDockActionButtons(RectTransform panel)
        {
            moonDockButtonsRoot = new GameObject("MoonDockActions");
            moonDockButtonsRoot.transform.SetParent(panel, false);
            var rowRt = moonDockButtonsRoot.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0.5f, 0.5f);
            rowRt.anchorMax = new Vector2(0.5f, 0.5f);
            rowRt.pivot = new Vector2(0f, 0.5f);
            // Tighter to stats column and slightly lower so the stack sits nearer the moon in view.
            rowRt.anchoredPosition = new Vector2(62f, 8f);
            rowRt.sizeDelta = new Vector2(50f, 46f);
            var vlg = moonDockButtonsRoot.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;

            CreateMoonDockChipButton(moonDockButtonsRoot.transform, "Cards", shipsPanel: false);
            CreateMoonDockChipButton(moonDockButtonsRoot.transform, "Ships", shipsPanel: true);
            moonDockButtonsRoot.SetActive(false);
        }

        private static void CreateMoonDockChipButton(Transform parent, string label, bool shipsPanel)
        {
            var go = new GameObject("MoonBtn_" + label);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(46f, 20f);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.14f, 0.2f, 0.34f, 0.94f);
            img.raycastTarget = true;
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                var ui = OrbitStationUI.GetOrCreate();
                ui.OpenGemMoonDockPanelFromWorld(shipsPanel);
            });
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var tr = textGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(2f, 1f);
            tr.offsetMax = new Vector2(-2f, -1f);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 10;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
        }

        private static void AddStatIcon(Transform parent, string name, Sprite sprite, Vector2 anchoredPosition, Color tintColor)
        {
            if (sprite == null) return;
            var iconGo = new GameObject(name);
            iconGo.transform.SetParent(parent, false);
            var img = iconGo.AddComponent<Image>();
            img.sprite = sprite;
            img.color = tintColor;
            img.preserveAspect = true;
            img.raycastTarget = false;
            var rect = iconGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(StatIconSize, StatIconSize);
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
            text.raycastTarget = false;
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
            if (HUDController.ShipUpgradeTreeObscuresHud)
            {
                if (canvas != null && canvas.gameObject.activeSelf)
                    canvas.gameObject.SetActive(false);
                return;
            }
            if (canvas != null && !canvas.gameObject.activeSelf)
                canvas.gameObject.SetActive(true);
            UpdatePanelPlacement();
            if (rootRect != null)
                rootRect.localRotation = Quaternion.Euler(90f, 0f, 0f);

            if (moonDockButtonsRoot != null && moon.Planet != null)
            {
                bool showDock = false;
                foreach (var s in Starship.AllStarships)
                {
                    if (s != null && s.IsOwner && s.IsGemMoonDockedAtPlanet(moon.Planet))
                    {
                        showDock = true;
                        break;
                    }
                }
                if (moonDockButtonsRoot.activeSelf != showDock)
                    moonDockButtonsRoot.SetActive(showDock);
            }

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
            if (shieldText != null)
                shieldText.text = Mathf.RoundToInt(moon.GetShieldPointsForDisplay()).ToString();
            if (shieldMaxText != null)
                shieldMaxText.text = Mathf.RoundToInt(moon.GetMaxShieldPointsForDisplay()).ToString();

            if (canvas != null && canvas.worldCamera == null)
                canvas.worldCamera = UnityEngine.Camera.main;
        }

        public void Refresh()
        {
            lastRefresh = -999f;
        }

        /// <summary>
        /// World-space gem/shield labels are display-only. Dock chips (Cards/Ships) still count as UI for input blocking.
        /// </summary>
        public static bool PointerHitBlocksCombatInput(GameObject hitObject)
        {
            if (hitObject == null) return false;
            Transform t = hitObject.transform;
            while (t != null)
            {
                if (t.name.StartsWith("MoonBtn_") || t.name == "MoonDockActions")
                    return true;
                if (t.GetComponent<GemMoonStatsDisplay>() != null)
                    return false;
                t = t.parent;
            }
            return false;
        }
    }
}

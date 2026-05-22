using TMPro;
using UnityEngine;
using UnityEngine.UI;
using TitanOrbit.Entities;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Modal shown when map-instance memory has a saved ship: rescued loadout vs fresh Level 1 starter.
    /// </summary>
    public static class RescuedShipChoiceUI
    {
        private static GameObject root;
        private static TextMeshProUGUI bodyText;
        private static Starship pendingShip;

        public static void Show(Starship ship, int savedShipLevel, int savedCardCount)
        {
            if (ship == null) return;
            pendingShip = ship;
            EnsureModal();
            if (bodyText != null)
            {
                string cardsLine = savedCardCount > 0 ? $" with {savedCardCount} equipped card{(savedCardCount == 1 ? "" : "s")}" : "";
                bodyText.text =
                    $"Your last ship in this match is still available (Level {savedShipLevel}{cardsLine}).\n\n" +
                    "Use your rescued ship, or start fresh with a Level 1 starter?";
            }
            root.SetActive(true);
            root.transform.SetAsLastSibling();
        }

        public static void Hide()
        {
            pendingShip = null;
            if (root != null)
                root.SetActive(false);
        }

        private static void EnsureModal()
        {
            if (root != null) return;

            Canvas canvas = Object.FindFirstObjectByType<Canvas>();
            Transform parent = canvas != null ? canvas.transform : null;
            if (parent == null)
            {
                Debug.LogWarning("[RescuedShipChoiceUI] No Canvas found; cannot show rescued ship prompt.");
                return;
            }

            root = new GameObject("RescuedShipChoice");
            root.transform.SetParent(parent, false);
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            var backdrop = new GameObject("Backdrop");
            backdrop.transform.SetParent(root.transform, false);
            var bdRt = backdrop.AddComponent<RectTransform>();
            bdRt.anchorMin = Vector2.zero;
            bdRt.anchorMax = Vector2.one;
            bdRt.offsetMin = Vector2.zero;
            bdRt.offsetMax = Vector2.zero;
            var bdImg = backdrop.AddComponent<Image>();
            bdImg.color = new Color(0.02f, 0.04f, 0.08f, 0.78f);
            bdImg.raycastTarget = true;

            var panel = new GameObject("Panel");
            panel.transform.SetParent(root.transform, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(420f, 200f);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.1f, 0.12f, 0.18f, 0.98f);

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panel.transform, false);
            var titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -12f);
            titleRt.sizeDelta = new Vector2(-32f, 28f);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Rescued ship";
            titleTmp.fontSize = 20;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.color = new Color(0.95f, 0.97f, 1f, 1f);
            if (TMP_Settings.defaultFontAsset != null) titleTmp.font = TMP_Settings.defaultFontAsset;

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(panel.transform, false);
            var bodyRt = bodyGo.AddComponent<RectTransform>();
            bodyRt.anchorMin = new Vector2(0f, 0f);
            bodyRt.anchorMax = new Vector2(1f, 1f);
            bodyRt.offsetMin = new Vector2(20f, 56f);
            bodyRt.offsetMax = new Vector2(-20f, -44f);
            bodyText = bodyGo.AddComponent<TextMeshProUGUI>();
            bodyText.text = "";
            bodyText.fontSize = 14;
            bodyText.alignment = TextAlignmentOptions.Top;
            bodyText.enableWordWrapping = true;
            bodyText.color = new Color(0.82f, 0.88f, 0.96f, 0.98f);
            if (TMP_Settings.defaultFontAsset != null) bodyText.font = TMP_Settings.defaultFontAsset;

            var row = new GameObject("Buttons");
            row.transform.SetParent(panel.transform, false);
            var rowRt = row.AddComponent<RectTransform>();
            rowRt.anchorMin = new Vector2(0f, 0f);
            rowRt.anchorMax = new Vector2(1f, 0f);
            rowRt.pivot = new Vector2(0.5f, 0f);
            rowRt.anchoredPosition = new Vector2(0f, 14f);
            rowRt.sizeDelta = new Vector2(-32f, 36f);
            var rowH = row.AddComponent<HorizontalLayoutGroup>();
            rowH.spacing = 12f;
            rowH.childAlignment = TextAnchor.MiddleCenter;
            rowH.childControlWidth = true;
            rowH.childControlHeight = true;
            rowH.childForceExpandWidth = true;
            rowH.childForceExpandHeight = true;

            CreateButton(row.transform, "Use rescued ship", new Color(0.18f, 0.42f, 0.28f, 0.98f), OnUseRescued);
            CreateButton(row.transform, "Start fresh (Lv. 1)", new Color(0.22f, 0.24f, 0.32f, 0.98f), OnStartFresh);

            root.SetActive(false);
        }

        private static void CreateButton(Transform parent, string label, Color bg, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160f, 36f);
            var img = go.AddComponent<Image>();
            img.color = bg;
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(onClick);

            var txtGo = new GameObject("Text");
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.AddComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(8f, 4f);
            txtRt.offsetMax = new Vector2(-8f, -4f);
            var tmp = txtGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 13;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
        }

        private static void OnUseRescued()
        {
            var ship = pendingShip;
            Hide();
            ship?.SubmitMapInstanceSpawnChoiceFromClient(useRescuedShip: true);
        }

        private static void OnStartFresh()
        {
            var ship = pendingShip;
            Hide();
            ship?.SubmitMapInstanceSpawnChoiceFromClient(useRescuedShip: false);
        }
    }
}

using TitanOrbit.ECS;
using TitanOrbit.Game;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Compact owner overlay while flying a MEGA: lock guns and kick all gunners.
    /// Dark space-gamer chrome, corner HUD — not a settings form.
    /// </summary>
    [DefaultExecutionOrder(66992)]
    public class MegaShipOwnerHud : MonoBehaviour
    {
        Canvas _canvas;
        RectTransform _root;
        TextMeshProUGUI _status;
        Button _lockButton;
        Button _kickButton;
        TextMeshProUGUI _lockLabel;

        /// <summary>[UNITY] Spawn a DontDestroyOnLoad host after the first scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<MegaShipOwnerHud>() != null)
                return;

            var go = new GameObject(nameof(MegaShipOwnerHud));
            DontDestroyOnLoad(go);
            go.AddComponent<MegaShipOwnerHud>();
        }

        /// <summary>Build the corner overlay.</summary>
        void Awake() => BuildHud();

        /// <summary>Show only while the local ship is a live MEGA.</summary>
        void LateUpdate()
        {
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                SetVisible(false);
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated || !EcsGameBridge.IsNetworkInGame())
            {
                SetVisible(false);
                return;
            }

            var em = world.EntityManager;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship)
                || !em.HasComponent<MegaShipState>(ship))
            {
                SetVisible(false);
                return;
            }

            var mega = em.GetComponentData<MegaShipState>(ship);
            if (!mega.IsMega)
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            int occupied = 0;
            if (em.HasBuffer<MegaShipGunnerSlotElement>(ship))
            {
                var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(ship);
                for (int i = 0; i < gunners.Length; i++)
                {
                    if (gunners[i].OccupiedByNetworkId != 0)
                        occupied++;
                }
            }

            if (_status != null)
                _status.text = mega.GunsLocked
                    ? "GUNS LOCKED"
                    : occupied > 0 ? $"GUNNERS {occupied}" : "GUNS OPEN";
            if (_lockLabel != null)
                _lockLabel.text = mega.GunsLocked ? "UNLOCK GUNS" : "LOCK GUNS";
        }

        void BuildHud()
        {
            var canvasGo = new GameObject("MegaShipOwnerCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 122;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();

            var rootGo = new GameObject("OwnerPanel", typeof(RectTransform));
            rootGo.transform.SetParent(canvasGo.transform, false);
            _root = rootGo.GetComponent<RectTransform>();
            _root.anchorMin = new Vector2(1f, 1f);
            _root.anchorMax = new Vector2(1f, 1f);
            _root.pivot = new Vector2(1f, 1f);
            _root.anchoredPosition = new Vector2(-16f, -16f);
            _root.sizeDelta = new Vector2(200f, 118f);
            var panel = rootGo.AddComponent<Image>();
            panel.color = new Color(0.012f, 0.016f, 0.028f, 0.92f);

            _status = CreateLabel(rootGo.transform, "Status", new Vector2(0f, -8f), "MEGA");
            _lockButton = CreateButton(rootGo.transform, "Lock", new Vector2(0f, -42f), "LOCK GUNS", () =>
            {
                bool locked = _lockLabel != null && _lockLabel.text.StartsWith("UNLOCK");
                MegaShipGunRpcClient.RequestSetLocked(!locked);
            });
            _lockLabel = _lockButton.GetComponentInChildren<TextMeshProUGUI>();
            _kickButton = CreateButton(rootGo.transform, "Kick", new Vector2(0f, -80f), "KICK ALL", () =>
                MegaShipGunRpcClient.RequestKick(255));

            SetVisible(false);
        }

        static TextMeshProUGUI CreateLabel(Transform parent, string name, Vector2 pos, string text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(-16f, 22f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 13f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = new Color(0.62f, 0.78f, 0.95f, 1f);
            tmp.text = text;
            tmp.raycastTarget = false;
            return tmp;
        }

        static Button CreateButton(Transform parent, string name, Vector2 pos, string label, UnityEngine.Events.UnityAction click)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(176f, 28f);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.04f, 0.08f, 0.14f, 0.96f);
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(click);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 13f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = new Color(0.88f, 0.92f, 0.98f, 1f);
            tmp.text = label;
            tmp.raycastTarget = false;
            return button;
        }

        void SetVisible(bool visible)
        {
            if (_root != null)
                _root.gameObject.SetActive(visible);
        }
    }
}

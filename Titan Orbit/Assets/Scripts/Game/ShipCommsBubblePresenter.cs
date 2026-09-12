using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] World-space keyword chips above a speaking ship. One bubble per
    /// <c>GhostOwner.NetworkId</c> — a new callout replaces the old one.
    /// <para>
    /// Client presentation only. Driven by <see cref="ShipCommsInbox"/> (RPC echo) and by
    /// <see cref="Show"/> for the speaker's optimistic local preview. Anchors through
    /// <see cref="ShipWeaponProxyRegistry"/> so we follow the wrapped hull transform
    /// (display = sim; no extra wrap tiles).
    /// </para>
    /// Regular nameplates sit world −Z (screen-below). These chips sit world +Z (screen-above)
    /// on the same play-plane rotation (<c>Euler(-90,0,0)</c>, facing +Y) so they do not tilt
    /// toward the camera. Borders are a sliced AA frame Image — not UGUI <c>Outline</c>,
    /// which crawls while the ship flies. Execution order 67012: after
    /// <see cref="EcsWorldVisualizer"/> and nameplates.
    /// </summary>
    [DefaultExecutionOrder(67012)]
    public sealed class ShipCommsBubblePresenter : MonoBehaviour
    {
        const float LifetimeSeconds = 4f;
        const float FadeSeconds = 0.65f;
        /// <summary>
        /// Same tiny Y lift as <see cref="ShipWorldNameplate"/> so chips sit on the play plane,
        /// not tilted toward the camera.
        /// </summary>
        const float HeightAbovePlane = 0.08f;

        /// <summary>Gap past the hull edge on the screen-above (+Z) side, beyond the chip half-height.</summary>
        const float PaddingPastHull = 0.7f;

        const float FallbackXzRadius = 0.7f;
        const float WorldCanvasScale = 0.013f;

        /// <summary>
        /// Nameplates use world −Z as screen-below. Chips sit on the opposite side so they
        /// read as “above the ship” without covering the plate.
        /// </summary>
        static readonly Vector3 ScreenAboveWorld = new Vector3(0f, 0f, 1f);

        /// <summary>
        /// Same wide HUD button as <c>ShipCommsPanel</c> tiles (100×26), not a square.
        /// Long words grow past this minimum so NO PROBLEM / TRANSPORT stay on one line.
        /// </summary>
        const float ChipWidth = 100f;
        const float ChipHeight = 26f;
        const float ChipGap = 4f;
        const float ChipPadX = 8f;
        const float FrameInset = 2f;
        const int WorldSortingOrder = 5010;

        static readonly Color ChipFill = new Color(0.03f, 0.05f, 0.09f, 0.96f);
        static readonly Color ChipFrame = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color ChipText = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color ChipOutline = new Color(0.02f, 0.04f, 0.08f, 0.85f);
        static readonly Color ChipCaret = new Color(0.35f, 0.72f, 0.95f, 0.95f);

        static ShipCommsBubblePresenter s_Instance;
        static Sprite s_PlateSprite;

        readonly Dictionary<int, Bubble> _live = new Dictionary<int, Bubble>(16);
        readonly List<int> _deadIds = new List<int>(8);
        Camera _cachedCamera;

        /// <summary>One player's active chip row.</summary>
        sealed class Bubble
        {
            public int NetworkId;
            public GameObject Root;
            public Canvas WorldCanvas;
            public RectTransform CanvasRect;
            public CanvasGroup Group;
            public readonly TextMeshProUGUI[] Labels = new TextMeshProUGUI[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly GameObject[] Chips = new GameObject[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly LayoutElement[] ChipLayouts = new LayoutElement[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly RectTransform[] ChipRects = new RectTransform[ShipCommsKeywordCatalog.MaxSequenceLength];
            public float Age;
            public byte Count;
            public bool HasLocalCenter;
            public Vector3 LocalXzCenter;
        }

        /// <summary>
        /// [UNITY] Creates the presenter once after the first scene load.
        /// Do not wrap this in <c>#if UNITY_SERVER</c> — the Editor Dedicated Server build
        /// target defines that symbol and would strip chips in Play Mode.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (FindFirstObjectByType<ShipCommsBubblePresenter>() != null)
                return;

            var go = new GameObject(nameof(ShipCommsBubblePresenter));
            DontDestroyOnLoad(go);
            go.AddComponent<ShipCommsBubblePresenter>();
        }

        /// <summary>Caches the singleton so <see cref="Show"/> can run before the first LateUpdate.</summary>
        void Awake()
        {
            s_Instance = this;
        }

        /// <summary>Drops live bubbles so a second Play does not keep stale world canvases.</summary>
        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;

            foreach (var pair in _live)
            {
                if (pair.Value?.Root != null)
                    Destroy(pair.Value.Root);
            }

            _live.Clear();
        }

        /// <summary>
        /// Paints chips above <paramref name="networkId"/>'s hull. Replaces any bubble
        /// already showing for that player and restarts the 4s timer.
        /// </summary>
        public static void Show(int networkId, byte count, byte k0, byte k1, byte k2)
        {
            if (s_Instance == null)
                EnsureExists();
            if (s_Instance == null)
                return;

            s_Instance.ApplyCallout(networkId, count, k0, k1, k2);
        }

        /// <summary>
        /// Drains the RPC inbox, then parks every live bubble above its hull.
        /// </summary>
        void LateUpdate()
        {
            // --- Inbox ---
            // [HYBRID] Client simulation enqueued rows; we Instantiates UI on the main thread.
            while (ShipCommsInbox.TryDequeue(out ShipCommsInbox.Callout callout))
                ApplyCallout(callout.NetworkId, callout.Count, callout.K0, callout.K1, callout.K2);

            if (_live.Count <= 0)
                return;

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;

            float dt = Time.deltaTime;
            _deadIds.Clear();

            foreach (var pair in _live)
            {
                Bubble bubble = pair.Value;
                bubble.Age += dt;

                // --- Expire ---
                if (bubble.Age >= LifetimeSeconds || bubble.Root == null)
                {
                    _deadIds.Add(pair.Key);
                    continue;
                }

                // --- Fade on the last slice of the lifetime ---
                if (bubble.Group != null)
                {
                    float fadeStart = LifetimeSeconds - FadeSeconds;
                    float alpha = bubble.Age < fadeStart
                        ? 1f
                        : 1f - Mathf.Clamp01((bubble.Age - fadeStart) / FadeSeconds);
                    bubble.Group.alpha = alpha;
                }

                if (!TryFollowHull(bubble))
                    _deadIds.Add(pair.Key);
            }

            for (int i = 0; i < _deadIds.Count; i++)
                DestroyBubble(_deadIds[i]);
        }

        /// <summary>Creates or recycles a bubble and writes the keyword labels.</summary>
        void ApplyCallout(int networkId, byte count, byte k0, byte k1, byte k2)
        {
            if (networkId <= 0 || count < 1)
                return;

            if (!_live.TryGetValue(networkId, out Bubble bubble) || bubble == null || bubble.Root == null)
            {
                bubble = CreateBubble(networkId);
                _live[networkId] = bubble;
            }

            bubble.Age = 0f;
            bubble.Count = count;
            if (bubble.Group != null)
                bubble.Group.alpha = 1f;

            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            float width = 0f;
            width += ApplyChip(bubble, 0, count >= 1, k0, catalog);
            width += ApplyChip(bubble, 1, count >= 2, k1, catalog);
            width += ApplyChip(bubble, 2, count >= 3, k2, catalog);
            width += Mathf.Max(0, count - 1) * ChipGap;
            if (bubble.CanvasRect != null)
                bubble.CanvasRect.sizeDelta = new Vector2(width, ChipHeight + 8f);

            TryFollowHull(bubble);
        }

        /// <summary>
        /// Shows or hides one chip, writes its label, and sizes it like a panel button.
        /// Returns the chip width used for the row (0 when hidden).
        /// </summary>
        static float ApplyChip(Bubble bubble, int slot, bool visible, byte index, ShipCommsKeywordCatalog catalog)
        {
            GameObject chip = bubble.Chips[slot];
            if (chip != null)
                chip.SetActive(visible);

            if (!visible || bubble.Labels[slot] == null)
                return 0f;

            string text = catalog.TryGetLabel(index, out string label) ? label : "?";
            TextMeshProUGUI tmp = bubble.Labels[slot];
            tmp.text = text.ToUpperInvariant();
            tmp.ForceMeshUpdate();

            float width = Mathf.Max(ChipWidth, tmp.preferredWidth + ChipPadX * 2f);
            if (bubble.ChipLayouts[slot] != null)
            {
                bubble.ChipLayouts[slot].preferredWidth = width;
                bubble.ChipLayouts[slot].minWidth = width;
            }

            if (bubble.ChipRects[slot] != null)
                bubble.ChipRects[slot].sizeDelta = new Vector2(width, ChipHeight);

            return width;
        }

        /// <summary>
        /// Parks the bubble on the play plane, screen-above the hull. Same world rotation as
        /// <see cref="ShipWorldNameplate"/>: flat on XZ, facing +Y, no yaw with the ship.
        /// </summary>
        bool TryFollowHull(Bubble bubble)
        {
            if (!ShipWeaponProxyRegistry.TryGetHull(bubble.NetworkId, out Transform hull) || hull == null)
                return false;

            float xzRadius = FallbackXzRadius;
            if (ShipWeaponProxyRegistry.TryGetCachedHullClearance(bubble.NetworkId, out _, out float cachedXz)
                && cachedXz > 0.001f)
                xzRadius = Mathf.Max(FallbackXzRadius, cachedXz);

            EnsureLocalHullCenter(hull, bubble);
            Vector3 centerWorld = hull.TransformPoint(bubble.LocalXzCenter);

            // [TITAN-ORBIT] Nameplates sit world −Z (screen-below). +Z keeps chips readable
            // on the opposite side of the hull, still on the play plane. Add half the canvas
            // height so the near edge clears the hull (the pivot is the chip row center).
            // Anchor XZ from mesh bounds center — hull.position is often off the visual midline.
            float halfChipWorld = (ChipHeight + 8f) * WorldCanvasScale * 0.5f;
            Vector3 pos = centerWorld
                + ScreenAboveWorld * (xzRadius + PaddingPastHull + halfChipWorld);
            pos.y = centerWorld.y + HeightAbovePlane;

            // [TITAN-ORBIT] Euler −90 X lays the canvas on XZ. Negative Y scale un-mirrors
            // UI after that tilt — same trick as the nameplate label root.
            bubble.Root.transform.SetPositionAndRotation(pos, Quaternion.Euler(-90f, 0f, 0f));
            bubble.Root.transform.localScale = new Vector3(
                WorldCanvasScale, -WorldCanvasScale, WorldCanvasScale);

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;
            if (bubble.WorldCanvas != null && _cachedCamera != null
                && bubble.WorldCanvas.worldCamera != _cachedCamera)
                bubble.WorldCanvas.worldCamera = _cachedCamera;

            return true;
        }

        /// <summary>Builds a world-space canvas with three reusable chips.</summary>
        Bubble CreateBubble(int networkId)
        {
            var root = new GameObject("ShipCommsBubble_" + networkId);
            root.transform.SetParent(null, true);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = WorldSortingOrder;
            canvas.pixelPerfect = false;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 32f;
            root.AddComponent<GraphicRaycaster>().enabled = false;

            var group = root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            var rect = root.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ChipWidth, ChipHeight + 8f);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f));
            root.transform.localScale = new Vector3(
                WorldCanvasScale, -WorldCanvasScale, WorldCanvasScale);

            var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(root.transform, false);
            var rowRt = row.GetComponent<RectTransform>();
            rowRt.anchorMin = Vector2.zero;
            rowRt.anchorMax = Vector2.one;
            rowRt.offsetMin = Vector2.zero;
            rowRt.offsetMax = Vector2.zero;
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = ChipGap;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = false;
            h.childControlHeight = false;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;

            var bubble = new Bubble
            {
                NetworkId = networkId,
                Root = root,
                WorldCanvas = canvas,
                CanvasRect = rect,
                Group = group,
            };

            for (int i = 0; i < ShipCommsKeywordCatalog.MaxSequenceLength; i++)
            {
                var chipGo = new GameObject("Chip" + i, typeof(RectTransform), typeof(LayoutElement));
                chipGo.transform.SetParent(row.transform, false);
                var chipRt = chipGo.GetComponent<RectTransform>();
                chipRt.sizeDelta = new Vector2(ChipWidth, ChipHeight);
                var le = chipGo.GetComponent<LayoutElement>();
                le.preferredWidth = ChipWidth;
                le.preferredHeight = ChipHeight;
                le.minWidth = ChipWidth;
                le.minHeight = ChipHeight;

                // Cyan frame first (behind). Sliced AA sprite — not UGUI Outline, which jitters in flight.
                var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
                frameGo.transform.SetParent(chipGo.transform, false);
                Stretch(frameGo.GetComponent<RectTransform>(), 0f);
                StylePlate(frameGo.GetComponent<Image>(), ChipFrame);

                var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fillGo.transform.SetParent(chipGo.transform, false);
                Stretch(fillGo.GetComponent<RectTransform>(), FrameInset);
                StylePlate(fillGo.GetComponent<Image>(), ChipFill);

                var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
                caretGo.transform.SetParent(chipGo.transform, false);
                var caretRt = caretGo.GetComponent<RectTransform>();
                caretRt.anchorMin = new Vector2(0f, 0f);
                caretRt.anchorMax = new Vector2(0f, 1f);
                caretRt.pivot = new Vector2(0f, 0.5f);
                caretRt.sizeDelta = new Vector2(2f, -FrameInset * 2f);
                caretRt.anchoredPosition = new Vector2(FrameInset, 0f);
                StylePlate(caretGo.GetComponent<Image>(), ChipCaret);

                var label = CreateWorldLabel(chipGo.transform, "Label");
                bubble.Chips[i] = chipGo;
                bubble.ChipLayouts[i] = le;
                bubble.ChipRects[i] = chipRt;
                bubble.Labels[i] = label;
            }

            return bubble;
        }

        /// <summary>TMP chip label using the shared Rajdhani HUD font when present.</summary>
        static TextMeshProUGUI CreateWorldLabel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(ChipPadX, 2f);
            rt.offsetMax = new Vector2(-ChipPadX, -2f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 11f;
            tmp.color = ChipText;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.outlineWidth = 0.18f;
            tmp.outlineColor = ChipOutline;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            return tmp;
        }

        /// <summary>Sliced AA plate so the border stays put while the hull moves.</summary>
        static void StylePlate(Image image, Color color)
        {
            image.sprite = GetPlateSprite();
            image.type = Image.Type.Sliced;
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>
        /// 16×16 white sprite with a 1px transparent rim. Bilinear filtering turns that rim
        /// into a stable AA edge — UGUI <c>Outline</c> drew 4 offset copies that shimmered.
        /// </summary>
        static Sprite GetPlateSprite()
        {
            if (s_PlateSprite != null)
                return s_PlateSprite;

            const int dim = 16;
            var tex = new Texture2D(dim, dim, TextureFormat.RGBA32, false);
            tex.name = "ShipCommsChipPlateTex";
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 0;

            var pixels = new Color32[dim * dim];
            var clear = new Color32(255, 255, 255, 0);
            var solid = new Color32(255, 255, 255, 255);
            for (int y = 0; y < dim; y++)
            {
                for (int x = 0; x < dim; x++)
                {
                    bool rim = x == 0 || y == 0 || x == dim - 1 || y == dim - 1;
                    pixels[y * dim + x] = rim ? clear : solid;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            s_PlateSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, dim, dim),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(1f, 1f, 1f, 1f));
            s_PlateSprite.name = "ShipCommsChipPlateSprite";
            return s_PlateSprite;
        }

        static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>
        /// Caches the mesh AABB center in hull-local XZ so chips sit over the visual
        /// midline, not the (often offset) prefab pivot.
        /// </summary>
        static void EnsureLocalHullCenter(Transform hull, Bubble bubble)
        {
            if (bubble.HasLocalCenter || hull == null)
                return;

            Renderer[] renderers = hull.GetComponentsInChildren<Renderer>(true);
            if (WorldBodyLabelLayout.TryEncapsulateBodyBounds(renderers, out Bounds bounds))
            {
                Vector3 local = hull.InverseTransformPoint(bounds.center);
                local.y = 0f;
                bubble.LocalXzCenter = local;
            }

            bubble.HasLocalCenter = true;
        }

        /// <summary>Destroys one bubble GameObject and forgets the NetworkId mapping.</summary>
        void DestroyBubble(int networkId)
        {
            if (!_live.TryGetValue(networkId, out Bubble bubble))
                return;

            _live.Remove(networkId);
            if (bubble?.Root != null)
                Destroy(bubble.Root);
        }
    }
}

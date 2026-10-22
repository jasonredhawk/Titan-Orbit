using TitanOrbit.Core;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Two small switches on the Your Ship picture. One shows or hides the moon,
    /// stars, and nearby hulls. The other turns the slow camera orbit on or off.
    /// Both write the same GameManager checkboxes, which are stored in PlayerPrefs.
    /// </summary>
    public sealed class OrbitMenuPortraitIconToggles : MonoBehaviour
    {
        const float IconSize = 22f;
        const float Gap = 4f;

        static Sprite s_Chip;
        static Sprite s_SceneIcon;
        static Sprite s_SpinIcon;

        Button _sceneButton;
        Button _spinButton;
        Image _sceneChip;
        Image _spinChip;
        Image _sceneIcon;
        Image _spinIcon;

        static readonly Color ChipOn = new Color(0.12f, 0.46f, 0.38f, 0.94f);
        static readonly Color ChipOff = new Color(0.06f, 0.08f, 0.12f, 0.72f);
        static readonly Color IconOn = Color.white;
        static readonly Color IconOff = new Color(0.72f, 0.76f, 0.82f, 0.55f);

        /// <summary>Creates the pair on the live picture, above the camera image.</summary>
        public static void Ensure(RawImage picture)
        {
            if (picture == null)
                return;

            OrbitMenuPortraitIconToggles toggles = picture.GetComponentInChildren<OrbitMenuPortraitIconToggles>(true);
            if (toggles == null)
            {
                var go = new GameObject("PortraitIconToggles", typeof(RectTransform));
                go.transform.SetParent(picture.transform, false);
                toggles = go.AddComponent<OrbitMenuPortraitIconToggles>();
            }

            toggles.transform.SetAsLastSibling();
            toggles.Refresh();
        }

        void Awake()
        {
            Build();
        }

        void OnEnable()
        {
            GameManager.PortraitCameraOptionsChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            GameManager.PortraitCameraOptionsChanged -= Refresh;
        }

        void Build()
        {
            if (_sceneButton != null)
                return;

            RectTransform root = (RectTransform)transform;
            root.anchorMin = new Vector2(1f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(1f, 1f);
            root.anchoredPosition = new Vector2(-4f, -4f);
            root.sizeDelta = new Vector2(IconSize * 2f + Gap, IconSize);

            var layoutIgnore = gameObject.GetComponent<LayoutElement>();
            if (layoutIgnore == null)
                layoutIgnore = gameObject.AddComponent<LayoutElement>();
            layoutIgnore.ignoreLayout = true;

            var row = gameObject.GetComponent<HorizontalLayoutGroup>();
            if (row == null)
                row = gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = Gap;
            row.childAlignment = TextAnchor.MiddleRight;
            row.childControlWidth = false;
            row.childControlHeight = false;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            row.padding = new RectOffset(0, 0, 0, 0);

            _sceneButton = CreateButton("SceneElements", out _sceneChip, out _sceneIcon, SceneSprite());
            _spinButton = CreateButton("AutoSpin", out _spinChip, out _spinIcon, SpinSprite());
            _sceneButton.onClick.AddListener(ToggleScene);
            _spinButton.onClick.AddListener(ToggleSpin);
        }

        Button CreateButton(string name, out Image chip, out Image icon, Sprite glyph)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(IconSize, IconSize);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = IconSize;
            le.preferredHeight = IconSize;
            le.minWidth = IconSize;
            le.minHeight = IconSize;

            chip = go.GetComponent<Image>();
            chip.sprite = ChipSprite();
            chip.type = Image.Type.Simple;
            chip.color = ChipOff;
            chip.raycastTarget = true;

            var button = go.GetComponent<Button>();
            button.targetGraphic = chip;
            button.transition = Selectable.Transition.None;

            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGo.transform.SetParent(go.transform, false);
            var iconRt = (RectTransform)iconGo.transform;
            iconRt.anchorMin = new Vector2(0.18f, 0.18f);
            iconRt.anchorMax = new Vector2(0.82f, 0.82f);
            iconRt.offsetMin = Vector2.zero;
            iconRt.offsetMax = Vector2.zero;
            icon = iconGo.GetComponent<Image>();
            icon.sprite = glyph;
            icon.color = IconOff;
            icon.raycastTarget = false;
            return button;
        }

        void ToggleScene()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null)
                return;
            manager.SetPortraitSceneElementsVisible(!manager.PortraitSceneElementsVisible);
        }

        void ToggleSpin()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null)
                return;
            manager.SetPortraitAutoRotate(!manager.PortraitAutoRotate);
        }

        void Refresh()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null || _sceneChip == null)
                return;

            Paint(_sceneChip, _sceneIcon, manager.PortraitSceneElementsVisible);
            Paint(_spinChip, _spinIcon, manager.PortraitAutoRotate);
        }

        static void Paint(Image chip, Image icon, bool on)
        {
            if (chip != null)
                chip.color = on ? ChipOn : ChipOff;
            if (icon != null)
                icon.color = on ? IconOn : IconOff;
        }

        static Sprite ChipSprite()
        {
            if (s_Chip != null)
                return s_Chip;
            s_Chip = DiscSprite("PortraitToggleChip", 32, 15f);
            return s_Chip;
        }

        static Sprite SceneSprite()
        {
            if (s_SceneIcon != null)
                return s_SceneIcon;

            const int size = 32;
            Color32[] pixels = new Color32[size * size];
            StampDisc(pixels, size, 12f, 14f, 8f, 255);
            StampDisc(pixels, size, 16f, 17f, 6.2f, 0);
            StampStar(pixels, size, 23f, 23f, 3.2f);
            StampStar(pixels, size, 22f, 12f, 2.2f);
            s_SceneIcon = Finish("PortraitSceneIcon", pixels, size);
            return s_SceneIcon;
        }

        static Sprite SpinSprite()
        {
            if (s_SpinIcon != null)
                return s_SpinIcon;

            const int size = 32;
            Color32[] pixels = new Color32[size * size];
            float cx = 16f;
            float cy = 15f;
            for (int deg = 30; deg <= 300; deg += 4)
            {
                float rad = deg * Mathf.Deg2Rad;
                StampDisc(pixels, size, cx + Mathf.Cos(rad) * 9f, cy + Mathf.Sin(rad) * 9f, 1.6f, 255);
            }

            float tip = 28f * Mathf.Deg2Rad;
            float tx = cx + Mathf.Cos(tip) * 9f;
            float ty = cy + Mathf.Sin(tip) * 9f;
            StampDisc(pixels, size, tx + 3.2f, ty + 0.4f, 2.3f, 255);
            StampDisc(pixels, size, tx + 0.2f, ty + 3.4f, 2.3f, 255);
            s_SpinIcon = Finish("PortraitSpinIcon", pixels, size);
            return s_SpinIcon;
        }

        static Sprite DiscSprite(string name, int size, float radius)
        {
            Color32[] pixels = new Color32[size * size];
            StampDisc(pixels, size, size * 0.5f, size * 0.5f, radius, 255);
            return Finish(name, pixels, size);
        }

        static void StampDisc(Color32[] pixels, int size, float cx, float cy, float radius, byte alpha)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius - 1f));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius + 1f));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius - 1f));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius + 1f));
            float r2 = radius * radius;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy > r2)
                        continue;
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
        }

        static void StampStar(Color32[] pixels, int size, float cx, float cy, float radius)
        {
            StampDisc(pixels, size, cx, cy, radius * 0.45f, 255);
            int arm = Mathf.CeilToInt(radius);
            for (int i = -arm; i <= arm; i++)
            {
                Plot(pixels, size, cx + i, cy, 255);
                Plot(pixels, size, cx, cy + i, 255);
            }
        }

        static void Plot(Color32[] pixels, int size, float x, float y, byte alpha)
        {
            int ix = Mathf.RoundToInt(x);
            int iy = Mathf.RoundToInt(y);
            if (ix < 0 || iy < 0 || ix >= size || iy >= size)
                return;
            pixels[iy * size + ix] = new Color32(255, 255, 255, alpha);
        }

        static Sprite Finish(string name, Color32[] pixels, int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = name;
            return sprite;
        }
    }
}

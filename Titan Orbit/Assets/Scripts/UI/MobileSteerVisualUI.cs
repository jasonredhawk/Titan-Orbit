using UnityEngine;
using UnityEngine.UI;
using TitanOrbit.Input;

namespace TitanOrbit.UI
{
    /// <summary>
    /// UGUI steer feedback: anchor dot, line to finger (white rotate / green thrust), and a semi-transparent thrust-radius ring.
    /// Does not rely on Shapes immediate mode so it always renders on the mobile canvas.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class MobileSteerVisualUI : MonoBehaviour
    {
        [Header("Style")]
        [SerializeField] private float lineThicknessPx = 5f;
        [SerializeField] private float dotSizePx = 14f;
        [SerializeField] private Color dotColor = new Color(1f, 1f, 1f, 0.95f);
        [SerializeField] private Color ringColor = new Color(1f, 1f, 1f, 0.38f);
        [SerializeField] private Color lineRotateColor = Color.white;
        [SerializeField] private Color lineThrustColor = new Color(0.25f, 0.95f, 0.42f, 1f);
        [SerializeField] private Color lineFaintColor = new Color(1f, 1f, 1f, 0.4f);
        [SerializeField] private int ringTextureSize = 128;
        [SerializeField] private float ringStrokePx = 4f;

        [Header("Stability")]
        [Tooltip("Higher = less jitter on anchor dot / ring (camera-space UI or noisy touches).")]
        [SerializeField] private float anchorVisualSmoothing = 14f;
        [Tooltip("Higher = smoother line end; slightly lower than finger can feel laggy.")]
        [SerializeField] private float fingerVisualSmoothing = 22f;

        private CanvasGroup _canvasGroup;
        private RectTransform _rootRt;
        private RectTransform _ringRt;
        private Image _ringImg;
        private RectTransform _lineRt;
        private Image _lineImg;
        private RectTransform _dotRt;
        private Image _dotImg;

        private static Sprite s_whiteSprite;
        private static Sprite s_ringSprite;

        private Vector2 _smoothedAnchorLocal;
        private Vector2 _smoothedFingerLocal;
        private bool _steerVisualPrimed;

        private void Awake()
        {
            // --- Unity lifecycle ---
            _rootRt = transform as RectTransform;
            _canvasGroup = gameObject.GetComponent<CanvasGroup>();
            if (_canvasGroup == null)
                _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;
            _canvasGroup.ignoreParentGroups = true;

            EnsureWhiteSprite();
            EnsureRingSprite();

            _ringRt = CreateUiChild("ThrustRing", transform);
            _ringImg = _ringRt.gameObject.AddComponent<Image>();
            _ringImg.sprite = s_ringSprite;
            _ringImg.type = Image.Type.Simple;
            _ringImg.preserveAspect = true;
            _ringImg.color = ringColor;
            _ringImg.raycastTarget = false;

            _lineRt = CreateUiChild("DragLine", transform);
            _lineImg = _lineRt.gameObject.AddComponent<Image>();
            _lineImg.sprite = s_whiteSprite;
            _lineImg.type = Image.Type.Simple;
            _lineImg.color = lineRotateColor;
            _lineImg.raycastTarget = false;
            _lineRt.pivot = new Vector2(0f, 0.5f);

            _dotRt = CreateUiChild("AnchorDot", transform);
            _dotImg = _dotRt.gameObject.AddComponent<Image>();
            _dotImg.sprite = s_whiteSprite;
            _dotImg.type = Image.Type.Simple;
            _dotImg.color = dotColor;
            _dotImg.raycastTarget = false;
        }

        private static RectTransform CreateUiChild(string name, Transform parent)
        {
            // --- Create instance ---
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        private static void EnsureWhiteSprite()
        {
            // --- Ensure setup ---
            if (s_whiteSprite != null) return;
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, Color.white);
            t.Apply(false, true);
            s_whiteSprite = Sprite.Create(t, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
        }

        private void EnsureRingSprite()
        {
            // --- Ensure setup ---
            if (s_ringSprite != null) return;
            int n = Mathf.Clamp(ringTextureSize, 32, 512);
            float cx = n * 0.5f;
            float cy = n * 0.5f;
            float rOut = n * 0.5f - 2f;
            float rIn = Mathf.Max(1f, rOut - ringStrokePx);
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 white = new Color32(255, 255, 255, 255);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    bool inRing = d <= rOut && d >= rIn;
                    tex.SetPixel(x, y, inRing ? white : clear);
                }
            }
            tex.Apply(false, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            s_ringSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        }

        private void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (_ringRt == null || _lineRt == null || _dotRt == null || _canvasGroup == null)
                return;

            // --- Death / expanded map: no steer chrome over the explosion or full map ---
            if (HUDController.LocalPlayerDeathHidesHud || HUDController.MinimapExpandedObscuresHud)
            {
                _canvasGroup.alpha = 0f;
                _steerVisualPrimed = false;
                return;
            }

            MobileInputHandler h = MobileInputHandler.Resolve();
            if (h == null || !h.LeftAnchorActive)
            {
                _canvasGroup.alpha = 0f;
                _steerVisualPrimed = false;
                return;
            }

            _canvasGroup.alpha = 1f;

            Canvas rootCanvas = GetComponentInParent<Canvas>()?.rootCanvas;
            RectTransform canvasRt = rootCanvas != null ? rootCanvas.transform as RectTransform : null;
            if (_rootRt == null || canvasRt == null)
                return;

            UnityEngine.Camera uiCam = rootCanvas != null && rootCanvas.renderMode == RenderMode.ScreenSpaceCamera
                ? rootCanvas.worldCamera
                : null;

            Vector2 anchorScreen = h.SteerAnchorScreenPx;
            Vector2 fingerScreen = h.SteerFingerScreenPx;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, anchorScreen, uiCam, out Vector2 canvasLocalA))
                return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, fingerScreen, uiCam, out Vector2 canvasLocalF))
                return;

            Vector3 wA = canvasRt.TransformPoint(new Vector3(canvasLocalA.x, canvasLocalA.y, 0f));
            Vector3 wF = canvasRt.TransformPoint(new Vector3(canvasLocalF.x, canvasLocalF.y, 0f));
            Vector3 aLocalRaw = _rootRt.InverseTransformPoint(wA);
            Vector3 fLocalRaw = _rootRt.InverseTransformPoint(wF);
            Vector2 aTarget = new Vector2(aLocalRaw.x, aLocalRaw.y);
            Vector2 fTarget = new Vector2(fLocalRaw.x, fLocalRaw.y);

            float dt = Time.unscaledDeltaTime;
            if (!_steerVisualPrimed)
            {
                _smoothedAnchorLocal = aTarget;
                _smoothedFingerLocal = fTarget;
                _steerVisualPrimed = true;
            }
            else
            {
                float ta = 1f - Mathf.Exp(-anchorVisualSmoothing * dt);
                float tf = 1f - Mathf.Exp(-fingerVisualSmoothing * dt);
                _smoothedAnchorLocal = Vector2.Lerp(_smoothedAnchorLocal, aTarget, ta);
                _smoothedFingerLocal = Vector2.Lerp(_smoothedFingerLocal, fTarget, tf);
            }

            float pxScale = _rootRt.rect.width / Mathf.Max(1f, Screen.width);
            float ringDiameter = h.SteerThrustRingRadiusPx * 2f * pxScale;
            float dotSize = dotSizePx * pxScale;
            float lineH = lineThicknessPx * pxScale;

            _ringRt.localPosition = new Vector3(_smoothedAnchorLocal.x, _smoothedAnchorLocal.y, 0f);
            _ringRt.sizeDelta = new Vector2(ringDiameter, ringDiameter);

            _dotRt.localPosition = new Vector3(_smoothedAnchorLocal.x, _smoothedAnchorLocal.y, 0f);
            _dotRt.sizeDelta = new Vector2(dotSize, dotSize);

            Vector2 delta = _smoothedFingerLocal - _smoothedAnchorLocal;
            float dist = delta.magnitude;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            _lineRt.localPosition = new Vector3(_smoothedAnchorLocal.x, _smoothedAnchorLocal.y, 0f);
            _lineRt.localRotation = Quaternion.Euler(0f, 0f, angle);
            _lineRt.sizeDelta = new Vector2(Mathf.Max(dist, 1f), Mathf.Max(lineH, 1f));

            float microPx = h.SteerMicroDeadzonePx;
            float dragPx = h.LeftDragDistancePixels;
            if (dragPx < microPx)
                _lineImg.color = lineFaintColor;
            else if (h.LeftThrustFromAnchor)
                _lineImg.color = lineThrustColor;
            else
                _lineImg.color = lineRotateColor;

            _ringRt.SetAsFirstSibling();
            _lineRt.SetSiblingIndex(1);
            _dotRt.SetAsLastSibling();
        }
    }
}

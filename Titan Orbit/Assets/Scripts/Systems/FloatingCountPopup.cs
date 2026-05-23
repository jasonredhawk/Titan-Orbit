using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Runtime-created world-space popup: rises upward, fades in, then fades out.
    /// </summary>
    public class FloatingCountPopup : MonoBehaviour
    {
        private const float MIN_POPUP_WORLD_Y = 4f;
        private const int TextSortingOrder = 5001;
        private const int IconSortingOrder = 5000;
        private static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        private TMP_Text tmpText;
        private SpriteRenderer iconRenderer;

        private Color baseColor = Color.white;
        private float elapsed;
        private float lifetime;
        private float riseSpeed;
        private float lockedY;
        /// <summary>Horizontal drift on the play plane (XZ), perpendicular to screen-up rise.</summary>
        private Vector3 lateralVelocity;

        private void EnsureTextAndIcon()
        {
            if (tmpText == null)
            {
                Transform textT = transform.Find("Text");
                GameObject textGo = textT != null ? textT.gameObject : new GameObject("Text");
                if (textT == null)
                    textGo.transform.SetParent(transform, false);

                var text3d = textGo.GetComponent<TextMeshPro>();
                if (text3d == null)
                    text3d = textGo.AddComponent<TextMeshPro>();
                tmpText = text3d;
            }

            if (iconRenderer == null)
            {
                Transform iconT = transform.Find("Icon");
                GameObject iconGo = iconT != null ? iconT.gameObject : new GameObject("Icon");
                if (iconT == null)
                    iconGo.transform.SetParent(transform, false);

                iconRenderer = iconGo.GetComponent<SpriteRenderer>();
                if (iconRenderer == null)
                    iconRenderer = iconGo.AddComponent<SpriteRenderer>();
            }
        }

        public void Initialize(
            string message,
            Sprite iconSprite,
            Color color,
            TMP_FontAsset font,
            float fontSize,
            float duration,
            float riseSpeed,
            float iconScale,
            Vector3 iconLocalOffset,
            float lateralDriftSpeedMax = 0f
        )
        {
            EnsureTextAndIcon();

            if (tmpText == null)
            {
                Debug.LogWarning("FloatingCountPopup: TMP_Text missing; cannot initialize popup text.");
                Destroy(gameObject);
                return;
            }

            lifetime = Mathf.Max(0.1f, duration);
            // Minimum drift so a zero/missing inspector value never looks "stuck".
            this.riseSpeed = Mathf.Max(0.15f, riseSpeed);
            lateralVelocity = Vector3.zero;
            if (lateralDriftSpeedMax > 0.0001f)
            {
                var cam = UnityEngine.Camera.main;
                Vector3 rise = GetRiseDirectionOnPlayPlane(cam);
                Vector3 lateral = Vector3.Cross(Vector3.up, rise);
                if (lateral.sqrMagnitude < 1e-8f)
                    lateral = Vector3.right;
                lateral.Normalize();
                float mag = Random.Range(lateralDriftSpeedMax * 0.35f, lateralDriftSpeedMax);
                lateralVelocity = lateral * mag * (Random.value < 0.5f ? -1f : 1f);
            }
            elapsed = 0f;
            lockedY = Mathf.Max(transform.position.y, MIN_POPUP_WORLD_Y);
            Vector3 initPos = transform.position;
            initPos.y = lockedY;
            transform.position = initPos;

            baseColor = color;
            baseColor.a = 0f;

            // Text setup.
            tmpText.text = message;
            if (font != null)
                tmpText.font = font;
            tmpText.fontSize = Mathf.Max(1f, fontSize);
            tmpText.transform.localScale = Vector3.one;
            tmpText.alignment = TextAlignmentOptions.Center;
            tmpText.enableWordWrapping = false;
            tmpText.richText = false;
            tmpText.color = baseColor;
            ApplyReadableTextMaterial(tmpText);
            // Ensure TMP generates its first mesh immediately (avoids "invisible for a few frames" issues).
            tmpText.ForceMeshUpdate();

            // Icon setup.
            if (iconSprite != null)
            {
                if (iconRenderer != null)
                {
                    iconRenderer.sprite = iconSprite;
                    iconRenderer.color = baseColor;
                    iconRenderer.transform.localPosition = iconLocalOffset;
                    iconRenderer.transform.localScale = Vector3.one * Mathf.Max(0.0001f, iconScale);
                    iconRenderer.enabled = true;
                    iconRenderer.sortingOrder = IconSortingOrder;
                }
            }
            else
            {
                if (iconRenderer != null)
                    iconRenderer.enabled = false;
            }
        }

        /// <summary>
        /// Match planet population text: outline + high render queue so popups draw above planets/ships.
        /// </summary>
        private static void ApplyReadableTextMaterial(TMP_Text text)
        {
            if (text == null)
                return;

            Material mat = text.fontMaterial;
            if (mat == null)
                return;

            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 0.85f));
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 0.2f);
            if (mat.HasProperty("_OutlineSoftness"))
                mat.SetFloat("_OutlineSoftness", 0.08f);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
        }

        /// <summary>
        /// Direction that reads as "up" on screen but stays on the XZ play plane.
        /// World +Y is nearly invisible from a top-down camera, so we use camera screen-up flattened to XZ.
        /// </summary>
        private static Vector3 GetRiseDirectionOnPlayPlane(UnityEngine.Camera cam)
        {
            if (cam == null)
                return Vector3.forward;

            Vector3 dir = cam.transform.up;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.ProjectOnPlane(-cam.transform.forward, Vector3.up);
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            return dir.normalized;
        }

        private void Update()
        {
            if (lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            var cam = UnityEngine.Camera.main;
            // Drift along screen-up on the ground plane (not world Y), plus optional lateral spread.
            transform.position += GetRiseDirectionOnPlayPlane(cam) * riseSpeed * Time.deltaTime;
            if (lateralVelocity.sqrMagnitude > 0f)
                transform.position += lateralVelocity * Time.deltaTime;
            Vector3 pos = transform.position;
            pos.y = lockedY; // Keep elevated height so popup renders above planets/ships.
            transform.position = pos;

            // Always face the main camera (billboard) so it's readable.
            if (cam != null)
            {
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
            }

            // Fade in first half, fade out second half.
            float alpha = t <= 0.5f ? (t * 2f) : (1f - (t - 0.5f) * 2f);
            alpha = Mathf.Clamp01(alpha);

            Color c = baseColor;
            c.a = alpha;
            if (tmpText != null)
                tmpText.color = c;

            if (iconRenderer != null && iconRenderer.enabled)
                iconRenderer.color = c;

            if (elapsed >= lifetime)
                Destroy(gameObject);
        }

        private void LateUpdate()
        {
            // Enforce overlay height in case another system mutates transform after Update.
            Vector3 pos = transform.position;
            if (pos.y != lockedY)
            {
                pos.y = lockedY;
                transform.position = pos;
            }
        }
    }
}


using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>Runtime-created world-space popup: rises upward, fades in, then fades out.</summary>
    public class FloatingCountPopup : MonoBehaviour
    {
        const float MinPopupWorldY = 4f;
        const int TextSortingOrder = 5001;
        const int IconSortingOrder = 5000;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        TMP_Text tmpText;
        SpriteRenderer iconRenderer;

        Color baseColor = Color.white;
        float elapsed;
        float lifetime;
        float riseSpeed;
        float lockedY;
        Vector3 lateralVelocity;
        Transform followAnchor;
        float followScreenUpOffset;
        Vector3 worldMotionOffset;

        void EnsureTextAndIcon()
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
            float lateralDriftSpeedMax = 0f,
            Transform followAnchor = null,
            float followScreenUpOffset = 0f,
            Vector3 initialWorldMotionOffset = default)
        {
            this.followAnchor = followAnchor;
            this.followScreenUpOffset = followScreenUpOffset;
            worldMotionOffset = initialWorldMotionOffset;
            EnsureTextAndIcon();

            if (tmpText == null)
            {
                Debug.LogWarning("FloatingCountPopup: TMP_Text missing; cannot initialize popup text.");
                Destroy(gameObject);
                return;
            }

            lifetime = Mathf.Max(0.1f, duration);
            this.riseSpeed = Mathf.Max(0.15f, riseSpeed);
            lateralVelocity = Vector3.zero;
            if (lateralDriftSpeedMax > 0.0001f)
            {
                var cam = Camera.main;
                Vector3 rise = GetRiseDirectionOnPlayPlane(cam);
                Vector3 lateral = Vector3.Cross(Vector3.up, rise);
                if (lateral.sqrMagnitude < 1e-8f)
                    lateral = Vector3.right;
                lateral.Normalize();
                float mag = Random.Range(lateralDriftSpeedMax * 0.35f, lateralDriftSpeedMax);
                lateralVelocity = lateral * mag * (Random.value < 0.5f ? -1f : 1f);
            }

            elapsed = 0f;
            if (followAnchor == null)
            {
                lockedY = Mathf.Max(transform.position.y, MinPopupWorldY);
                Vector3 initPos = transform.position;
                initPos.y = lockedY;
                transform.position = initPos;
            }
            else
            {
                ApplyFollowPosition();
            }

            baseColor = color;
            baseColor.a = 0f;

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
            tmpText.ForceMeshUpdate();

            if (iconSprite != null && iconRenderer != null)
            {
                iconRenderer.sprite = iconSprite;
                iconRenderer.color = baseColor;
                iconRenderer.transform.localPosition = iconLocalOffset;
                iconRenderer.transform.localScale = Vector3.one * Mathf.Max(0.0001f, iconScale);
                iconRenderer.enabled = true;
                iconRenderer.sortingOrder = IconSortingOrder;
            }
            else if (iconRenderer != null)
            {
                iconRenderer.enabled = false;
            }
        }

        static void ApplyReadableTextMaterial(TMP_Text text)
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

        static Vector3 GetRiseDirectionOnPlayPlane(Camera cam)
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

        void Update()
        {
            if (lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            var cam = Camera.main;
            Vector3 motion = GetRiseDirectionOnPlayPlane(cam) * riseSpeed * Time.deltaTime;
            if (lateralVelocity.sqrMagnitude > 0f)
                motion += lateralVelocity * Time.deltaTime;

            worldMotionOffset += motion;

            if (followAnchor == null)
            {
                transform.position += motion;
                Vector3 pos = transform.position;
                pos.y = lockedY;
                transform.position = pos;
            }

            if (cam != null)
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);

            float alpha = t <= 0.5f ? t * 2f : 1f - (t - 0.5f) * 2f;
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

        void LateUpdate()
        {
            if (followAnchor != null)
            {
                ApplyFollowPosition();
                return;
            }

            Vector3 pos = transform.position;
            if (pos.y != lockedY)
            {
                pos.y = lockedY;
                transform.position = pos;
            }
        }

        void ApplyFollowPosition()
        {
            if (followAnchor == null)
                return;

            var cam = Camera.main;
            Vector3 playUp = GetRiseDirectionOnPlayPlane(cam);
            Vector3 basePos = followAnchor.position + playUp * followScreenUpOffset;
            basePos.y = followAnchor.position.y;
            transform.position = basePos + worldMotionOffset;
        }
    }
}

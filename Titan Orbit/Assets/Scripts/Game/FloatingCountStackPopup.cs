using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    public readonly struct FloatingCountStackLine
    {
        public readonly string Text;
        public readonly Color Color;

        public FloatingCountStackLine(string text, Color color)
        {
            Text = text;
            Color = color;
        }
    }

    /// <summary>World-space popup with multiple colored lines stacked vertically; rises and fades as one unit.</summary>
    public class FloatingCountStackPopup : MonoBehaviour
    {
        const float MinPopupWorldY = 4f;
        const int TextSortingOrder = 5001;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        TMP_Text tmpText;
        Color baseColor = Color.white;
        float elapsed;
        float lifetime;
        float riseSpeed;
        float lockedY;
        Vector3 lateralVelocity;

        public void Initialize(
            FloatingCountStackLine[] lines,
            TMP_FontAsset font,
            float fontSize,
            float lineSpacing,
            float duration,
            float riseSpeed,
            float lateralDriftSpeedMax = 0f)
        {
            if (lines == null || lines.Length == 0)
            {
                Destroy(gameObject);
                return;
            }

            EnsureText();

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
            lockedY = Mathf.Max(transform.position.y, MinPopupWorldY);
            Vector3 initPos = transform.position;
            initPos.y = lockedY;
            transform.position = initPos;

            baseColor = Color.white;
            baseColor.a = 0f;

            tmpText.text = BuildRichText(lines);
            if (font != null)
                tmpText.font = font;
            tmpText.fontSize = Mathf.Max(1f, fontSize);
            tmpText.lineSpacing = lineSpacing;
            tmpText.alignment = TextAlignmentOptions.Center;
            tmpText.enableWordWrapping = false;
            tmpText.richText = true;
            tmpText.color = baseColor;
            ApplyReadableTextMaterial(tmpText);
            tmpText.ForceMeshUpdate();
        }

        void EnsureText()
        {
            if (tmpText != null)
                return;

            Transform textT = transform.Find("Text");
            GameObject textGo = textT != null ? textT.gameObject : new GameObject("Text");
            if (textT == null)
                textGo.transform.SetParent(transform, false);

            var text3d = textGo.GetComponent<TextMeshPro>();
            if (text3d == null)
                text3d = textGo.AddComponent<TextMeshPro>();
            tmpText = text3d;
        }

        static string BuildRichText(FloatingCountStackLine[] lines)
        {
            var sb = new StringBuilder(lines.Length * 24);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    sb.Append('\n');
                sb.Append("<color=#");
                sb.Append(ColorUtility.ToHtmlStringRGBA(lines[i].Color));
                sb.Append('>');
                sb.Append(lines[i].Text);
                sb.Append("</color>");
            }
            return sb.ToString();
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
            transform.position += GetRiseDirectionOnPlayPlane(cam) * riseSpeed * Time.deltaTime;
            if (lateralVelocity.sqrMagnitude > 0f)
                transform.position += lateralVelocity * Time.deltaTime;

            Vector3 pos = transform.position;
            pos.y = lockedY;
            transform.position = pos;

            if (cam != null)
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);

            float alpha = t <= 0.5f ? t * 2f : 1f - (t - 0.5f) * 2f;
            alpha = Mathf.Clamp01(alpha);

            Color c = baseColor;
            c.a = alpha;
            if (tmpText != null)
                tmpText.color = c;

            if (elapsed >= lifetime)
                Destroy(gameObject);
        }

        void LateUpdate()
        {
            Vector3 pos = transform.position;
            if (pos.y != lockedY)
            {
                pos.y = lockedY;
                transform.position = pos;
            }
        }
    }
}

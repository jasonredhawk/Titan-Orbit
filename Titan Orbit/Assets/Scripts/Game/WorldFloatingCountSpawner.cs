using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>Spawns short-lived world-space +/- popups (people transfers, etc.).</summary>
    public static class WorldFloatingCountSpawner
    {
        const float DefaultDuration = 1.7f;
        const float DefaultRiseSpeed = 2.5f;
        const float DefaultFontSize = 10f;
        const float VerticalOffset = 3.5f;
        const int TextSortingOrder = 5001;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        public static void SpawnPeopleDelta(Vector3 worldPosition, int signedAmount, Color color)
        {
            if (signedAmount == 0)
                return;

            var font = ResolveFont();
            if (font == null)
                return;

            var go = new GameObject("PeopleTransferPopup");
            go.transform.position = worldPosition + Vector3.up * VerticalOffset;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSize = DefaultFontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.richText = false;
            tmp.color = color;
            char sign = signedAmount > 0 ? '+' : '-';
            tmp.text = $"{sign}{Mathf.Abs(signedAmount)} People";
            ApplyReadableTextMaterial(tmp);
            tmp.ForceMeshUpdate();

            var popup = go.AddComponent<WorldFloatingCountPopup>();
            popup.Initialize(color, DefaultDuration, DefaultRiseSpeed);
        }

        static TMP_FontAsset ResolveFont()
        {
            if (TMP_Settings.defaultFontAsset != null)
                return TMP_Settings.defaultFontAsset;

            var fallback = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF - Fallback");
            if (fallback != null)
                return fallback;

#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset");
#else
            return null;
#endif
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
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
        }
    }

    /// <summary>Lightweight rise/fade behaviour for world-space TMP popups.</summary>
    sealed class WorldFloatingCountPopup : MonoBehaviour
    {
        Color _baseColor = Color.white;
        float _elapsed;
        float _lifetime = 1.7f;
        float _riseSpeed = 2.5f;
        float _lockedY;
        Vector3 _lateralVelocity;
        TMP_Text _text;

        public void Initialize(Color color, float duration, float riseSpeed)
        {
            _baseColor = color;
            _baseColor.a = 0f;
            _lifetime = Mathf.Max(0.1f, duration);
            _riseSpeed = Mathf.Max(0.15f, riseSpeed);
            _elapsed = 0f;
            _lockedY = Mathf.Max(transform.position.y, 4f);
            var pos = transform.position;
            pos.y = _lockedY;
            transform.position = pos;
            _text = GetComponent<TMP_Text>();

            var cam = Camera.main;
            Vector3 rise = GetRiseDirectionOnPlayPlane(cam);
            Vector3 lateral = Vector3.Cross(Vector3.up, rise);
            if (lateral.sqrMagnitude < 1e-8f)
                lateral = Vector3.right;
            lateral.Normalize();
            _lateralVelocity = lateral * Random.Range(0.2f, 0.55f) * (Random.value < 0.5f ? -1f : 1f);
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
            if (_lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            _elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsed / _lifetime);

            var cam = Camera.main;
            transform.position += GetRiseDirectionOnPlayPlane(cam) * _riseSpeed * Time.deltaTime;
            if (_lateralVelocity.sqrMagnitude > 0f)
                transform.position += _lateralVelocity * Time.deltaTime;

            Vector3 pos = transform.position;
            pos.y = _lockedY;
            transform.position = pos;

            if (cam != null)
                transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);

            float alpha = t <= 0.5f ? t * 2f : 1f - (t - 0.5f) * 2f;
            alpha = Mathf.Clamp01(alpha);
            Color c = _baseColor;
            c.a = alpha;
            if (_text != null)
                _text.color = c;

            if (_elapsed >= _lifetime)
                Destroy(gameObject);
        }
    }
}

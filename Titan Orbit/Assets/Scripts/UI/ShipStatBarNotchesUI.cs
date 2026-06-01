using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Vertical tick marks on a ship stat bar track to show logical capacity steps.
    /// </summary>
    public class ShipStatBarNotchesUI : MonoBehaviour
    {
        private static Sprite s_whiteSprite;

        private RectTransform _track;
        private readonly List<Image> _notches = new List<Image>();

        public void BindTrack(RectTransform track)
        {
            _track = track;
        }

        /// <param name="segmentCount">How many equal segments the bar is split into (notches = segmentCount - 1).</param>
        public void SetSegmentCount(int segmentCount)
        {
            if (_track == null) return;

            int notchCount = Mathf.Max(0, segmentCount - 1);
            EnsureNotchCount(notchCount);

            for (int i = 0; i < notchCount; i++)
            {
                float t = (i + 1) / (float)segmentCount;
                PlaceNotch(_notches[i], t);
            }
        }

        private void EnsureNotchCount(int count)
        {
            while (_notches.Count < count)
            {
                var go = new GameObject("Notch", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(transform, false);
                Image img = go.GetComponent<Image>();
                img.sprite = GetWhiteSprite();
                img.type = Image.Type.Simple;
                img.color = new Color(0f, 0f, 0f, 0.42f);
                img.raycastTarget = false;
                RectTransform rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(1f, 0f);
                _notches.Add(img);
            }

            for (int i = 0; i < _notches.Count; i++)
                _notches[i].gameObject.SetActive(i < count);
        }

        private void PlaceNotch(Image notch, float normalizedX)
        {
            if (notch == null) return;
            RectTransform rt = notch.rectTransform;
            rt.anchorMin = new Vector2(normalizedX, 0f);
            rt.anchorMax = new Vector2(normalizedX, 1f);
            rt.anchoredPosition = Vector2.zero;
        }

        private static Sprite GetWhiteSprite()
        {
            if (s_whiteSprite != null) return s_whiteSprite;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            s_whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            return s_whiteSprite;
        }
    }
}

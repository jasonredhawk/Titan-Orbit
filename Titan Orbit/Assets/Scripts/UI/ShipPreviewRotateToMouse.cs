using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Rotates a preview root (e.g. ship model) around Y so it points toward the mouse.
    /// Uses the center of the given RectTransform (e.g. RawImage) in screen space as the reference point.
    /// </summary>
    public class ShipPreviewRotateToMouse : MonoBehaviour
    {
        [SerializeField] private RectTransform previewRectTransform;
        private Canvas parentCanvas;

        public void SetPreviewRect(RectTransform rect)
        {
            previewRectTransform = rect;
            parentCanvas = rect != null ? rect.GetComponentInParent<Canvas>() : null;
        }

        private void Update()
        {
            if (previewRectTransform == null) return;

            Vector2 centerScreen;
            if (parentCanvas != null && parentCanvas.renderMode != RenderMode.ScreenSpaceOverlay && parentCanvas.worldCamera != null)
                centerScreen = RectTransformUtility.WorldToScreenPoint(parentCanvas.worldCamera, previewRectTransform.TransformPoint(previewRectTransform.rect.center));
            else
                centerScreen = previewRectTransform.TransformPoint(previewRectTransform.rect.center);

            float dx = UnityEngine.Input.mousePosition.x - centerScreen.x;
            float dy = UnityEngine.Input.mousePosition.y - centerScreen.y;
            float angleRad = Mathf.Atan2(dx, dy);
            float angleDeg = Mathf.Rad2Deg * angleRad;
            transform.rotation = Quaternion.Euler(0f, angleDeg, 0f);
        }
    }
}

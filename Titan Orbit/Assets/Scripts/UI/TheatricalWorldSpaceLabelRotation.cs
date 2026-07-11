using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Rotates world-space planet/moon stat panels toward the camera during theatrical orbit,
    /// lifts them clear of the body, and restores the flat top-down pose during normal gameplay.
    /// </summary>
    internal static class TheatricalWorldSpaceLabelRotation
    {
        private const float TheatricalSurfacePaddingWorld = 0.45f;
        private static readonly Quaternion GameplayTopDownRotation = Quaternion.Euler(90f, 0f, 0f);

        public static bool IsTheatricalEngaged() => false;

        public static void ApplyPanelPlacement(
            RectTransform panel,
            Transform body,
            float bodyRadiusWorld,
            float gameplayLocalY,
            float gameplaySurfacePaddingWorld)
        {
            // --- Gameplay top-down vs theatrical camera-facing ---
            if (panel == null || body == null) return;

            if (!IsTheatricalEngaged())
            {
                panel.localPosition = new Vector3(0f, gameplayLocalY, 0f);
                return;
            }

            var cam = UnityEngine.Camera.main;
            if (cam == null)
            {
                panel.localPosition = new Vector3(0f, gameplayLocalY, 0f);
                return;
            }

            GetPanelExtentsWorld(panel, out float halfWidthWorld, out float halfHeightWorld);

            Vector3 bodyCenter = body.position;
            Vector3 toCam = cam.transform.position - bodyCenter;
            if (toCam.sqrMagnitude < 1e-6f)
                toCam = Vector3.up;
            else
                toCam.Normalize();

            Vector3 camRight = cam.transform.right;
            Vector3 camUp = cam.transform.up;
            float backReach = halfHeightWorld * Mathf.Abs(Vector3.Dot(camUp, toCam))
                + halfWidthWorld * Mathf.Abs(Vector3.Dot(camRight, toCam));

            float padding = Mathf.Max(gameplaySurfacePaddingWorld, TheatricalSurfacePaddingWorld);
            float standoff = bodyRadiusWorld + backReach + padding;
            float minStandoff = bodyRadiusWorld + halfHeightWorld + halfWidthWorld * 0.35f + padding;
            standoff = Mathf.Max(standoff, minStandoff);

            panel.position = bodyCenter + toCam * standoff;
        }

        public static void ApplyPanelRotation(Transform panel, bool gameplayUsesLocalRotation)
        {
            if (panel == null) return;

            if (IsTheatricalEngaged())
            {
                var cam = UnityEngine.Camera.main;
                if (cam != null)
                    panel.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
                return;
            }

            if (gameplayUsesLocalRotation)
                panel.localRotation = GameplayTopDownRotation;
            else
                panel.rotation = GameplayTopDownRotation;
        }

        private static void GetPanelExtentsWorld(RectTransform panel, out float halfWidthWorld, out float halfHeightWorld)
        {
            Vector2 size = panel.rect.size;
            if (size.sqrMagnitude < 1e-4f)
                size = panel.sizeDelta;

            Vector3 lossyScale = panel.lossyScale;
            halfWidthWorld = Mathf.Abs(size.x * lossyScale.x) * 0.5f;
            halfHeightWorld = Mathf.Abs(size.y * lossyScale.y) * 0.5f;
        }
    }
}

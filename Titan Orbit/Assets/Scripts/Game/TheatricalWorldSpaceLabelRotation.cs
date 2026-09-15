using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Places world-space planet/moon labels during theatrical orbit and restores the
    /// flat top-down TMP pose when gameplay follow returns. Paired with
    /// <see cref="Game.CameraFollowEcs.IsTheatricalEngaged"/>. Client presentation only.
    /// </summary>
    internal static class TheatricalWorldSpaceLabelRotation
    {
        /// <summary>Extra world gap so tilted labels clear the sphere surface.</summary>
        private const float TheatricalSurfacePaddingWorld = 0.45f;

        /// <summary>
        /// Gameplay label local rotation. Matches planet/moon TMP roots (pitch −90,
        /// Y scale flipped so glyphs read from a top-down camera).
        /// </summary>
        public static readonly Quaternion GameplayTopDownLocalRotation = Quaternion.Euler(-90f, 0f, 0f);

        /// <summary>
        /// True while the local gameplay camera is in idle theatrical orbit (or blending in).
        /// </summary>
        public static bool IsTheatricalEngaged()
        {
            var follow = Game.CameraFollowEcs.Instance;
            return follow != null && follow.IsTheatricalEngaged;
        }

        /// <summary>
        /// Snaps a planet/moon TMP root back to gameplay: local Y, −90 pitch, flipped-Y scale.
        /// Call this on theatrical exit and from ApplyLayout so a dirty refresh cannot leave
        /// a leftover world rotation.
        /// </summary>
        /// <param name="label">PlanetStatsLabel / GemsLabel root.</param>
        /// <param name="gameplayLocalY">Snug local Y from WorldBodyLabelLayout.</param>
        /// <param name="worldScale">Positive readable scale; Y is flipped for TMP.</param>
        public static void RestoreGameplayPose(Transform label, float gameplayLocalY, float worldScale)
        {
            if (label == null)
                return;

            float s = Mathf.Max(0.01f, worldScale);
            label.localScale = new Vector3(s, -s, s);
            label.localRotation = GameplayTopDownLocalRotation;
            label.localPosition = new Vector3(0f, gameplayLocalY, 0f);
        }

        /// <summary>
        /// Billboard a world label toward the camera and lift it off the body.
        /// </summary>
        public static void ApplyTheatricalBillboard(
            Transform label,
            Transform body,
            float bodyRadiusWorld,
            float worldScale,
            float gameplaySurfacePaddingWorld)
        {
            if (label == null || body == null)
                return;

            var cam = UnityEngine.Camera.main;
            if (cam == null)
                return;

            float s = Mathf.Abs(worldScale);
            if (s < 0.01f)
                s = 0.55f;
            // Keep gameplay (s, −s, s). BillboardRotationFacingCamera uses −camera.up so
            // this Y flip stays a winding fix, not an upside-down / back-face flip.
            label.localScale = new Vector3(s, -s, s);

            GetLabelExtentsWorld(label, out float halfWidthWorld, out float halfHeightWorld);

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

            label.position = bodyCenter + toCam * standoff;

            label.rotation = BillboardRotationFacingCamera();
        }

        /// <summary>
        /// Billboard rotation for gameplay TMP (Euler −90, Y scale flipped).
        /// +Z faces the lens; up is −camera.up so the existing (s, −s, s) scale
        /// keeps glyphs upright instead of mirroring them onto the back face.
        /// </summary>
        public static Quaternion BillboardRotationFacingCamera()
        {
            var cam = UnityEngine.Camera.main;
            if (cam == null)
                return GameplayTopDownLocalRotation;

            Transform t = cam.transform;
            return Quaternion.LookRotation(-t.forward, -t.up);
        }

        /// <summary>
        /// World half-extents for theatrical standoff. Prefers a RectTransform; otherwise
        /// uses lossy scale so we never walk child Renderers every frame.
        /// </summary>
        private static void GetLabelExtentsWorld(Transform label, out float halfWidthWorld, out float halfHeightWorld)
        {
            if (label is RectTransform rect)
            {
                Vector2 size = rect.rect.size;
                if (size.sqrMagnitude < 1e-4f)
                    size = rect.sizeDelta;

                Vector3 lossyScale = rect.lossyScale;
                halfWidthWorld = Mathf.Abs(size.x * lossyScale.x) * 0.5f;
                halfHeightWorld = Mathf.Abs(size.y * lossyScale.y) * 0.5f;
                return;
            }

            Vector3 scale = label.lossyScale;
            halfWidthWorld = Mathf.Max(0.35f, Mathf.Abs(scale.x) * 2.2f);
            halfHeightWorld = Mathf.Max(0.25f, Mathf.Abs(scale.y) * 2.2f);
        }
    }
}

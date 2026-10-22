using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// The 3/4 "theatrical" camera used for gear and hull thumbnail PNGs.
    /// Gear component thumbs and family hull thumbs share this pose: the ship nose
    /// stays on local +Z, the lens sits front-right and above, and the model is
    /// turned 28° so you see the nose and one side.
    /// <para>
    /// [TITAN-ORBIT] Editor PNG generators
    /// (<c>ShipFamilyMenuPreviewGenerator</c>, <c>MegaShipMenuPreviewGenerator</c>)
    /// and the Orbit Menu "Your Ship" live camera both call <see cref="Build"/>
    /// so a purchase preview cannot drift from the baked thumbs. This type is
    /// data-only — it does not create a camera or write a texture.
    /// </para>
    /// </summary>
    public readonly struct TheatricalMenuPreviewFraming
    {
        /// <summary>Lens height above the horizon, in degrees. 0 looks along the deck; 90 looks straight down.</summary>
        public const float ElevationDegrees = 26f;

        /// <summary>Lens yaw around the hull, in degrees. Positive swings the camera toward world +X when the stage faces +Z.</summary>
        public const float AzimuthDegrees = 34f;

        /// <summary>
        /// Extra yaw applied to the hull inside the thumbnail stage, in degrees.
        /// Negative turns the nose toward the lens's left so the shot is a 3/4 view, not a head-on shot.
        /// </summary>
        public const float SubjectYawDegrees = -28f;

        /// <summary>
        /// How many "largest half-size" units to stand back, before padding.
        /// Combined with <see cref="ShipFamilyDefinition.menuPreviewBoundsPadding"/> (default 1.15)
        /// this is the zoom of the baked PNGs.
        /// </summary>
        public const float StandoffExtentScale = 3.6f;

        /// <summary>Default lens field of view when a family or MEGA catalog has not overridden it.</summary>
        public const float DefaultFieldOfView = 35f;

        /// <summary>Default bounds padding when a family or MEGA catalog has not overridden it.</summary>
        public const float DefaultBoundsPadding = 1.15f;

        /// <summary>Directional key-light brightness used by the thumbnail renders.</summary>
        public const float KeyLightIntensity = 1.2f;

        /// <summary>Look-at point in the same space as <paramref name="stageBounds"/> passed to <see cref="Build"/>.</summary>
        public Vector3 LookTarget { get; }

        /// <summary>Camera position in the same space as <see cref="LookTarget"/>.</summary>
        public Vector3 CameraPosition { get; }

        /// <summary>Key light rotation in that same space. Aims from the upper front-right onto the hull.</summary>
        public Quaternion KeyLightRotation { get; }

        /// <summary>Largest half-extent of the bounds used to pick the standoff. World units.</summary>
        public float MaxExtent { get; }

        /// <summary>Perspective field of view in degrees, clamped to the thumbnail range 20–55.</summary>
        public float FieldOfView { get; }

        /// <summary>Near clip plane for a camera placed at <see cref="CameraPosition"/>.</summary>
        public float NearClip { get; }

        /// <summary>Far clip plane for a camera placed at <see cref="CameraPosition"/>.</summary>
        public float FarClip { get; }

        TheatricalMenuPreviewFraming(
            Vector3 lookTarget,
            Vector3 cameraPosition,
            Quaternion keyLightRotation,
            float maxExtent,
            float fieldOfView,
            float nearClip,
            float farClip)
        {
            LookTarget = lookTarget;
            CameraPosition = cameraPosition;
            KeyLightRotation = keyLightRotation;
            MaxExtent = maxExtent;
            FieldOfView = fieldOfView;
            NearClip = nearClip;
            FarClip = farClip;
        }

        /// <summary>
        /// Places the lens for one hull. <paramref name="stageBounds"/> must already be in
        /// thumbnail-stage space: the hull root at the origin, nose yawed by
        /// <see cref="SubjectYawDegrees"/>, axes matching the stage.
        /// The Orbit Menu live view builds that space around the flying ship so the
        /// gameplay hull itself is not rotated.
        /// </summary>
        /// <param name="stageBounds">Renderer bounds in stage space.</param>
        /// <param name="fieldOfView">Family or MEGA catalog field of view. Values outside 20–55 are clamped.</param>
        /// <param name="boundsPadding">Family or MEGA catalog padding. Values below 1 are raised to 1.</param>
        public static TheatricalMenuPreviewFraming Build(Bounds stageBounds, float fieldOfView, float boundsPadding)
        {
            // --- One-shot check against the baked-thumbnail numbers ---
            // [EDITOR] Play Mode and the PNG menu both come through here. The check
            // runs once so a later edit to the angles cannot silently disagree with
            // the gear thumbs. Player builds skip it.
#if UNITY_EDITOR
            CheckReferenceOnce();
#endif
            return BuildUnchecked(stageBounds, fieldOfView, boundsPadding);
        }

        /// <summary>
        /// Stage rotation for a live ship. The PNG generator yaws the model and leaves
        /// the stage still. Here the model keeps its gameplay yaw and the stage turns
        /// the other way, which puts the lens on the same 3/4 side.
        /// </summary>
        /// <param name="yawOnly">
        /// Hull heading with pitch and bank removed. Its up is the deck
        /// (world up in flight, the moon surface normal while docked).
        /// </param>
        public static Quaternion StageRotationFromYaw(Quaternion yawOnly)
        {
            // Inverse of Euler(0, SubjectYaw, 0). Subject yaw is -28, so this is +28°.
            return yawOnly * Quaternion.Euler(0f, -SubjectYawDegrees, 0f);
        }

        /// <summary>
        /// Moves a stage-space pose onto a ship in the world. The stage origin is the
        /// hull root. Yaw-only stages keep world up, so the key light stays "upright".
        /// </summary>
        /// <param name="shipRoot">World position of the hull root (not the bounds center).</param>
        /// <param name="stageRotation">From <see cref="StageRotationFromYaw"/>.</param>
        public void ToWorld(
            Vector3 shipRoot,
            Quaternion stageRotation,
            out Vector3 cameraWorld,
            out Vector3 lookWorld,
            out Quaternion lightWorld)
        {
            cameraWorld = shipRoot + stageRotation * CameraPosition;
            lookWorld = shipRoot + stageRotation * LookTarget;
            lightWorld = stageRotation * KeyLightRotation;
        }

        /// <summary>
        /// True when <see cref="Build"/> still matches the gear-thumbnail reference pose:
        /// a hull whose extents are (2, 1, 4), padding 1.15, field of view 35.
        /// </summary>
        public static bool ReferenceSampleMatches()
        {
            // Bounds stores size, not extents. (4, 2, 8) is extents (2, 1, 4).
            // Largest extent is 4. Standoff = 4 * 1.15 * 3.6 = 16.56.
            var bounds = new Bounds(Vector3.zero, new Vector3(4f, 2f, 8f));
            TheatricalMenuPreviewFraming framing = BuildUnchecked(bounds, DefaultFieldOfView, DefaultBoundsPadding);
            Vector3 expectedLook = new Vector3(0f, 0.12f, 0.4f);
            Vector3 expectedCam = new Vector3(8.3230436f, 7.379426f, 12.73942f);
            return (framing.LookTarget - expectedLook).sqrMagnitude < 1e-4f
                && (framing.CameraPosition - expectedCam).sqrMagnitude < 1e-4f;
        }

        static bool s_referenceChecked;

        static void CheckReferenceOnce()
        {
            if (s_referenceChecked)
                return;

            // Set the flag first so the sample does not call back into Build.
            s_referenceChecked = true;
            if (!ReferenceSampleMatches())
            {
                Debug.LogError(
                    "[TitanOrbit] Theatrical menu preview framing drifted from the gear thumbnail pose " +
                    "(elevation 26, azimuth 34, subject yaw -28, standoff scale 3.6).");
            }
        }

        /// <summary>
        /// Same math as the old editor-only <c>BuildTheatricalFraming</c>.
        /// Does not run the reference check (the check calls this).
        /// </summary>
        static TheatricalMenuPreviewFraming BuildUnchecked(Bounds stageBounds, float fieldOfView, float boundsPadding)
        {
            // --- Standoff from the largest half-extent ---
            Vector3 center = stageBounds.center;
            float padding = Mathf.Max(1f, boundsPadding > 0f ? boundsPadding : DefaultBoundsPadding);
            float maxExt = Mathf.Max(stageBounds.extents.x, stageBounds.extents.y, stageBounds.extents.z);
            float standoff = maxExt * padding * StandoffExtentScale;

            // Ship nose points +Z. The camera sits front-right and above, looking down at the hull.
            float elevRad = ElevationDegrees * Mathf.Deg2Rad;
            float azRad = AzimuthDegrees * Mathf.Deg2Rad;
            float horiz = standoff * Mathf.Cos(elevRad);
            float height = standoff * Mathf.Sin(elevRad);
            Vector3 cameraOffset = new Vector3(
                horiz * Mathf.Sin(azRad),
                height,
                horiz * Mathf.Cos(azRad));

            // A hair above and in front of the bounds center so the nose is not cropped.
            Vector3 lookTarget = center + new Vector3(0f, stageBounds.extents.y * 0.12f, stageBounds.extents.z * 0.1f);
            Vector3 cameraPosition = lookTarget + cameraOffset;

            // Key light from the upper front-right, a bit higher than the lens.
            Vector3 lightOffset = new Vector3(horiz * 0.62f, height * 1.75f, horiz * 0.82f);
            Vector3 lightPosition = lookTarget + lightOffset;
            Vector3 lightForward = lookTarget - lightPosition;
            Quaternion keyLightRotation = lightForward.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(lightForward.normalized, Vector3.up)
                : Quaternion.Euler(50f, -40f, 0f);

            float fov = Mathf.Clamp(
                fieldOfView > 0f ? fieldOfView : DefaultFieldOfView,
                20f,
                55f);
            float dist = Vector3.Distance(cameraPosition, lookTarget);
            float nearClip = Mathf.Clamp(dist * 0.02f, 0.05f, 20f);
            float farClip = Mathf.Max(200f, dist + maxExt * 6f);

            return new TheatricalMenuPreviewFraming(
                lookTarget,
                cameraPosition,
                keyLightRotation,
                maxExt,
                fov,
                nearClip,
                farClip);
        }
    }
}

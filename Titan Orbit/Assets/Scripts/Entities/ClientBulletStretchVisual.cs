using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Stretches a bullet visual along its forward axis while in flight. The rear of the mesh stays
    /// anchored at the tracer origin so the slug appears to grow out of the muzzle.
    /// </summary>
    public sealed class ClientBulletStretchVisual : MonoBehaviour
    {
        private const float DefaultStartLengthFactor = 0.5f;
        private const float DefaultEndLengthFactor = 2f;

        private Transform visualRoot;
        private Vector3 baseUniformScale = Vector3.one;
        private Vector3 authoredUniformScale = Vector3.one;
        private bool haveAuthoredScale;
        private float rearLocalZ;
        private bool haveRearExtent;
        private float startLengthFactor = DefaultStartLengthFactor;
        private float endLengthFactor = DefaultEndLengthFactor;

        /// <summary>
        /// Shrinks stretch length when the shot is a mini tracer (drone <c>ScaleMultiplier</c> &lt; 1).
        /// Ship / turret shots stay at authored length.
        /// </summary>
        public static void ApplyShotScale(float scaleMultiplier, ref float startFactor, ref float endFactor)
        {
            if (scaleMultiplier <= 0.01f || scaleMultiplier >= 1f)
                return;
            startFactor *= scaleMultiplier;
            endFactor *= scaleMultiplier;
        }

        public static bool TryAttach(
            Transform tracerRoot,
            GameObject visual,
            float startFactor,
            float endFactor)
        {
            // --- Attach stretch driver and set initial slug length ---
            if (tracerRoot == null || visual == null)
                return false;

            startFactor = startFactor > 0f ? startFactor : DefaultStartLengthFactor;
            endFactor = endFactor > 0f ? endFactor : DefaultEndLengthFactor;
            if (Mathf.Approximately(startFactor, endFactor))
                return false;

            var stretch = tracerRoot.gameObject.AddComponent<ClientBulletStretchVisual>();
            stretch.Rebind(visual, startFactor, endFactor);
            return true;
        }

        /// <summary>
        /// Refresh stretch after a pool Rent. Authored XY is captured once — never from a
        /// mid-flight Z (that made each shot longer than the last). Shot size lives on the
        /// tracer root (<c>ApplyImpactVisualScale</c>), not on this child.
        /// </summary>
        public void Rebind(GameObject visual, float startFactor, float endFactor)
        {
            if (visual == null)
                return;
            visualRoot = visual.transform;
            if (!haveAuthoredScale)
            {
                Vector3 current = visualRoot.localScale;
                // Z may already be stretch-dirty from a prior shot; XY is the prefab size.
                float uniform = Mathf.Max(0.01f, (Mathf.Abs(current.x) + Mathf.Abs(current.y)) * 0.5f);
                authoredUniformScale = new Vector3(uniform, uniform, uniform);
                haveAuthoredScale = true;
            }

            baseUniformScale = authoredUniformScale;
            visualRoot.localScale = authoredUniformScale;
            visualRoot.localPosition = Vector3.zero;
            startLengthFactor = startFactor > 0f ? startFactor : DefaultStartLengthFactor;
            endLengthFactor = endFactor > 0f ? endFactor : DefaultEndLengthFactor;
            if (!haveRearExtent)
            {
                CacheRearExtent();
                haveRearExtent = true;
            }

            ApplyLengthFactor(startLengthFactor);
        }

        public void ApplyTravelProgress(float progress)
        {
            if (visualRoot == null) return;
            float lengthFactor = Mathf.Lerp(startLengthFactor, endLengthFactor, Mathf.Clamp01(progress));
            ApplyLengthFactor(lengthFactor);
        }

        /// <summary>
        /// Pulls the stretched slug back onto the tracer origin so the tip does not
        /// sit past an impact spawned at the GO position.
        /// </summary>
        public void Collapse()
        {
            if (visualRoot == null)
                return;
            visualRoot.localScale = authoredUniformScale;
            visualRoot.localPosition = Vector3.zero;
        }

        private void ApplyLengthFactor(float lengthFactor)
        {
            // --- Scale along Z and anchor rear at tracer origin ---
            Vector3 scale = baseUniformScale;
            scale.z *= lengthFactor;
            visualRoot.localScale = scale;

            // Keep the rear of the visual at the tracer origin as length changes.
            float scaledRearZ = rearLocalZ * baseUniformScale.z * lengthFactor;
            visualRoot.localPosition = new Vector3(0f, 0f, -scaledRearZ);
        }

        private void CacheRearExtent()
        {
            rearLocalZ = 0f;
            if (visualRoot == null)
                return;

            bool found = false;
            float minZ = float.PositiveInfinity;
            Renderer[] renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                Bounds localBounds = renderer.localBounds;
                Vector3 center = localBounds.center;
                Vector3 extents = localBounds.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 localCorner = center + new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    Vector3 rootLocal = visualRoot.InverseTransformPoint(renderer.transform.TransformPoint(localCorner));
                    if (rootLocal.z < minZ)
                        minZ = rootLocal.z;
                    found = true;
                }
            }

            rearLocalZ = found ? minZ : -0.5f;
        }
    }
}

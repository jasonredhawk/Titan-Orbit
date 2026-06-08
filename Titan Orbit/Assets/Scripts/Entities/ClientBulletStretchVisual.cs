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
        private float rearLocalZ;
        private float startLengthFactor = DefaultStartLengthFactor;
        private float endLengthFactor = DefaultEndLengthFactor;

        public static bool TryAttach(
            Transform tracerRoot,
            GameObject visual,
            float startFactor,
            float endFactor)
        {
            if (tracerRoot == null || visual == null)
                return false;

            startFactor = startFactor > 0f ? startFactor : DefaultStartLengthFactor;
            endFactor = endFactor > 0f ? endFactor : DefaultEndLengthFactor;
            if (Mathf.Approximately(startFactor, endFactor))
                return false;

            var stretch = tracerRoot.gameObject.AddComponent<ClientBulletStretchVisual>();
            stretch.visualRoot = visual.transform;
            stretch.baseUniformScale = visual.transform.localScale;
            stretch.startLengthFactor = startFactor;
            stretch.endLengthFactor = endFactor;
            stretch.CacheRearExtent();
            stretch.ApplyLengthFactor(startFactor);
            return true;
        }

        public void ApplyTravelProgress(float progress)
        {
            if (visualRoot == null) return;
            float lengthFactor = Mathf.Lerp(startLengthFactor, endLengthFactor, Mathf.Clamp01(progress));
            ApplyLengthFactor(lengthFactor);
        }

        private void ApplyLengthFactor(float lengthFactor)
        {
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

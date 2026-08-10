using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Shared helpers for the unit-scale planet proxy hierarchy.
    /// <para>
    /// Planet GameObject roots stay at <c>localScale = 1</c> (pose only). ECS
    /// <c>LocalTransform.Scale</c> is applied to a child named <see cref="BodyName"/>
    /// (<c>PlanetVisualBody</c>) so labels, gem moons, and defense pads can use true
    /// world sizes without ÷ planetScale compensation.
    /// </para>
    /// </summary>
    public static class PlanetVisualBody
    {
        /// <summary>Child that carries planet mesh scale (spin pivot + rings live under it).</summary>
        public const string BodyName = "PlanetVisualBody";

        /// <summary>
        /// Finds or creates <see cref="BodyName"/> under <paramref name="planetRoot"/>,
        /// keeps the root at unit scale, and sets the body to <paramref name="planetSize"/>.
        /// </summary>
        /// <param name="planetRoot">Hybrid planet proxy root (pose only).</param>
        /// <param name="planetSize">ECS <c>LocalTransform.Scale</c> (world diameter).</param>
        /// <returns>The visual-body transform, or null when root is missing.</returns>
        public static Transform EnsureAndApplyScale(GameObject planetRoot, float planetSize)
        {
            if (planetRoot == null)
                return null;

            planetSize = Mathf.Max(0.25f, planetSize);

            // --- Root stays unit — pose only ---
            Transform root = planetRoot.transform;
            if ((root.localScale - Vector3.one).sqrMagnitude > 0.0001f)
                root.localScale = Vector3.one;

            Transform body = EnsureBody(root);
            Vector3 want = Vector3.one * planetSize;
            if ((body.localScale - want).sqrMagnitude > 0.0001f)
                body.localScale = want;

            return body;
        }

        /// <summary>
        /// Syncs body scale without recreating hierarchy. No-op when body is missing
        /// (caller should run <see cref="EnsureAndApplyScale"/> at create time).
        /// </summary>
        public static void ApplyScale(GameObject planetRoot, float planetSize)
        {
            if (planetRoot == null)
                return;

            planetSize = Mathf.Max(0.25f, planetSize);
            Transform root = planetRoot.transform;
            if ((root.localScale - Vector3.one).sqrMagnitude > 0.0001f)
                root.localScale = Vector3.one;

            Transform body = root.Find(BodyName);
            if (body == null)
            {
                EnsureAndApplyScale(planetRoot, planetSize);
                return;
            }

            Vector3 want = Vector3.one * planetSize;
            if ((body.localScale - want).sqrMagnitude > 0.0001f)
                body.localScale = want;
        }

        /// <summary>Returns the visual-body child when present.</summary>
        public static bool TryGet(Transform planetRoot, out Transform body)
        {
            body = null;
            if (planetRoot == null)
                return false;
            body = planetRoot.Find(BodyName);
            return body != null;
        }

        /// <summary>
        /// Planet size for presentation: body localScale when present, else root lossyScale
        /// (legacy scaled-root proxies), floored at 0.25.
        /// Prefer ECS <c>LocalTransform.Scale</c> when available.
        /// </summary>
        public static float ResolvePresentationSize(Transform planetRoot)
        {
            if (planetRoot == null)
                return 1f;

            if (TryGet(planetRoot, out Transform body))
                return Mathf.Max(0.25f, body.localScale.x);

            return Mathf.Max(0.25f, planetRoot.lossyScale.x);
        }

        /// <summary>Creates <see cref="BodyName"/> as a direct child of the planet root.</summary>
        static Transform EnsureBody(Transform planetRoot)
        {
            Transform body = planetRoot.Find(BodyName);
            if (body != null)
                return body;

            var go = new GameObject(BodyName);
            body = go.transform;
            body.SetParent(planetRoot, false);
            body.localPosition = Vector3.zero;
            body.localRotation = Quaternion.identity;
            body.localScale = Vector3.one;
            return body;
        }
    }
}

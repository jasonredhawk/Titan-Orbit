using System.Collections.Generic;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Static registry mapping NetCode ship network ids to hull proxy <see cref="Transform"/> roots.
    /// <see cref="BulletMuzzlePresentation"/> reads this for live weapon component muzzle poses
    /// (including BankPivot). Client presentation only — not authoritative sim state.
    /// <para>
    /// Also caches floating-text Y clearance once per hull spawn / chassis swap. Popups read that
    /// snapshot; they do not walk mesh bounds every frame.
    /// </para>
    /// </summary>
    public static class ShipWeaponProxyRegistry
    {
        struct HullClearance
        {
            public Transform Hull;
            public float LiftFromPivot;
            public float XzRadius;
            public bool Measured;
            public bool DeferredRetryDone;
        }

        static readonly Dictionary<int, Transform> s_HullByNetworkId = new Dictionary<int, Transform>();
        static readonly Dictionary<int, HullClearance> s_ClearanceByNetworkId = new Dictionary<int, HullClearance>();
        static readonly Dictionary<Transform, int> s_NetworkIdByHull = new Dictionary<Transform, int>();

        /// <summary>Records the visual hull root for a spawned ship ghost. Clearance is snapshotted separately after scale.</summary>
        public static void Register(int networkId, Transform hullRoot)
        {
            // --- Register ---
            if (networkId <= 0 || hullRoot == null)
                return;

            if (s_HullByNetworkId.TryGetValue(networkId, out Transform previous) &&
                previous != null &&
                previous != hullRoot)
            {
                s_NetworkIdByHull.Remove(previous);
            }

            if (s_NetworkIdByHull.TryGetValue(hullRoot, out int oldId) && oldId != networkId)
                s_ClearanceByNetworkId.Remove(oldId);

            s_HullByNetworkId[networkId] = hullRoot;
            s_NetworkIdByHull[hullRoot] = networkId;
            s_ClearanceByNetworkId[networkId] = new HullClearance { Hull = hullRoot };
        }

        /// <summary>
        /// Walks hull meshes once and freezes float-text Y / XZ clearance. Call after the
        /// new proxy has its presentation scale (spawn / chassis swap), not every frame.
        /// </summary>
        public static void SnapshotClearance(int networkId)
        {
            if (networkId <= 0 ||
                !s_ClearanceByNetworkId.TryGetValue(networkId, out HullClearance clearance) ||
                clearance.Hull == null)
                return;

            MeasureClearance(ref clearance);
            clearance.Measured = true;
            clearance.DeferredRetryDone = true;
            s_ClearanceByNetworkId[networkId] = clearance;
        }

        /// <summary>Removes the mapping when the proxy is destroyed (guards against stale transforms).</summary>
        public static void Unregister(int networkId, Transform hullRoot)
        {
            // --- Unregister ---
            if (networkId <= 0)
                return;
            if (s_HullByNetworkId.TryGetValue(networkId, out var existing) && existing == hullRoot)
            {
                s_HullByNetworkId.Remove(networkId);
                s_ClearanceByNetworkId.Remove(networkId);
            }

            if (hullRoot != null)
                s_NetworkIdByHull.Remove(hullRoot);
        }

        /// <summary>Returns the registered hull root for a ship network id, or false when unknown.</summary>
        public static bool TryGetHull(int networkId, out Transform hullRoot)
        {
            // --- Attempt resolution ---
            hullRoot = null;
            if (networkId <= 0)
                return false;
            return s_HullByNetworkId.TryGetValue(networkId, out hullRoot) && hullRoot != null;
        }

        /// <summary>
        /// Cached mesh-top lift (world Y above the hull pivot) and XZ radius, measured at
        /// <see cref="Register"/> (one deferred retry if meshes were not ready yet).
        /// </summary>
        public static bool TryGetCachedHullClearance(
            int networkId,
            out float liftFromPivot,
            out float xzRadius)
        {
            liftFromPivot = 0f;
            xzRadius = 0f;
            if (networkId <= 0 ||
                !s_ClearanceByNetworkId.TryGetValue(networkId, out HullClearance clearance) ||
                clearance.Hull == null)
                return false;

            EnsureMeasured(networkId, ref clearance);
            liftFromPivot = clearance.LiftFromPivot;
            xzRadius = clearance.XzRadius;
            return xzRadius > 0.001f || liftFromPivot > 0.001f;
        }

        /// <summary>Same cache lookup from a hull proxy transform.</summary>
        public static bool TryGetCachedHullClearance(
            Transform hullRoot,
            out float liftFromPivot,
            out float xzRadius)
        {
            liftFromPivot = 0f;
            xzRadius = 0f;
            if (hullRoot == null || !s_NetworkIdByHull.TryGetValue(hullRoot, out int networkId))
                return false;
            return TryGetCachedHullClearance(networkId, out liftFromPivot, out xzRadius);
        }

        static void EnsureMeasured(int networkId, ref HullClearance clearance)
        {
            if (clearance.Measured)
                return;

            if (!clearance.DeferredRetryDone)
            {
                clearance.DeferredRetryDone = true;
                MeasureClearance(ref clearance);
            }

            clearance.Measured = true;
            s_ClearanceByNetworkId[networkId] = clearance;
        }

        static void MeasureClearance(ref HullClearance clearance)
        {
            if (clearance.Hull == null)
                return;

            float fallback = ResolveFallbackRadius(clearance.Hull);
            if (WorldBodyLabelLayout.TryMeasureBodyClearance(
                    clearance.Hull, out float lift, out float xz) &&
                (lift > 0.001f || xz > 0.001f))
            {
                clearance.LiftFromPivot = Mathf.Max(fallback, lift);
                clearance.XzRadius = Mathf.Max(fallback, xz);
                clearance.Measured = true;
                return;
            }

            clearance.LiftFromPivot = Mathf.Max(clearance.LiftFromPivot, fallback);
            clearance.XzRadius = Mathf.Max(clearance.XzRadius, fallback);
        }

        static float ResolveFallbackRadius(Transform hull)
        {
            if (hull == null)
                return BodyCollisionMath.MinShipHullRadiusWorld;

            float presentationScale = Mathf.Max(0.0001f, hull.lossyScale.x);
            float ecsScale = presentationScale / BodyCollisionMath.ShipPresentationScale;
            return BodyCollisionMath.GetShipHullRadiusWorld(ecsScale);
        }
    }
}

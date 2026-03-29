using UnityEngine;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Toroidal display: the player/ship never wraps (can be at 100, 310 or 100, 15000). All other
    /// entities are repositioned each frame to the toroidal copy closest to the local camera, so
    /// the world appears seamless and bullets/gems don't disappear from warping. Static entities
    /// (planets, asteroids): move root to display copy. Moving entities (gems, bullets): position
    /// a "Visual" child at the display copy; root stays for physics/network.
    /// </summary>
    [DefaultExecutionOrder(32000)] // Run after NetworkTransform/sync and other LateUpdates
    public class ToroidalRenderer : MonoBehaviour
    {
        private const string VISUAL_CHILD_NAME = "Visual";

        private Vector3 logicalPosition;
        private bool logicalPositionStored;
        private Rigidbody rb;
        private Transform visualChild; // For Rigidbody entities: we position this, not the root
        private static UnityEngine.Camera s_cachedMainCamera;
        private static int s_cachedCameraFrame = -1;
        /// <summary>Cached so we don't call GetComponent&lt;Starship&gt;() every LateUpdate on 300+ asteroids.</summary>
        private bool _isShip;
        private Starship _starship;

        private void Start()
        {
            rb = GetComponent<Rigidbody>();
            _starship = GetComponent<Starship>();
            _isShip = _starship != null;
            // Local player: no wrap on BankPivot. Non-local ships: LateUpdate positions BankPivot toroidally.
            if (_isShip)
                return;
            // Only use Visual child for non-kinematic Rigidbodies (gems, bullets). Kinematic (e.g. asteroids)
            // use procedural/SGT mesh on root and must have root moved like planets.
            if (rb != null && !rb.isKinematic)
            {
                Transform v = transform.Find(VISUAL_CHILD_NAME);
                if (v != null)
                    visualChild = v;
                else
                    EnsureVisualChild();
            }
            StoreLogicalPosition();
        }

        private void OnEnable()
        {
            if (rb == null)
                rb = GetComponent<Rigidbody>();
            if (!_isShip && GetComponent<Starship>() != null)
            {
                _isShip = true;
                _starship = GetComponent<Starship>();
            }
            if (_isShip)
                return;
            if (rb != null && !rb.isKinematic && visualChild == null)
            {
                Transform v = transform.Find(VISUAL_CHILD_NAME);
                if (v != null) visualChild = v;
                else EnsureVisualChild();
            }
            if (!logicalPositionStored)
                StoreLogicalPosition();
        }

        /// <summary>
        /// For Rigidbody entities without a "Visual" child: create one, copy root mesh/renderer to it,
        /// and reparent any existing children (e.g. Bullet's Glow) under it so we can position
        /// the visual independently of the synced/physics root.
        /// </summary>
        private void EnsureVisualChild()
        {
            if (transform.Find(VISUAL_CHILD_NAME) != null) return;

            // Collect current children to reparent (before we add the new Visual child)
            int n = transform.childCount;
            var toReparent = new System.Collections.Generic.List<Transform>(n);
            for (int i = 0; i < n; i++)
                toReparent.Add(transform.GetChild(i));

            GameObject visualGo = new GameObject(VISUAL_CHILD_NAME);
            visualGo.transform.SetParent(transform, false);
            visualGo.transform.localPosition = Vector3.zero;
            visualGo.transform.localRotation = Quaternion.identity;
            visualGo.transform.localScale = Vector3.one;

            MeshFilter mf = GetComponent<MeshFilter>();
            Renderer r = GetComponent<Renderer>();
            if (mf != null && mf.sharedMesh != null)
            {
                var mfChild = visualGo.AddComponent<MeshFilter>();
                mfChild.sharedMesh = mf.sharedMesh;
                // MeshFilter has no enabled property; root mesh is hidden by disabling Renderer below
            }
            if (r != null)
            {
                var rChild = visualGo.AddComponent<MeshRenderer>();
                rChild.sharedMaterials = r.sharedMaterials;
                rChild.shadowCastingMode = r.shadowCastingMode;
                rChild.receiveShadows = r.receiveShadows;
                r.enabled = false;
            }

            foreach (Transform c in toReparent)
                c.SetParent(visualGo.transform, true);

            visualChild = visualGo.transform;
        }

        private void StoreLogicalPosition()
        {
            logicalPosition = rb != null ? rb.position : transform.position;
            logicalPositionStored = true;
        }

        private void LateUpdate()
        {
            if (Time.frameCount != s_cachedCameraFrame)
            {
                s_cachedCameraFrame = Time.frameCount;
                s_cachedMainCamera = UnityEngine.Camera.main;
            }
            UnityEngine.Camera cam = s_cachedMainCamera;
            if (cam == null) return;

            if (_isShip && _starship != null)
            {
                if (_starship.IsLocalPlayerShip() || _starship.GemMoonDocked)
                    return;
                Transform bankPivot = _starship.BankPivotTransform;
                if (bankPivot == null || bankPivot == transform)
                    return;
                Vector3 logical = rb != null ? rb.position : transform.position;
                Vector3 display = ToroidalMap.GetDisplayPosition(logical, cam.transform.position);
                bankPivot.position = display;
                return;
            }

            if (_isShip)
                return;

            if (rb != null)
            {
                logicalPosition = ToroidalMap.WrapPosition(rb.position);
                logicalPositionStored = true;
            }
            else if (!logicalPositionStored)
            {
                StoreLogicalPosition();
            }

            Vector3 displayPos = ToroidalMap.GetDisplayPosition(logicalPosition, cam.transform.position);

            // Use visual child only for non-kinematic moving entities (gems, bullets). Kinematic (asteroids)
            // and no-Rigidbody (planets) move root so procedural/SGT visuals follow.
            if (rb != null && !rb.isKinematic && visualChild != null)
            {
                // Reparent any siblings that were added at runtime (e.g. Bullet's spawnedVisual) under our Visual
                for (int i = transform.childCount - 1; i >= 0; i--)
                {
                    Transform c = transform.GetChild(i);
                    if (c != visualChild && c.parent == transform)
                        c.SetParent(visualChild, true);
                }
                // Position only the visual child; leave root for physics/network
                visualChild.position = displayPos;
            }
            else
            {
                // Static or kinematic entity: move root (planets, asteroids with SgtPlanet/procedural mesh)
                transform.position = displayPos;
            }
        }
    }
}

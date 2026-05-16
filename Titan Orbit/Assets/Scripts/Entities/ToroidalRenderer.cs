using UnityEngine;
using TitanOrbit.Camera;
using TitanOrbit.Generation;
using Unity.Netcode;

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
        /// <summary>When Camera.main is missing or wrong, follow the gameplay camera on the CameraController (same as LocalPlayerSetup).</summary>
        private static UnityEngine.Camera s_cachedGameplayCameraFromController;
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
            // Visual child for non-kinematic movers and for networked movers that are kinematic on clients:
            // NetworkRigidbody keeps client RBs kinematic while NetworkTransform drives the root.
            bool isBullet = GetComponent<Bullet>() != null;
            bool isGem = GetComponent<Gem>() != null;
            bool isPeopleTransport = GetComponent<PeopleTransportProjectile>() != null;
            if (rb != null && (!rb.isKinematic || isBullet || isGem || isPeopleTransport))
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
            bool isBullet = GetComponent<Bullet>() != null;
            bool isGem = GetComponent<Gem>() != null;
            bool isPeopleTransport = GetComponent<PeopleTransportProjectile>() != null;
            if (rb != null && (!rb.isKinematic || isBullet || isGem || isPeopleTransport) && visualChild == null)
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
                if ((s_cachedMainCamera == null || !s_cachedMainCamera.isActiveAndEnabled) && s_cachedGameplayCameraFromController == null)
                {
                    var cc = UnityEngine.Object.FindFirstObjectByType<CameraController>();
                    if (cc != null)
                        s_cachedGameplayCameraFromController = cc.GetComponent<UnityEngine.Camera>();
                }
            }
            UnityEngine.Camera cam = s_cachedMainCamera;
            if (cam == null || !cam.isActiveAndEnabled)
                cam = s_cachedGameplayCameraFromController;
            if (cam == null)
            {
                return;
            }

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
                // On non-authority peers, NetworkRigidbody is often kinematic and NetworkTransform drives transform.
                // Use transform position in that case so we don't sample stale rb.position and freeze visuals.
                Vector3 sourcePos = rb.isKinematic ? transform.position : rb.position;
                logicalPosition = ToroidalMap.WrapPosition(sourcePos);
                logicalPositionStored = true;
            }
            else if (!logicalPositionStored)
            {
                StoreLogicalPosition();
            }

            Vector3 displayPos = ToroidalMap.GetDisplayPosition(logicalPosition, cam.transform.position);

            // Bullets may be kinematic on clients (NetworkRigidbody); still offset visuals only, not root.
            if (rb != null && visualChild != null)
            {
                // Reparent any siblings that were added at runtime (e.g. Bullet's spawnedVisual) under our Visual
                for (int i = transform.childCount - 1; i >= 0; i--)
                {
                    Transform c = transform.GetChild(i);
                    if (c != visualChild && c.parent == transform)
                        c.SetParent(visualChild, true);
                }
                var bullet = GetComponent<Bullet>();
                bool isBullet = bullet != null;
                var peopleTransport = GetComponent<PeopleTransportProjectile>();
                Vector3 bulletExtrapolation = Vector3.zero;
                if (isBullet && NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
                    bulletExtrapolation = bullet.GetClientVisualExtrapolationOffset();
                else if (peopleTransport != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
                    bulletExtrapolation = peopleTransport.GetClientVisualExtrapolationOffset();
                displayPos += bulletExtrapolation;
                // Mobile: toroidal display offset can place bullet visuals off-screen if Camera.main differs from the gameplay camera.
                // Keep visuals parented at local origin so they follow the network transform (same as desktop when toroidal is wrong).
                if (isBullet && Application.isMobilePlatform)
                {
                    visualChild.localPosition = Vector3.zero;
                    visualChild.localRotation = Quaternion.identity;
                    // Still apply client extrapolation (visual is tied to root; offset slightly along velocity for remote players).
                    if (bulletExtrapolation.sqrMagnitude > 0.0001f)
                        visualChild.position = transform.position + bulletExtrapolation;
                }
                else
                {
                    // Position only the visual child; leave root for physics/network
                    visualChild.position = displayPos;
                }
            }
            else
            {
                // Static or kinematic entity: move root (planets, asteroids with SgtPlanet/procedural mesh).
                // Server physics + NetworkTransform must stay at logical coordinates; bullets keep logical RB roots
                // while ToroidalRenderer offsets only their Visual child. Moving asteroid roots here on a host
                // with a camera desyncs SphereCasts/traces from bullet paths (dedicated headless skips early — no cam).
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                    return;

                transform.position = displayPos;
            }
        }
    }
}

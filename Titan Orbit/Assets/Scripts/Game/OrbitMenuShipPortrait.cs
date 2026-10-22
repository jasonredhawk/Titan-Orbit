using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Live picture on the Orbit Menu Your Ship card. One camera films the local
    /// player's hull, the same way the customize-ship screen films a preview hull,
    /// and the card shows that texture. The baked thumbnail is not used.
    /// <para>
    /// [HYBRID] Client presentation only. Runs after <see cref="EcsWorldVisualizer"/>
    /// (order 66000) so the lens sees this frame's hull pose. Server sim is untouched.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67120)]
    public sealed class OrbitMenuShipPortrait : MonoBehaviour
    {
        /// <summary>Square texture. The sidebar card is about 286 by 220 pixels.</summary>
        const int TextureSize = 384;

        /// <summary>Degrees per second the lens walks around the hull when auto-rotate is on.</summary>
        const float AutoOrbitDegreesPerSecond = 10f;

        /// <summary>Other hulls farther than this (toroidal) stay out of the framed shot.</summary>
        const float NearbyShipRadius = 48f;

        /// <summary>Cap so a crowded moon cannot clone every hull in the match.</summary>
        const int MaxNearbyShips = 6;

        /// <summary>
        /// Extra reach past the moon body. A docked ship sits on the surface, so the
        /// center is about one radius away; this still includes that ground.
        /// </summary>
        const float MoonSurfaceExtraReach = 36f;

        /// <summary>The live portrait, or null before the menu has asked for one.</summary>
        public static OrbitMenuShipPortrait Active { get; private set; }

        struct SavedLayer
        {
            public Renderer Renderer;
            public int Layer;
        }

        struct SceneCopy
        {
            public int SourceId;
            public Renderer Copy;
        }

        struct PortraitOptions
        {
            public bool Moon;
            public bool Stars;
            public bool NearbyShips;
            public bool AutoRotate;
        }

        Camera _cam;
        Light _keyLight;
        RenderTexture _rt;
        RawImage _target;
        GameObject _proxy;
        int _previewLayer = 6;
        int _moonLayer = 7;
        int _nearbyLayer = 8;
        int _starLayer = 9;
        bool _live;
        bool _shapesOwned;
        float _autoYaw;
        Camera _shapesPrevious;
        Camera _gameCam;
        int _gameCamMask;
        bool _gameCamMasked;
        GameObject _starQuad;
        Material _starMaterial;
        static readonly int FollowXZId = Shader.PropertyToID("_FollowXZ");
        static readonly int QuadScaleId = Shader.PropertyToID("_QuadScale");
        static Mesh _starMesh;

        readonly List<Renderer> _meshes = new List<Renderer>(32);
        readonly List<Renderer> _sceneScratch = new List<Renderer>(32);
        readonly List<SavedLayer> _saved = new List<SavedLayer>(32);
        readonly List<SceneCopy> _sceneCopies = new List<SceneCopy>(16);
        readonly List<Entity> _shipEntities = new List<Entity>(32);
        readonly HashSet<int> _savedIds = new HashSet<int>();
        readonly HashSet<int> _keep = new HashSet<int>();
        readonly HashSet<int> _sceneKeep = new HashSet<int>();

        /// <summary>
        /// Points the portrait at the sidebar image.
        /// </summary>
        /// <param name="target">Raw image on the Your Ship card.</param>
        public static void Bind(RawImage target)
        {
            // Dedicated Server is the active Editor build subtarget, so UNITY_SERVER
            // is defined while playing in the Editor. Gating on UNITY_SERVER alone
            // compiled this method down to a single return, and the card never
            // received a texture. Strip it only in the player server build.
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (!Application.isPlaying || target == null)
                return;
            Ensure().BindInternal(target);
#endif
        }

        /// <summary>
        /// Drops the image and puts the hull back on the gameplay layer.
        /// </summary>
        /// <param name="target">The image that previously called <see cref="Bind"/>.</param>
        public static void Unbind(RawImage target)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (Active == null)
                return;
            Active.UnbindInternal(target);
#endif
        }

        static OrbitMenuShipPortrait Ensure()
        {
            if (Active != null)
                return Active;

            // DontSave keeps this out of the scene file. HideAndDontSave also
            // sets HideInHierarchy, which is why a Hierarchy camera filter never
            // listed it. It still lives under the DontDestroyOnLoad scene while playing.
            var go = new GameObject("OrbitMenuShipPortrait");
            go.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(go);
            return go.AddComponent<OrbitMenuShipPortrait>();
        }

        void Awake()
        {
            ClaimOrDiscard();
        }

        void OnEnable()
        {
            // A script reload during Play clears the static owner but leaves this
            // object alive. Claim it again, and clear HideInHierarchy so a
            // Hierarchy search for "camera" can find the lens.
            ClaimOrDiscard();
            if (Active != this)
                return;
            Application.onBeforeRender -= FilmBeforeRender;
            Application.onBeforeRender += FilmBeforeRender;
            Canvas.willRenderCanvases -= StretchPicture;
            Canvas.willRenderCanvases += StretchPicture;
        }

        /// <summary>
        /// One portrait rig. Extra copies are removed. HideAndDontSave is never
        /// used: it hides the camera and those objects leak after Play Mode ends.
        /// </summary>
        void ClaimOrDiscard()
        {
            if (Active != null && Active != this)
            {
                gameObject.hideFlags = HideFlags.None;
                Destroy(gameObject);
                return;
            }

            Active = this;
            gameObject.hideFlags = HideFlags.DontSave;
            _previewLayer = PreviewLayer();
            _moonLayer = LayerOr("PortraitMoon", 7);
            _nearbyLayer = LayerOr("PortraitNearbyShip", 8);
            _starLayer = LayerOr("PortraitStars", 9);
            EnsureCamera();
            EnsureLight();
            EnsureTexture();
        }

        void OnDisable()
        {
            Application.onBeforeRender -= FilmBeforeRender;
            Canvas.willRenderCanvases -= StretchPicture;
        }

        void OnDestroy()
        {
            StopFilming();
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
                _rt = null;
            }

            ReleaseSceneCopies();
            if (_starMaterial != null)
            {
                Destroy(_starMaterial);
                _starMaterial = null;
            }

            if (Active == this)
                Active = null;
        }

        void BindInternal(RawImage target)
        {
            // The rig may have survived a domain reload with its texture cleared.
            if (_rt == null)
                EnsureTexture();
            _target = target;
            _target.texture = _rt;
            _target.color = Color.white;
            _target.maskable = false;
            _target.material = null;
            _target.enabled = true;
            StretchPicture();
        }

        void UnbindInternal(RawImage target)
        {
            if (_target != target)
                return;
            _target = null;
            StopFilming();
        }

        void LateUpdate()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (Active == null)
                Active = this;
            if (Active != this)
                return;

            if (_target == null || !_target.gameObject.activeInHierarchy)
            {
                StopFilming();
                return;
            }

            _live = true;
            ShowPicture();
            _proxy = ResolveLocalShipProxy();
            if (_proxy == null)
                return;

            PortraitOptions options = ReadOptions();
            if (options.AutoRotate && !OrbitMenuPortraitTurntable.IsDragging)
                _autoYaw += AutoOrbitDegreesPerSecond * Time.unscaledDeltaTime;
            Film(_proxy.transform);
#endif
        }

        /// <summary>
        /// The visualizer moves the hull again after LateUpdate. Put the meshes on
        /// the preview layer and place the lens after that, right before cameras render.
        /// </summary>
        void FilmBeforeRender()
        {
            if (!_live || _proxy == null)
                return;
            Film(_proxy.transform);
        }

        /// <summary>
        /// Moves the hull onto the preview layer, aims the lens, and turns the camera on.
        /// </summary>
        void Film(Transform hull)
        {
            if (hull == null || _cam == null || _rt == null)
                return;

            AdoptMeshes(hull);
            PortraitOptions options = ReadOptions();
            SyncScene(options, hull);
            ApplyMask(options);
            ShieldGameCamera();
            Aim(hull);
            if (!_rt.IsCreated())
                _rt.Create();

            // [UNITY] Assign the texture before the camera is enabled. A camera with
            // no target blits into the game view. URP fills this texture when the
            // camera renders; Camera.Render() does not.
            _cam.targetTexture = _rt;
            _cam.enabled = true;
            RememberShapesSkip();
            ShowPicture();
        }

        void ShowPicture()
        {
            if (_target == null || _rt == null)
                return;
            if (_target.texture != _rt)
                _target.texture = _rt;
            if (_target.material != null)
                _target.material = null;
            _target.color = Color.white;
            _target.maskable = false;
            _target.enabled = true;
            StretchPicture();
        }

        void StretchPicture()
        {
            if (_target == null)
                return;
            RectTransform rt = _target.rectTransform;
            if (rt.anchorMin != Vector2.zero || rt.anchorMax != Vector2.one
                || rt.offsetMin != Vector2.zero || rt.offsetMax != Vector2.zero
                || rt.localScale != Vector3.one)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
            }

            if (_target.transform.parent != null)
                _target.transform.SetAsLastSibling();
        }

        void StopFilming()
        {
            _live = false;
            _proxy = null;
            RestoreMeshes();
            ReleaseSceneCopies();
            UnshieldGameCamera();
            if (_starQuad != null)
                _starQuad.SetActive(false);
            if (_cam != null)
            {
                _cam.enabled = false;
                _cam.targetTexture = null;
            }

            if (_shapesOwned)
            {
                if (ImmediateModeShapeDrawer.skipDrawCamera == _cam)
                    ImmediateModeShapeDrawer.skipDrawCamera = _shapesPrevious;
                _shapesOwned = false;
                _shapesPrevious = null;
            }

            if (_target != null)
                _target.enabled = false;
        }

        void RememberShapesSkip()
        {
            if (_shapesOwned || _cam == null)
                return;
            _shapesPrevious = ImmediateModeShapeDrawer.skipDrawCamera;
            _shapesOwned = true;
            ImmediateModeShapeDrawer.skipDrawCamera = _cam;
        }

        /// <summary>
        /// The hull the local player is flying.
        /// </summary>
        static GameObject ResolveLocalShipProxy()
        {
            EcsWorldVisualizer viz = EcsWorldVisualizer.Active;
            if (viz != null && viz.LocalPlayerShipProxy != null)
                return viz.LocalPlayerShipProxy;

            Transform root = EcsWorldVisualizer.LocalPlayerShipVisualRoot;
            if (root != null)
                return root.gameObject;

            if (viz == null)
                return null;
            World world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return null;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity ship))
                return null;
            return viz.TryGetProxy(ship, out GameObject proxy) ? proxy : null;
        }

        void AdoptMeshes(Transform hull)
        {
            _meshes.Clear();
            CollectMeshes(hull, _meshes);
            _keep.Clear();
            for (int i = 0; i < _meshes.Count; i++)
            {
                Renderer renderer = _meshes[i];
                if (renderer == null)
                    continue;
                GameObject go = renderer.gameObject;
                int id = go.GetInstanceID();
                _keep.Add(id);
                if (_savedIds.Add(id))
                    _saved.Add(new SavedLayer { Renderer = renderer, Layer = go.layer });

                go.layer = _previewLayer;
            }

            for (int i = _saved.Count - 1; i >= 0; i--)
            {
                Renderer renderer = _saved[i].Renderer;
                if (renderer == null)
                {
                    _saved.RemoveAt(i);
                    continue;
                }

                int id = renderer.gameObject.GetInstanceID();
                if (_keep.Contains(id))
                    continue;
                renderer.gameObject.layer = _saved[i].Layer;
                _savedIds.Remove(id);
                _saved.RemoveAt(i);
            }
        }

        static void CollectMeshes(Transform node, List<Renderer> dst)
        {
            if (node == null || !node.gameObject.activeInHierarchy)
                return;
            // Scene copies are parented under the source mesh. Walking them again
            // would clone the clone.
            if (node.name == "PortraitSceneCopy")
                return;

            Renderer renderer = node.GetComponent<Renderer>();
            if (renderer != null
                && renderer.enabled
                && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                && !WorldBodyLabelLayout.ShouldSkipRenderer(renderer))
            {
                dst.Add(renderer);
            }

            int count = node.childCount;
            for (int i = 0; i < count; i++)
                CollectMeshes(node.GetChild(i), dst);
        }

        void RestoreMeshes()
        {
            for (int i = 0; i < _saved.Count; i++)
            {
                Renderer renderer = _saved[i].Renderer;
                if (renderer != null)
                    renderer.gameObject.layer = _saved[i].Layer;
            }

            _saved.Clear();
            _savedIds.Clear();
        }

        void Aim(Transform hull)
        {
            Quaternion heading = DeckHeading(hull.rotation);
            Quaternion stage = TheatricalMenuPreviewFraming.StageRotationFromYaw(heading);
            Bounds stageBounds = MeasureStageBounds(hull, stage);
            TheatricalMenuPreviewFraming framing = TheatricalMenuPreviewFraming.Build(
                stageBounds,
                TheatricalMenuPreviewFraming.DefaultFieldOfView,
                TheatricalMenuPreviewFraming.DefaultBoundsPadding);
            framing.ToWorld(hull.position, stage, out Vector3 camWorld, out Vector3 lookWorld, out Quaternion lightWorld);

            Vector3 deckUp = heading * Vector3.up;
            float yaw = OrbitMenuPortraitTurntable.DragYawDegrees + _autoYaw;
            Vector3 offset = camWorld - lookWorld;
            if (Mathf.Abs(yaw) > 0.001f)
            {
                Quaternion spin = Quaternion.AngleAxis(yaw, deckUp);
                offset = spin * offset;
                lightWorld = spin * lightWorld;
            }

            _cam.fieldOfView = framing.FieldOfView;
            _cam.nearClipPlane = Mathf.Max(0.05f, framing.NearClip);
            _cam.farClipPlane = Mathf.Max(_cam.nearClipPlane + 1f, framing.FarClip);
            _cam.transform.SetPositionAndRotation(lookWorld + offset, Quaternion.identity);
            _cam.transform.LookAt(lookWorld, deckUp);
            if (_keyLight != null)
                _keyLight.transform.rotation = lightWorld;
        }

        /// <summary>
        /// Hull bounds in thumbnail-stage space. An empty measure uses the reference
        /// hull size so the lens still sits at the theatrical standoff instead of
        /// inside the ship.
        /// </summary>
        Bounds MeasureStageBounds(Transform hull, Quaternion stage)
        {
            Quaternion stageInv = Quaternion.Inverse(stage);
            Vector3 origin = hull.position;
            bool any = false;
            Bounds stageBounds = default;
            for (int i = 0; i < _meshes.Count; i++)
            {
                Renderer renderer = _meshes[i];
                if (renderer == null)
                    continue;
                if (!Encapsulate(renderer, origin, stageInv, ref stageBounds, ref any))
                    continue;
            }

            if (!any)
                stageBounds = new Bounds(Vector3.zero, new Vector3(4f, 2f, 8f));
            return stageBounds;
        }

        static bool Encapsulate(
            Renderer renderer,
            Vector3 origin,
            Quaternion stageInv,
            ref Bounds stageBounds,
            ref bool any)
        {
            Bounds local = renderer.localBounds;
            if (local.size.sqrMagnitude < 1e-8f)
                return false;

            Transform mesh = renderer.transform;
            Vector3 center = local.center;
            Vector3 extents = local.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localCorner = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 world = mesh.TransformPoint(localCorner);
                Vector3 stagePoint = stageInv * (world - origin);
                if (!any)
                {
                    stageBounds = new Bounds(stagePoint, Vector3.zero);
                    any = true;
                }
                else
                {
                    stageBounds.Encapsulate(stagePoint);
                }
            }

            return true;
        }

        /// <summary>
        /// Heading of the hull root around its own deck. Bank lives on a child.
        /// Moon dock sets root up to the surface normal.
        /// </summary>
        static Quaternion DeckHeading(Quaternion worldRotation)
        {
            Vector3 up = worldRotation * Vector3.up;
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.up;
            else
                up.Normalize();

            Vector3 forward = worldRotation * Vector3.forward;
            forward -= up * Vector3.Dot(forward, up);
            if (forward.sqrMagnitude < 1e-6f)
            {
                forward = Vector3.Cross(worldRotation * Vector3.right, up);
                if (forward.sqrMagnitude < 1e-6f)
                    forward = Vector3.Cross(Vector3.right, up);
            }

            if (forward.sqrMagnitude < 1e-6f)
                return Quaternion.LookRotation(Vector3.forward, up);
            return Quaternion.LookRotation(forward.normalized, up);
        }

        void EnsureCamera()
        {
            if (_cam == null)
                _cam = GetComponentInChildren<Camera>(true);

            if (_cam == null)
            {
                // Same setup as ShipCustomizeScreen's preview camera, which does show a hull.
                var camGo = new GameObject("OrbitMenuShipPortraitCamera");
                camGo.hideFlags = HideFlags.DontSave;
                camGo.transform.SetParent(transform, false);
                _cam = camGo.AddComponent<Camera>();
            }

            _cam.gameObject.name = "OrbitMenuShipPortraitCamera";
            _cam.gameObject.hideFlags = HideFlags.DontSave;
            _cam.enabled = false;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.01f, 0.015f, 0.03f, 1f);
            _cam.cullingMask = 1 << _previewLayer;
            _cam.orthographic = false;
            _cam.fieldOfView = TheatricalMenuPreviewFraming.DefaultFieldOfView;
            _cam.nearClipPlane = 0.2f;
            _cam.farClipPlane = 80f;
            _cam.allowHDR = true;
            _cam.useOcclusionCulling = false;
            _cam.depth = -80f;
            _cam.targetTexture = null;

            AudioListener listener = _cam.GetComponent<AudioListener>();
            if (listener != null)
                Destroy(listener);
        }

        void EnsureLight()
        {
            if (_keyLight == null)
            {
                Light[] lights = GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    if (lights[i] != null && lights[i].type == LightType.Directional)
                    {
                        _keyLight = lights[i];
                        break;
                    }
                }
            }

            if (_keyLight == null)
            {
                var lightGo = new GameObject("KeyLight");
                lightGo.hideFlags = HideFlags.DontSave;
                lightGo.transform.SetParent(transform, false);
                _keyLight = lightGo.AddComponent<Light>();
            }

            _keyLight.gameObject.hideFlags = HideFlags.DontSave;
            _keyLight.type = LightType.Directional;
            _keyLight.intensity = TheatricalMenuPreviewFraming.KeyLightIntensity;
            _keyLight.color = Color.white;
            _keyLight.shadows = LightShadows.None;
            _keyLight.cullingMask = 1 << _previewLayer;
        }

        void EnsureTexture()
        {
            if (_rt != null)
                return;

            // Default format, matching ShipCustomizeScreen. An ARGB32 target was
            // coming back with alpha 0, and the card drew that as invisible.
            _rt = new RenderTexture(TextureSize, TextureSize, 16)
            {
                name = "OrbitMenuShipPortrait",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear
            };
            _rt.Create();
        }

        static int PreviewLayer()
        {
            return LayerOr("ShipPreview", 6);
        }

        static int LayerOr(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        /// <summary>
        /// GameManager checkboxes. Ship-only hides moon, stars, and nearby hulls.
        /// Missing manager keeps the hull-only shot.
        /// </summary>
        static PortraitOptions ReadOptions()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null)
                return default;

            bool shipOnly = manager.PortraitShipOnly;
            return new PortraitOptions
            {
                Moon = !shipOnly && manager.PortraitShowMoon,
                Stars = !shipOnly && manager.PortraitShowStars,
                NearbyShips = !shipOnly && manager.PortraitShowNearbyShips,
                AutoRotate = manager.PortraitAutoRotate
            };
        }

        void ApplyMask(PortraitOptions options)
        {
            int mask = 1 << _previewLayer;
            int lightMask = 1 << _previewLayer;
            if (options.Moon)
            {
                mask |= 1 << _moonLayer;
                lightMask |= 1 << _moonLayer;
            }

            if (options.Stars)
                mask |= 1 << _starLayer;
            if (options.NearbyShips)
            {
                mask |= 1 << _nearbyLayer;
                lightMask |= 1 << _nearbyLayer;
            }

            _cam.cullingMask = mask;
            if (_keyLight != null)
                _keyLight.cullingMask = lightMask;
        }

        /// <summary>
        /// The game camera renders every layer. Moon, star, and nearby copies
        /// must stay out of that view or the world draws them twice.
        /// </summary>
        void ShieldGameCamera()
        {
            Camera game = UnityEngine.Camera.main;
            if (game == null || game == _cam)
                return;

            if (!_gameCamMasked || _gameCam != game)
            {
                _gameCam = game;
                _gameCamMask = game.cullingMask;
                _gameCamMasked = true;
            }

            int exclude = (1 << _moonLayer) | (1 << _nearbyLayer) | (1 << _starLayer);
            game.cullingMask = _gameCamMask & ~exclude;
        }

        void UnshieldGameCamera()
        {
            if (!_gameCamMasked)
                return;
            if (_gameCam != null)
                _gameCam.cullingMask = _gameCamMask;
            _gameCam = null;
            _gameCamMasked = false;
        }

        /// <summary>
        /// Copies of the moon surface and nearby hulls on portrait-only layers.
        /// The gameplay renderers stay on their own layers so the world view is unchanged.
        /// </summary>
        void SyncScene(PortraitOptions options, Transform hull)
        {
            _sceneKeep.Clear();
            if (options.Moon)
                KeepMoon(hull.position);
            if (options.NearbyShips)
                KeepNearbyShips(hull);
            if (options.Stars)
                ShowStars(hull.position);
            else if (_starQuad != null)
                _starQuad.SetActive(false);

            for (int i = _sceneCopies.Count - 1; i >= 0; i--)
            {
                Renderer copy = _sceneCopies[i].Copy;
                if (copy != null && _sceneKeep.Contains(_sceneCopies[i].SourceId))
                    continue;
                if (copy != null)
                    Destroy(copy.gameObject);
                _sceneCopies.RemoveAt(i);
            }
        }

        void KeepMoon(Vector3 shipPos)
        {
            if (!PlanetGemMoonVisualRegistry.TryFindClosestMoon(
                    shipPos, TeamId.None, false, out int planetId, out Vector3 moonPos))
                return;
            if (!PlanetGemMoonVisualRegistry.TryGetMoon(planetId, out PlanetGemMoonVisualProxy moon) || moon == null)
                return;

            float reach = moon.MoonBodyRadiusWorld + MoonSurfaceExtraReach;
            if (ToroidalMap.ToroidalDistance(shipPos, moonPos) > reach)
                return;

            Renderer surface = moon.SurfaceRenderer;
            if (surface == null || !surface.enabled || !surface.gameObject.activeInHierarchy)
                return;
            EnsureSceneCopy(surface, _moonLayer);
        }

        void KeepNearbyShips(Transform hull)
        {
            EcsWorldVisualizer viz = EcsWorldVisualizer.Active;
            if (viz == null)
                return;

            viz.CopyShipProxyEntitiesTo(_shipEntities);
            int kept = 0;
            for (int i = 0; i < _shipEntities.Count; i++)
            {
                if (kept >= MaxNearbyShips)
                    break;
                if (!viz.TryGetProxy(_shipEntities[i], out GameObject proxy) || proxy == null)
                    continue;
                if (proxy.transform == hull || proxy == hull.gameObject)
                    continue;
                if (ToroidalMap.ToroidalDistance(hull.position, proxy.transform.position) > NearbyShipRadius)
                    continue;

                _sceneScratch.Clear();
                CollectMeshes(proxy.transform, _sceneScratch);
                for (int m = 0; m < _sceneScratch.Count; m++)
                    EnsureSceneCopy(_sceneScratch[m], _nearbyLayer);
                kept++;
            }
        }

        void EnsureSceneCopy(Renderer source, int layer)
        {
            if (source == null || layer < 0)
                return;

            int id = source.GetInstanceID();
            _sceneKeep.Add(id);
            for (int i = 0; i < _sceneCopies.Count; i++)
            {
                if (_sceneCopies[i].SourceId != id)
                    continue;
                if (_sceneCopies[i].Copy != null)
                    return;
            }

            var go = new GameObject("PortraitSceneCopy");
            go.hideFlags = HideFlags.DontSave;
            go.layer = layer;
            go.transform.SetParent(source.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            Renderer copy = null;
            if (source is SkinnedMeshRenderer skin && skin.sharedMesh != null)
            {
                var skinned = go.AddComponent<SkinnedMeshRenderer>();
                skinned.sharedMesh = skin.sharedMesh;
                skinned.sharedMaterials = skin.sharedMaterials;
                skinned.bones = skin.bones;
                skinned.rootBone = skin.rootBone;
                skinned.updateWhenOffscreen = true;
                copy = skinned;
            }
            else if (source is MeshRenderer)
            {
                MeshFilter srcFilter = source.GetComponent<MeshFilter>();
                if (srcFilter != null && srcFilter.sharedMesh != null)
                {
                    MeshFilter filter = go.AddComponent<MeshFilter>();
                    filter.sharedMesh = srcFilter.sharedMesh;
                    MeshRenderer mesh = go.AddComponent<MeshRenderer>();
                    mesh.sharedMaterials = source.sharedMaterials;
                    copy = mesh;
                }
            }

            if (copy == null)
            {
                Destroy(go);
                return;
            }

            copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            copy.receiveShadows = false;
            copy.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            copy.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _sceneCopies.Add(new SceneCopy { SourceId = id, Copy = copy });
        }

        void ShowStars(Vector3 shipPos)
        {
            EnsureStarQuad();
            if (_starQuad == null || _cam == null)
                return;

            float dist = Mathf.Clamp(_cam.farClipPlane * 0.8f, 20f, 160f);
            float halfHeight = dist * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfWidth = halfHeight * Mathf.Max(0.5f, _cam.aspect);
            Transform quad = _starQuad.transform;
            quad.SetParent(_cam.transform, false);
            quad.localPosition = new Vector3(0f, 0f, dist);
            quad.localRotation = Quaternion.Euler(0f, 180f, 0f);
            quad.localScale = new Vector3(halfWidth * 2.2f, halfHeight * 2.2f, 1f);
            _starQuad.layer = _starLayer;
            if (!_starQuad.activeSelf)
                _starQuad.SetActive(true);

            if (_starMaterial != null)
            {
                _starMaterial.SetVector(FollowXZId, new Vector4(shipPos.x, shipPos.z, 0f, 0f));
                _starMaterial.SetVector(QuadScaleId, new Vector4(quad.localScale.x, quad.localScale.y, 0f, 0f));
            }
        }

        void EnsureStarQuad()
        {
            if (_starQuad != null)
                return;
            if (_starMaterial == null)
            {
                Material template = Resources.Load<Material>("Materials/TitanOrbitParallaxStarfield");
                if (template == null)
                {
                    Shader shader = Shader.Find("TitanOrbit/ParallaxStarfield");
                    if (shader == null)
                        return;
                    _starMaterial = new Material(shader);
                }
                else
                {
                    _starMaterial = new Material(template);
                }

                _starMaterial.SetOverrideTag("RenderType", "Opaque");
                _starMaterial.SetFloat("_Surface", 0f);
                _starMaterial.renderQueue = 1001;
            }

            _starQuad = new GameObject("OrbitMenuPortraitStars");
            _starQuad.hideFlags = HideFlags.DontSave;
            _starQuad.layer = _starLayer;
            _starQuad.transform.SetParent(transform, false);
            MeshFilter filter = _starQuad.AddComponent<MeshFilter>();
            filter.sharedMesh = SharedStarMesh();
            MeshRenderer renderer = _starQuad.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _starMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _starQuad.SetActive(false);
        }

        static Mesh SharedStarMesh()
        {
            if (_starMesh != null)
                return _starMesh;

            _starMesh = new Mesh { name = "PortraitStarQuad" };
            _starMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f)
            };
            _starMesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            };
            _starMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _starMesh.RecalculateNormals();
            return _starMesh;
        }

        void ReleaseSceneCopies()
        {
            for (int i = 0; i < _sceneCopies.Count; i++)
            {
                if (_sceneCopies[i].Copy != null)
                    Destroy(_sceneCopies[i].Copy.gameObject);
            }

            _sceneCopies.Clear();
            _sceneKeep.Clear();
        }
    }
}

using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Reads the gameplay camera and tells the server which ground rectangle this
    /// player can see. Sends <see cref="ViewInterestCommand"/> only when that rectangle moves,
    /// the zoom changes, the full map opens or closes, or a one-second heartbeat is due.
    /// Also drops local gem visuals that have left the view so an off-screen scoop does not
    /// leave a crystal behind. Those gems come back through the server catch-up when the
    /// camera slides over them again.
    /// </summary>
    public sealed class ViewInterestReporter : MonoBehaviour
    {
        /// <summary>Center must move this far (world units) before we send again.</summary>
        const float MoveThreshold = 2f;

        /// <summary>Half-extent must change this far before we send again.</summary>
        const float SizeThreshold = 1f;

        /// <summary>Seconds between sends even when the camera is still (covers a dropped packet).</summary>
        const float HeartbeatSeconds = 1f;

        /// <summary>How often local off-screen gems are removed.</summary>
        const float GemCullInterval = 0.25f;

        Camera _camera;
        float3 _lastCenter;
        float _lastHalfW;
        float _lastHalfH;
        byte _lastFullMap;
        float _lastSendTime = -999f;
        float _lastGemCullTime = -999f;
        bool _hasSent;

        /// <summary>
        /// [UNITY] Spawns the reporter after the scene loads. Dedicated server builds skip it.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (FindAnyObjectByType<ViewInterestReporter>() != null)
                return;

            var go = new GameObject(nameof(ViewInterestReporter));
            DontDestroyOnLoad(go);
            go.AddComponent<ViewInterestReporter>();
#endif
        }

        /// <summary>Clears the last rectangle when this Play Mode session ends.</summary>
        void OnDestroy()
        {
            _hasSent = false;
        }

        /// <summary>
        /// Samples the camera after <see cref="CameraFollowEcs"/> has posed it, and sends when
        /// the visible ground changed.
        /// </summary>
        void LateUpdate()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                _hasSent = false;
                return;
            }

            if (!TryMeasureView(out float3 center, out float halfW, out float halfH))
                return;

            byte fullMap = ViewInterestTuning.ClientWantsAllShipPositions ? (byte)1 : (byte)0;
            bool changed = !_hasSent
                || math.distance(new float2(center.x, center.z), new float2(_lastCenter.x, _lastCenter.z)) > MoveThreshold
                || math.abs(halfW - _lastHalfW) > SizeThreshold
                || math.abs(halfH - _lastHalfH) > SizeThreshold
                || fullMap != _lastFullMap;
            bool heartbeat = Time.unscaledTime - _lastSendTime >= HeartbeatSeconds;
            if (changed || heartbeat)
            {
                if (TrySend(center, halfW, halfH, fullMap))
                {
                    _lastCenter = center;
                    _lastHalfW = halfW;
                    _lastHalfH = halfH;
                    _lastFullMap = fullMap;
                    _lastSendTime = Time.unscaledTime;
                    _hasSent = true;
                }
            }

            if (Time.unscaledTime - _lastGemCullTime >= GemCullInterval)
            {
                _lastGemCullTime = Time.unscaledTime;
                CullOffscreenGems(center, halfW, halfH);
            }
        }

        /// <summary>
        /// Projects the camera onto the y=0 plane and publishes that rectangle on
        /// <see cref="CameraGroundView"/> for the same frame.
        /// </summary>
        bool TryMeasureView(out float3 center, out float halfW, out float halfH)
        {
            if (_camera == null)
            {
                var follow = FindAnyObjectByType<CameraFollowEcs>();
                _camera = follow != null ? follow.GetComponent<Camera>() : Camera.main;
            }

            if (_camera != null)
                CameraGroundView.UseCamera(_camera);

            return CameraGroundView.TryGet(out center, out halfW, out halfH);
        }

        /// <summary>
        /// Local Host writes the command straight onto the server world. Dedicated clients
        /// send it as an RPC. Returns false when no connection is ready yet.
        /// </summary>
        static bool TrySend(float3 center, float halfW, float halfH, byte fullMap)
        {
            var command = new ViewInterestCommand
            {
                Center = center,
                HalfWidth = halfW,
                HalfHeight = halfH,
                FullMap = fullMap,
            };

            if (EcsGameBridge.IsLocalHost())
                return TryInjectHost(command);

            return TrySendDedicated(command);
        }

        /// <summary>Puts the command on the server connection so the host does not wait on a loopback RPC.</summary>
        static bool TryInjectHost(in ViewInterestCommand command)
        {
            int networkId = EcsGameBridge.GetLocalNetworkId();
            if (networkId <= 0)
                return false;

            World server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var em = server.EntityManager;
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            if (query.CalculateEntityCount() != 1)
                return false;

            Entity connection = query.GetSingletonEntity();
            if (em.GetComponentData<NetworkId>(connection).Value != networkId)
                return false;

            Entity rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, command);
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>Dedicated / Relay client: one RPC toward the server connection.</summary>
        static bool TrySendDedicated(in ViewInterestCommand command)
        {
            World world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            Entity entity = em.CreateEntity();
            em.AddComponentData(entity, command);
            em.AddComponentData(entity, new SendRpcCommandRequest());
            return true;
        }

        /// <summary>Removes local crystals outside the padded view. Does not mark them consumed.</summary>
        static void CullOffscreenGems(float3 center, float halfW, float halfH)
        {
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            World world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            ClientLocalGemSpawn.CullOutsideView(
                world.EntityManager,
                center,
                halfW + ViewInterestTuning.GemKeepMargin,
                halfH + ViewInterestTuning.GemKeepMargin,
                mapW,
                mapH);
        }
    }

    /// <summary>
    /// Ground rectangle the gameplay camera covers this frame. Measured once, then shared
    /// by the world visualizer, rock spin, bullets, and defense pads. A missing camera or
    /// an unset map does not cull — everything stays visible.
    /// <para>
    /// A top-down lens hits the ground with all four corner rays, and that quad is the rect.
    /// Theatrical orbit tilts and pulls back until the upper frame looks at the sky, or the
    /// camera drops beside the hull. Those rays never close on y=0. The old fallback then
    /// built a small top-down box around the camera, which does not contain the hull the
    /// lens is looking at, so planets and rocks in view were turned off. An open horizon
    /// covers the whole map instead.
    /// </para>
    /// </summary>
    public static class CameraGroundView
    {
        /// <summary>Pad so a rock or gem whose edge is on screen is not dropped.</summary>
        public const float BodyMargin = 16f;

        /// <summary>Pad so a large planet disk does not pop when its center leaves the rect.</summary>
        public const float PlanetMargin = 48f;

        /// <summary>Pad so a shot entering the rect this frame is still swept.</summary>
        public const float BulletMargin = 24f;

        static readonly Vector2[] Corners =
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
        };

        static int _frame = -1;
        static bool _valid;
        static float3 _center;
        static float _halfW;
        static float _halfH;
        static Camera _camera;

        public static void UseCamera(Camera camera)
        {
            if (camera != null)
                _camera = camera;
        }

        public static bool TryGet(out float3 center, out float halfW, out float halfH)
        {
            Ensure();
            center = _center;
            halfW = _halfW;
            halfH = _halfH;
            return _valid;
        }

        /// <summary>
        /// Toroidal test against the camera ground rectangle. False when the rect or the
        /// map size is not ready, so callers keep drawing instead of hiding the map.
        /// </summary>
        public static bool IsOutside(float3 point, float margin)
        {
            if (!TryGet(out float3 center, out float halfW, out float halfH))
                return false;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return false;
            return !ViewInterestMath.PointInView(
                center.x, center.z, halfW, halfH, point, mapW, mapH, margin);
        }

        public static bool IsOutside(Vector3 point, float margin) =>
            IsOutside(new float3(point.x, 0f, point.z), margin);

        static void Ensure()
        {
            if (_frame == Time.frameCount)
                return;

            _frame = Time.frameCount;
            _valid = Measure(out _center, out _halfW, out _halfH);
        }

        /// <summary>
        /// A corner ray this close to horizontal is the sky or the horizon. The visible
        /// ground then runs out of the frame, and a finite tight rect would hide it.
        /// </summary>
        const float HorizonRayY = -0.02f;

        static bool Measure(out float3 center, out float halfW, out float halfH)
        {
            center = float3.zero;
            halfW = 0f;
            halfH = 0f;

            if (_camera == null)
            {
                var follow = UnityEngine.Object.FindAnyObjectByType<CameraFollowEcs>();
                _camera = follow != null ? follow.GetComponent<Camera>() : Camera.main;
            }

            if (_camera == null)
                return false;

            var ground = new Plane(Vector3.up, Vector3.zero);
            // Beside or under the hull, the ground hits sit between the camera and the
            // bodies on screen, so those bodies fall outside the quad.
            bool openHorizon = _camera.transform.position.y <= 0.5f;
            int hits = 0;
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;

            for (int i = 0; i < Corners.Length; i++)
            {
                Ray ray = _camera.ViewportPointToRay(new Vector3(Corners[i].x, Corners[i].y, 0f));
                if (ray.direction.y >= HorizonRayY ||
                    !ground.Raycast(ray, out float dist) ||
                    dist <= 0.05f ||
                    dist >= 1000000f)
                {
                    openHorizon = true;
                    continue;
                }

                Vector3 p = ray.GetPoint(dist);
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
                hits++;
            }

            if (openHorizon || hits < Corners.Length)
            {
                if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                    return false;

                if (hits > 0)
                    center = new float3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
                else
                {
                    Vector3 pos = _camera.transform.position;
                    center = new float3(pos.x, 0f, pos.z);
                }

                halfW = mapW * 0.5f;
                halfH = mapH * 0.5f;
                return true;
            }

            center = new float3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            halfW = Mathf.Max(8f, (maxX - minX) * 0.5f);
            halfH = Mathf.Max(8f, (maxZ - minZ) * 0.5f);
            return true;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Camera;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Cosmetic-only client bullet visual. Spawned by <see cref="CombatSystem"/> on every client
    /// after the server simulation creates a bullet, and then advances on a fixed straight-line
    /// trajectory: <c>position = spawnPosition + velocity * (currentServerTime - serverSpawnTime)</c>.
    /// No NetworkObject, no NetworkTransform, no physics body — the server is solely authoritative
    /// for hit detection and damage.
    /// Using synced server time (instead of local <c>Time.time</c>) means the tracer pops in
    /// already advanced by the one-way network latency, matching where the server has actually
    /// simulated the bullet to. Without this, shots appeared to fire from where the ship was at
    /// fire time (RTT-stale) and looked like they came out behind the moving ship.
    /// </summary>
    public sealed class ClientBulletTracer : MonoBehaviour
    {
        private static Transform s_pool;
        private static UnityEngine.Camera s_cachedMainCamera;
        private static int s_cachedCameraFrame = -1;
        private static UnityEngine.Camera s_cachedGameplayCamera;
        private static readonly Dictionary<uint, ClientBulletTracer> s_bySequence = new Dictionary<uint, ClientBulletTracer>(256);

        private Vector3 logicalSpawn;
        private Vector3 velocity;
        private float serverSpawnTime;
        private float localSpawnTimeFallback;
        private float maxDistance;
        private float lifetime;
        private uint sequence;

        public static GameObject Spawn(BulletSpawnPayload payload)
        {
            EnsurePool();
            GameObject go = new GameObject("ClientBulletTracer");
            go.transform.SetParent(s_pool, false);

            Vector3 spawn = payload.SpawnPosition;
            spawn.y = 0f;
            Vector3 vel = payload.Velocity;
            vel.y = 0f;

            var tracer = go.AddComponent<ClientBulletTracer>();
            tracer.logicalSpawn = spawn;
            tracer.velocity = vel;
            tracer.serverSpawnTime = payload.ServerSpawnTime;
            tracer.localSpawnTimeFallback = Time.time;
            tracer.maxDistance = Mathf.Max(0.5f, payload.MaxDistance);
            tracer.lifetime = Mathf.Max(0.1f, payload.Lifetime);
            tracer.sequence = payload.Sequence;
            if (payload.Sequence != 0)
                s_bySequence[payload.Sequence] = tracer;

            // Initial visual position uses elapsed since server spawn (one-way client latency),
            // so the bullet appears where the server has already simulated it to instead of at
            // the ship's outdated fire-time origin.
            float elapsed = tracer.GetElapsedSinceServerSpawn();
            Vector3 logical = spawn + vel * elapsed;
            logical.y = 0f;
            go.transform.position = logical;
            if (vel.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);

            BulletShape shape = (BulletShape)Mathf.Clamp(payload.ShapeIndex, 0, 2);
            float speedForVisual = vel.magnitude;
            BulletVisualFactory.BuildVisual(
                go.transform,
                payload.VisualPrefabBankIndex,
                (TeamManager.Team)payload.OwnerTeamByte,
                shape,
                payload.ScaleMultiplier,
                speedForVisual,
                payload.NoTrailFlag != 0);

            return go;
        }

        /// <summary>Removes the tracer matching <paramref name="seq"/> if it is still alive on this client.</summary>
        public static void DespawnBySequence(uint seq)
        {
            if (seq == 0) return;
            if (s_bySequence.TryGetValue(seq, out ClientBulletTracer tracer) && tracer != null)
                Destroy(tracer.gameObject);
        }

        private void OnDestroy()
        {
            if (sequence != 0)
                s_bySequence.Remove(sequence);
        }

        private static void EnsurePool()
        {
            if (s_pool != null) return;
            var poolGo = new GameObject("ClientBulletTracers");
            Object.DontDestroyOnLoad(poolGo);
            s_pool = poolGo.transform;
        }

        private void LateUpdate()
        {
            float elapsed = GetElapsedSinceServerSpawn();
            Vector3 logical = logicalSpawn + velocity * elapsed;
            logical.y = 0f;

            if (elapsed > lifetime
                || ToroidalMap.ToroidalDistance(logical, logicalSpawn) > maxDistance)
            {
                Destroy(gameObject);
                return;
            }

            UnityEngine.Camera cam = ResolveCamera();
            Vector3 displayPos = cam != null
                ? ToroidalMap.GetDisplayPosition(logical, cam.transform.position)
                : logical;
            transform.position = displayPos;
        }

        /// <summary>
        /// Seconds elapsed since the server spawned this bullet, in the synced NGO server-time
        /// domain. Falls back to local time when offline so single-player / tests still work.
        /// </summary>
        private float GetElapsedSinceServerSpawn()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return Mathf.Max(0f, (float)nm.ServerTime.Time - serverSpawnTime);
            return Time.time - localSpawnTimeFallback;
        }

        private static UnityEngine.Camera ResolveCamera()
        {
            if (Time.frameCount != s_cachedCameraFrame)
            {
                s_cachedCameraFrame = Time.frameCount;
                s_cachedMainCamera = UnityEngine.Camera.main;
                if (s_cachedMainCamera == null || !s_cachedMainCamera.isActiveAndEnabled)
                {
                    if (s_cachedGameplayCamera == null)
                    {
                        var cc = Object.FindFirstObjectByType<CameraController>();
                        if (cc != null) s_cachedGameplayCamera = cc.GetComponent<UnityEngine.Camera>();
                    }
                }
            }
            UnityEngine.Camera cam = s_cachedMainCamera;
            if (cam == null || !cam.isActiveAndEnabled) cam = s_cachedGameplayCamera;
            return cam;
        }
    }
}

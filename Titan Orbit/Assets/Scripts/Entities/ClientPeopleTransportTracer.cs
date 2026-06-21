using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Cosmetic people-transport sphere. Magnet-steers toward ship/planet like the legacy
    /// <see cref="PeopleTransportProjectile"/> client prediction.
    /// </summary>
    public sealed class ClientPeopleTransportTracer : MonoBehaviour
    {
        private static Transform s_pool;
        private static readonly Dictionary<uint, ClientPeopleTransportTracer> s_bySequence = new Dictionary<uint, ClientPeopleTransportTracer>(64);

        private Vector3 position;
        private Vector3 velocity;
        private Vector3 spawnPosition;
        private float serverSpawnTime;
        private float cruiseSpeed;
        private uint sequence;
        private float amount;
        private TeamManager.Team team;
        private bool isLoad;
        private ulong targetNetworkObjectId;
        private ulong sourcePlanetNetworkObjectId;

        public static GameObject Spawn(PeopleTransportSpawnPayload payload)
        {
            EnsurePool();
            var go = new GameObject("ClientPeopleTransportTracer");
            go.transform.SetParent(s_pool, false);
            var tracer = go.AddComponent<ClientPeopleTransportTracer>();
            tracer.spawnPosition = payload.SpawnPosition;
            tracer.spawnPosition.y = 0f;
            tracer.position = tracer.spawnPosition;
            tracer.velocity = payload.Velocity;
            tracer.velocity.y = 0f;
            tracer.serverSpawnTime = payload.ServerSpawnTime;
            tracer.cruiseSpeed = payload.CruiseSpeed > 0.01f
                ? payload.CruiseSpeed
                : PeopleTransportProjectile.TryResolveMagnetTarget(
                    payload.IsLoadFlag != 0,
                    payload.TargetNetworkObjectId,
                    payload.SourcePlanetNetworkObjectId,
                    tracer.spawnPosition,
                    out Vector3 targetAtSpawn)
                    ? PeopleTransportProjectile.ComputeCruiseSpeed(tracer.spawnPosition, targetAtSpawn, payload.IsLoadFlag != 0)
                    : Mathf.Max(0.5f, payload.Velocity.magnitude);
            tracer.sequence = payload.Sequence;
            tracer.amount = payload.Amount;
            tracer.team = (TeamManager.Team)payload.TeamByte;
            tracer.isLoad = payload.IsLoadFlag != 0;
            tracer.targetNetworkObjectId = payload.TargetNetworkObjectId;
            tracer.sourcePlanetNetworkObjectId = payload.SourcePlanetNetworkObjectId;
            if (payload.Sequence != 0)
                s_bySequence[payload.Sequence] = tracer;

            tracer.CatchUpAfterSpawn();
            go.transform.position = tracer.GetDisplayPosition();

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(go.transform, false);
            float scale = PeopleTransportProjectile.GetVisualScaleMultiplier(tracer.amount);
            sphere.transform.localScale = Vector3.one * (0.5f * scale);
            var col = sphere.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            if (TeamManager.Instance != null)
            {
                var r = sphere.GetComponent<Renderer>();
                if (r != null) r.material.color = TeamManager.GetTeamColor(tracer.team);
            }
            return go;
        }

        public static void DespawnBySequence(uint seq)
        {
            if (seq == 0) return;
            if (s_bySequence.TryGetValue(seq, out ClientPeopleTransportTracer t) && t != null)
                Object.Destroy(t.gameObject);
        }

        private void OnDestroy()
        {
            if (sequence != 0)
                s_bySequence.Remove(sequence);
        }

        private static void EnsurePool()
        {
            if (s_pool != null) return;
            var poolGo = new GameObject("ClientPeopleTransportTracers");
            Object.DontDestroyOnLoad(poolGo);
            s_pool = poolGo.transform;
        }

        private void CatchUpAfterSpawn()
        {
            float catchUpSec = 0.06f;
            var nm = NetworkManager.Singleton;
            var transport = nm?.NetworkConfig?.NetworkTransport;
            if (transport != null)
            {
                ulong ms = transport.GetCurrentRtt(NetworkManager.ServerClientId);
                if (ms > 0)
                    catchUpSec = ms * 0.0005f;
            }
            catchUpSec = Mathf.Clamp(catchUpSec, 0f, 0.22f);
            if (catchUpSec <= 0.001f) return;

            const float step = 1f / 60f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(catchUpSec / step));
            for (int i = 0; i < steps; i++)
                StepMagnet(step);
        }

        private float GetElapsed()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return Mathf.Max(0f, (float)nm.ServerTime.Time - serverSpawnTime);
            return Time.time - serverSpawnTime;
        }

        private void StepMagnet(float dt)
        {
            if (!PeopleTransportProjectile.TryResolveMagnetTarget(
                    isLoad,
                    targetNetworkObjectId,
                    sourcePlanetNetworkObjectId,
                    position,
                    out Vector3 targetPos))
                return;

            velocity = PeopleTransportProjectile.SteerMagnetVelocity(position, targetPos, velocity, dt, cruiseSpeed);
            velocity.y = 0f;
            position += velocity * dt;
            position.y = 0f;
        }

        private Vector3 GetDisplayPosition()
        {
            var cam = UnityEngine.Camera.main;
            return cam != null
                ? ToroidalMap.GetDisplayPosition(position, cam.transform.position)
                : position;
        }

        private void Update()
        {
            StepMagnet(Time.deltaTime);
        }

        private void LateUpdate()
        {
            transform.position = GetDisplayPosition();
        }
    }
}

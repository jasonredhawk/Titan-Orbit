using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.ECS.Authoring;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only bullet tracers aligned to the visual ship proxy (weapon mount), not raw ECS origin.
    /// Runs after <see cref="EcsWorldVisualizer"/> has synced hull transforms for the frame.
    /// </summary>
    [DefaultExecutionOrder(66100)]
    public class ClientLocalBulletVfxBridge : MonoBehaviour
    {
        float _fireCooldown;
        PlayerInputHandler _input;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<ClientLocalBulletVfxBridge>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
                session.gameObject.AddComponent<ClientLocalBulletVfxBridge>();
        }

        void Start()
        {
            _input = FindAnyObjectByType<PlayerInputHandler>();
        }

        void LateUpdate()
        {
            if (_input == null || EcsGameBridge.IsLocalHost() || !TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated ||
                !TitanOrbitSessionManager.IsClientGameplayReady(world))
                return;

            if (!_input.ShootPressed || MoonOrbitClientState.IsOrbitMenuVisible)
                return;

            float dt = Time.deltaTime;
            if (_fireCooldown > 0f)
                _fireCooldown = Mathf.Max(0f, _fireCooldown - dt);

            if (_fireCooldown > 0f)
                return;

            if (!TryGetLocalShipCombatState(world.EntityManager, out int networkId, out ShipWeaponConfig weaponCfg,
                    out ShipState shipState, out ShipKinematics kinematics, out ShipInput shipInput))
                return;

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            if (!TryResolveVisualMuzzlePose(networkId, shipInput, out Vector3 fireOrigin, out Vector3 fireForward))
                return;

            Vector3 shipVel = kinematics.Velocity;
            shipVel.y = 0f;
            Vector3 bulletVel = fireForward * Mathf.Max(1f, weaponCfg.BulletSpeed) + shipVel;
            float visualScale = BulletVisualScale.ComputePerShotScale(
                weaponCfg.BulletScale,
                weaponCfg.BulletDamage,
                weaponCfg.BulletSpeed,
                weaponCfg.ReferenceBulletDamage > 0f
                    ? weaponCfg.ReferenceBulletDamage
                    : BulletVisualScale.DefaultReferenceBulletDamage,
                weaponCfg.ReferenceBulletSpeed > 0f
                    ? weaponCfg.ReferenceBulletSpeed
                    : BulletVisualScale.DefaultReferenceBulletSpeed);

            var em = world.EntityManager;
            var tracer = em.CreateEntity();
            em.AddComponentData(tracer, LocalTransform.FromPositionRotationScale(fireOrigin, quaternion.identity, 0.3f));
            em.AddComponent<BulletTracerDisplaySpace>(tracer);
            em.AddComponentData(tracer, new BulletTracerState
            {
                Position = fireOrigin,
                SpawnPosition = fireOrigin,
                Velocity = bulletVel,
                RemainingLifetime = Mathf.Max(0.1f, weaponCfg.BulletLifetime),
                MaxDistance = Mathf.Max(10f, weaponCfg.BulletMaxDistance),
                Scale = 0.3f,
                ScaleMultiplier = visualScale,
                Damage = Mathf.Max(1f, weaponCfg.BulletDamage),
                OwnerTeam = (byte)shipState.Team,
                BankIndex = 0,
            });

            _fireCooldown = 1f / Mathf.Max(0.1f, weaponCfg.FireRate);
        }

        static bool TryGetLocalShipCombatState(
            EntityManager em,
            out int networkId,
            out ShipWeaponConfig weaponCfg,
            out ShipState shipState,
            out ShipKinematics kinematics,
            out ShipInput shipInput)
        {
            networkId = 0;
            weaponCfg = default;
            shipState = default;
            kinematics = default;
            shipInput = default;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipInput>(),
                ComponentType.ReadOnly<ShipWeaponConfig>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<ShipKinematics>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            if (entities.Length == 0)
                return false;

            int localId = EcsGameBridge.GetLocalNetworkId();
            Entity shipEntity = Entity.Null;
            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (em.HasComponent<LocalPlayerShipTag>(entity))
                {
                    shipEntity = entity;
                    break;
                }

                if (localId > 0 && em.HasComponent<GhostOwner>(entity) &&
                    em.GetComponentData<GhostOwner>(entity).NetworkId == localId)
                {
                    shipEntity = entity;
                    break;
                }
            }

            if (shipEntity == Entity.Null)
                shipEntity = entities[0];

            if (em.HasComponent<GhostOwner>(shipEntity))
                networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;

            weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            shipState = em.GetComponentData<ShipState>(shipEntity);
            kinematics = em.GetComponentData<ShipKinematics>(shipEntity);
            shipInput = em.GetComponentData<ShipInput>(shipEntity);
            return true;
        }

        static bool TryResolveVisualMuzzlePose(
            int networkId,
            in ShipInput input,
            out Vector3 fireOrigin,
            out Vector3 fireForward)
        {
            fireOrigin = default;
            fireForward = Vector3.forward;

            if (!ShipWeaponProxyRegistry.TryGetHull(networkId, out var hull) || hull == null)
                return false;

            Transform muzzle = hull;
            var mountAuth = hull.GetComponentInChildren<ShipWeaponMountAuthoring>(true);
            if (mountAuth != null)
                muzzle = mountAuth.transform;

            fireOrigin = muzzle.position;
            fireForward = muzzle.forward;
            fireForward.y = 0f;

            if (fireForward.sqrMagnitude < 0.0001f)
            {
                float2 aim = input.AimPlanarDir;
                if (math.lengthsq(aim) > 0.0001f)
                    fireForward = new Vector3(aim.x, 0f, aim.y);
                else
                {
                    fireForward = hull.forward;
                    fireForward.y = 0f;
                }
            }

            if (fireForward.sqrMagnitude < 0.0001f)
                return false;

            fireForward.Normalize();
            if (mountAuth == null)
                fireOrigin = hull.position + fireForward * 2f;

            return true;
        }
    }
}

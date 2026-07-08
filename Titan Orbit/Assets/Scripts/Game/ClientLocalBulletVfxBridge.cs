using TitanOrbit.Core;
using TitanOrbit.ECS;
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
    /// Client-only bullet tracers aligned to the predicted ECS muzzle pose (same space as camera follow).
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

            if (!TryGetLocalShipCombatState(world.EntityManager, out Entity shipEntity, out ShipWeaponConfig weaponCfg,
                    out ShipState shipState, out ShipKinematics kinematics, out ShipInput shipInput))
                return;

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            if (!TryResolveEcsMuzzlePose(world.EntityManager, shipEntity, shipInput, out Vector3 fireOrigin, out Vector3 fireForward))
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
            out Entity shipEntity,
            out ShipWeaponConfig weaponCfg,
            out ShipState shipState,
            out ShipKinematics kinematics,
            out ShipInput shipInput)
        {
            shipEntity = Entity.Null;
            weaponCfg = default;
            shipState = default;
            kinematics = default;
            shipInput = default;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(EcsGameBridge.ClientWorld, out shipEntity) ||
                !em.Exists(shipEntity))
                return false;

            if (!em.HasComponent<ShipInput>(shipEntity) ||
                !em.HasComponent<ShipWeaponConfig>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<ShipKinematics>(shipEntity))
                return false;

            weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            shipState = em.GetComponentData<ShipState>(shipEntity);
            kinematics = em.GetComponentData<ShipKinematics>(shipEntity);
            shipInput = em.GetComponentData<ShipInput>(shipEntity);
            return true;
        }

        static bool TryResolveEcsMuzzlePose(
            EntityManager em,
            Entity shipEntity,
            in ShipInput input,
            out Vector3 fireOrigin,
            out Vector3 fireForward)
        {
            fireOrigin = default;
            fireForward = Vector3.forward;

            var shipTransform = em.GetComponentData<LocalTransform>(shipEntity);

            if (em.HasBuffer<ShipWeaponMountElement>(shipEntity))
            {
                var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
                if (mounts.Length > 0)
                {
                    var mount = mounts[0];
                    if (ShipWeaponPose.TryResolve(shipTransform, mount, out float3 origin, out float3 forward))
                    {
                        fireOrigin = origin;
                        fireForward = forward;
                        return true;
                    }
                }
            }

            float2 aim = input.AimPlanarDir;
            if (math.lengthsq(aim) > 0.0001f)
                fireForward = new Vector3(aim.x, 0f, aim.y).normalized;
            else
            {
                fireForward = ((Quaternion)shipTransform.Rotation) * Vector3.forward;
                fireForward.y = 0f;
                if (fireForward.sqrMagnitude < 0.0001f)
                    return false;
                fireForward.Normalize();
            }

            float muzzleOffset = 2f;
            if (em.HasComponent<ShipWeaponConfig>(shipEntity))
                muzzleOffset = em.GetComponentData<ShipWeaponConfig>(shipEntity).MuzzleOffset;

            fireOrigin = (Vector3)shipTransform.Position + fireForward * muzzleOffset;
            fireOrigin.y = shipTransform.Position.y;
            return true;
        }
    }
}

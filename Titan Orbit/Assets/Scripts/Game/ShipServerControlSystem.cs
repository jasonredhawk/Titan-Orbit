using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Local-host-only bridge: reads keyboard/mouse on the authoritative server world and writes
    /// ShipInput on the host player's ghost before ShipMovementSystem runs. Dedicated clients and
    /// remote players send input via NetCode ghost commands (ShipInputApplySystem on client).
    /// Avoids MonoBehaviour update ordering gaps during Client+Server play in the Editor.
    /// Right-click thrust still works alongside WASD / arrow keys.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(Unity.NetCode.GhostInputSystemGroup))]
    [UpdateBefore(typeof(ShipMovementSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipServerControlSystem : SystemBase
    {
        EntityQuery _playerShips;
        PlayerInputHandler _inputHandler;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            // [NETCODE] GhostOwner identifies which connection owns each ship ghost.
            _playerShips = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadWrite<ShipInput>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        protected override void OnUpdate()
        {
            // Dedicated / remote clients send ShipInput via NetCode ghost commands.
            // Only the local host reads keyboard/mouse into ShipInput on the server world.
            if (!IsLocalHostPlay())
                return;

            if (_playerShips.IsEmpty)
                return;

            if (_inputHandler == null)
                _inputHandler = Object.FindAnyObjectByType<PlayerInputHandler>();

            int playerNetworkId = GetLocalClientNetworkId();
            if (playerNetworkId <= 0)
                return;

            var cmd = BuildShipInput(_inputHandler);

            // --- Write input onto the host player's ship ghost only ---
            using var owners = _playerShips.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = _playerShips.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != playerNetworkId)
                    continue;

                var entity = entities[i];
                // [ECS/DOTS] ShipKinematics may be missing on first frame after spawn.
                if (!EntityManager.HasComponent<ShipKinematics>(entity))
                    EntityManager.AddComponentData(entity, new ShipKinematics());

                // [TITAN-ORBIT] Clear team-selection gate once host starts moving.
                if (EntityManager.HasComponent<ShipState>(entity))
                {
                    var shipState = EntityManager.GetComponentData<ShipState>(entity);
                    if (shipState.AwaitingTeamSelection)
                    {
                        shipState.AwaitingTeamSelection = false;
                        EntityManager.SetComponentData(entity, shipState);
                    }
                }

                bool wantDeposit = MoonOrbitClientState.WantDepositGems;
                cmd.WantDepositGems = wantDeposit;
                EntityManager.SetComponentData(entity, cmd);

                // [TITAN-ORBIT] Host also mirrors deposit intent for moon-dock systems.
                if (EcsGameBridge.IsLocalHost() && EntityManager.HasComponent<ShipDepositIntent>(entity))
                {
                    EntityManager.SetComponentData(entity, new ShipDepositIntent
                    {
                        WantDepositGems = wantDeposit,
                    });
                }
            }
        }

        /// <summary>
        /// True when Editor/MPPM runs client + server worlds locally (not dedicated online client).
        /// </summary>
        static bool IsLocalHostPlay()
        {
            if (TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return false;

            var client = ClientServerBootstrap.ClientWorld;
            var server = ClientServerBootstrap.ServerWorld;
            if (client == null || !client.IsCreated || server == null || !server.IsCreated)
                return false;

            return TitanOrbit.NetCode.TitanOrbitSessionManager.IsClientGameplayReady(client) &&
                   TitanOrbit.NetCode.TitanOrbitSessionManager.IsClientConnectionReady(server);
        }

        /// <summary>NetworkId of the local client connection (matches GhostOwner on host ship).</summary>
        static int GetLocalClientNetworkId()
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated)
                return -1;

            using var ids = client.EntityManager
                .CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }

        int GetFirstConnectedNetworkId()
        {
            using var ids = EntityManager
                .CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }

        /// <summary>
        /// Builds ShipInput from PlayerInputHandler (preferred) or raw keyboard/mouse fallback.
        /// Aim direction is planar (XZ) from mouse world position toward ship.
        /// </summary>
        static ShipInput BuildShipInput(PlayerInputHandler inputHandler)
        {
            float2 aimDir = float2.zero;
            bool thrust = false;
            bool spaceBrakes = true;
            bool shoot = false;

            if (inputHandler != null)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 aimWorld = inputHandler.GetMouseWorldPosition(cam);
                    Vector3 shipPos = Vector3.zero;
                    if (EcsGameBridge.TryGetLocalShipPosition(out var pos))
                        shipPos = pos;

                    Vector3 toAim = aimWorld - shipPos;
                    toAim.y = 0f;
                    if (toAim.sqrMagnitude > 0.001f)
                    {
                        Vector3 dir = toAim.normalized;
                        aimDir = new float2(dir.x, dir.z);
                    }
                }

                Vector2 move = inputHandler.GetMoveInput();
                thrust = inputHandler.MoveForwardPressed;
                spaceBrakes = inputHandler.SpaceBrakesEnabled;
                // [TITAN-ORBIT] Orbit menu open suppresses shooting (same as client path).
                shoot = inputHandler.ShootPressed && !MoonOrbitClientState.IsOrbitMenuVisible;
            }
            else
            {
                ReadFallbackInput(ref thrust, ref spaceBrakes, ref shoot);
            }

            // [NETCODE] InputEvent.Set marks Fire as pressed this tick for ghost serialization.
            var fire = new InputEvent();
            if (shoot)
                fire.Set();

            return new ShipInput
            {
                AimPlanarDir = aimDir,
                MovePlanarDir = float2.zero,
                Thrust = thrust,
                Fire = fire,
                SpaceBrakes = spaceBrakes,
                WantDepositGems = MoonOrbitClientState.WantDepositGems,
            };
        }

        /// <summary>Direct Input System read when PlayerInputHandler is not in scene.</summary>
        static void ReadFallbackInput(ref bool thrust, ref bool spaceBrakes, ref bool shoot)
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.leftCtrlKey.wasPressedThisFrame)
                    spaceBrakes = !spaceBrakes;
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                    thrust = true;
                if (!MoonOrbitClientState.IsOrbitMenuVisible)
                    shoot = mouse.leftButton.isPressed;
            }
        }
    }
}

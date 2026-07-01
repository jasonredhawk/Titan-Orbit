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
    /// Reads keyboard/mouse on the authoritative host and writes ShipInput immediately before movement.
    /// Avoids MonoBehaviour update ordering and empty NetCode input commands during local Client+Server play.
    /// Right-click thrust still works alongside WASD / arrow keys.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ShipMovementSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ShipServerControlSystem : SystemBase
    {
        EntityQuery _playerShips;
        PlayerInputHandler _inputHandler;

        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            _playerShips = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadWrite<ShipInput>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        protected override void OnUpdate()
        {
            if (_playerShips.IsEmpty)
                return;

            if (_inputHandler == null)
                _inputHandler = Object.FindAnyObjectByType<PlayerInputHandler>();

            int playerNetworkId = GetFirstConnectedNetworkId();
            if (playerNetworkId <= 0)
                return;

            var cmd = BuildShipInput(_inputHandler);

            using var owners = _playerShips.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = _playerShips.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != playerNetworkId)
                    continue;

                var entity = entities[i];
                if (!EntityManager.HasComponent<ShipKinematics>(entity))
                    EntityManager.AddComponentData(entity, new ShipKinematics());

                if (EntityManager.HasComponent<ShipState>(entity))
                {
                    var shipState = EntityManager.GetComponentData<ShipState>(entity);
                    if (shipState.AwaitingTeamSelection)
                    {
                        shipState.AwaitingTeamSelection = false;
                        EntityManager.SetComponentData(entity, shipState);
                    }
                }

                EntityManager.SetComponentData(entity, cmd);
            }
        }

        int GetFirstConnectedNetworkId()
        {
            using var ids = EntityManager
                .CreateEntityQuery(typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(NetworkId))
                .ToComponentDataArray<NetworkId>(Allocator.Temp);
            return ids.Length > 0 ? ids[0].Value : -1;
        }

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
                shoot = inputHandler.ShootPressed;
            }
            else
            {
                ReadFallbackInput(ref thrust, ref spaceBrakes, ref shoot);
            }

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
            };
        }

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
                shoot = mouse.leftButton.isPressed;
            }
        }
    }
}

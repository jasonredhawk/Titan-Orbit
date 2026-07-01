using TitanOrbit.ECS;
using TitanOrbit.Input;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Captures input for online client ghosts. Local Client+Server play uses ShipServerControlSystem on the host instead.</summary>
    [DefaultExecutionOrder(-10000)]
    public class ShipInputBridge : MonoBehaviour
    {
        PlayerInputHandler _input;

        void Start()
        {
            _input = FindAnyObjectByType<PlayerInputHandler>();
        }

        void Update()
        {
            if (_input == null || EcsGameBridge.IsLocalHost())
                return;

            ShipPendingInput.Set(BuildInput(), localHostMode: false);
        }

        ShipInput BuildInput()
        {
            var cam = UnityEngine.Camera.main;
            float2 aimDir = float2.zero;
            if (cam != null)
            {
                Vector3 aimWorld = _input.GetMouseWorldPosition(cam);
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

            Vector2 move = _input.GetMoveInput();
            var movePlanar = new float2(move.x, move.y);
            bool thrust = _input.MoveForwardPressed || math.lengthsq(movePlanar) > 0.01f;

            var fire = new InputEvent();
            if (_input.ShootPressed)
                fire.Set();

            return new ShipInput
            {
                AimPlanarDir = aimDir,
                MovePlanarDir = movePlanar,
                Thrust = thrust,
                Fire = fire,
                SpaceBrakes = _input.SpaceBrakesEnabled,
            };
        }
    }
}

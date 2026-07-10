using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Captures keyboard/mouse into <see cref="ShipPendingInput"/> for client prediction.
    /// Local host also uses <see cref="ShipServerControlSystem"/> on ServerWorld for authority.
    /// </summary>
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
            if (_input == null)
                return;

            // Local host still needs client-world ShipInput for owner prediction (ShipClientPredictedMovementSystem).
            // Server authority is handled separately by ShipServerControlSystem.
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

            bool thrust = _input.MoveForwardPressed;

            var fire = new InputEvent();
            if (_input.ShootPressed && !MoonOrbitClientState.IsOrbitMenuVisible)
                fire.Set();

            return new ShipInput
            {
                AimPlanarDir = aimDir,
                MovePlanarDir = float2.zero,
                Thrust = thrust,
                Fire = fire,
                SpaceBrakes = _input.SpaceBrakesEnabled,
                WantDepositGems = MoonOrbitClientState.WantDepositGems,
            };
        }
    }
}

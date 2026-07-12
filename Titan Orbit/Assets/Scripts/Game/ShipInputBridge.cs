using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// MonoBehaviour bridge from Unity's Update loop to ECS input. Captures keyboard/mouse via
    /// PlayerInputHandler and writes into <see cref="ShipPendingInput"/>, which
    /// <see cref="ShipInputApplySystem"/> reads during GhostInputSystemGroup on the client world.
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
            // --- Per-frame refresh ---
            if (_input == null)
                return;

            ShipPendingInput.Set(BuildInput(), localHostMode: false);
        }

        /// <summary>
        /// Converts PlayerInputHandler state into a ShipInput struct for ECS consumption.
        /// Aim direction is computed from mouse world position relative to local ship.
        /// </summary>
        ShipInput BuildInput()
        {
            // --- Build data ---
            var cam = UnityEngine.Camera.main;
            float2 aimDir = float2.zero;
            // [HYBRID] Aim from the same ECS pose the motor uses — avoids presentation/sim mismatch jitter.
            if (cam != null)
            {
                Vector3 aimWorld = _input.GetMouseWorldPosition(cam);
                Vector3 shipPos = Vector3.zero;
                if (!EcsGameBridge.TryGetLocalShipPosition(out shipPos))
                    shipPos = Vector3.zero;
                Vector3 toAim = aimWorld - shipPos;
                toAim.y = 0f;
                if (toAim.sqrMagnitude > 0.001f)
                {
                    Vector3 dir = toAim.normalized;
                    aimDir = new float2(dir.x, dir.z);
                }
            }

            bool thrust = _input.MoveForwardPressed;

            // [NETCODE] InputEvent.Set() marks fire as pressed this tick (one-shot for ghost input).
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

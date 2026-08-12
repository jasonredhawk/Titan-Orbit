using System;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// World-space Take Control button target under a planetary defense pad's level/gem labels.
    /// The UGUI <see cref="UnityEngine.UI.Button"/> calls <see cref="NotifyClicked"/>; UI hosts
    /// subscribe to <see cref="EnterRequested"/> to send the enter RPC (Game cannot reference
    /// the UI assembly directly).
    /// Presentation only — no sim writes.
    /// </summary>
    public sealed class PlanetaryDefenseTakeControlMarker : MonoBehaviour
    {
        /// <summary>
        /// Fired when the player clicks Take Control. Args: planetId, slotIndex.
        /// </summary>
        public static event Action<int, byte> EnterRequested;

        /// <summary>Stable planet id for the enter RPC.</summary>
        public int PlanetId;

        /// <summary>0-based defense slot index for the enter RPC.</summary>
        public byte SlotIndex;

        /// <summary>
        /// Called by the world-space Button onClick. Validates ids then raises
        /// <see cref="EnterRequested"/>.
        /// </summary>
        public void NotifyClicked()
        {
            if (PlanetId <= 0)
                return;
            EnterRequested?.Invoke(PlanetId, SlotIndex);
        }
    }
}

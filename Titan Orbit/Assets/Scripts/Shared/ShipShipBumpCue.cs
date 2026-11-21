using Unity.Mathematics;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// One local ship-vs-ship impact for the display ease. The client bounce pass writes
    /// it; <see cref="Game.ShipVisualSyncSystem"/> reads it. This is not a simulated
    /// position, and prediction rollback does not clear it.
    /// </summary>
    public static class ShipShipBumpCue
    {
        /// <summary>Increments on each new impact. Zero until the first one.</summary>
        public static int Serial { get; private set; }

        /// <summary>Planar unit normal that pushes the local hull away from the other ship.</summary>
        public static float3 Normal { get; private set; }

        /// <summary>Approach speed along <see cref="Normal"/>, world units/sec.</summary>
        public static float ClosingSpeed { get; private set; }

        /// <summary>
        /// Matches <c>ShipCollisionImpulseLogic.ShipShipTapClosingSpeed</c>. Kept here so
        /// this assembly does not reference simulation code. The bounce pass already
        /// skips taps; this is a second gate.
        /// </summary>
        const float TapClosingSpeed = 2f;

        /// <summary>Records one real impact. Slower closes are ignored.</summary>
        public static void NoteImpact(float3 normal, float closingSpeed)
        {
            normal.y = 0f;
            if (closingSpeed < TapClosingSpeed)
                return;
            if (math.lengthsq(normal) < 1e-8f)
                return;

            Serial++;
            if (Serial == 0)
                Serial = 1;
            Normal = math.normalize(normal);
            ClosingSpeed = closingSpeed;
        }
    }
}

using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only singleton tag for the frame's pending ramming contacts queue.
    /// Filled by Physics collision events (asteroids / ships) and server wrap-seam
    /// resolves; consumed by
    /// <see cref="ShipRammingCollisionDamageSystem"/>.
    /// </summary>
    public struct RamContactQueueTag : IComponentData { }

    /// <summary>
    /// One actual collision this physics tick (PhysX event or toroidal penetration resolve).
    /// Not ghosted — server damage only.
    /// </summary>
    public struct PendingRamContactElement : IBufferElementData
    {
        /// <summary>Ship that took part in the contact.</summary>
        public Entity Ship;

        /// <summary>Asteroid or enemy ship entity.</summary>
        public Entity Other;

        /// <summary>1 when <see cref="Other"/> is a ship; 0 when asteroid.</summary>
        public byte OtherIsShip;

        /// <summary>
        /// Contact normal pointing from Other toward Ship (XZ). Used for grind push and closing.
        /// </summary>
        public float3 NormalShipFromOther;

        /// <summary>
        /// Closing speed along the contact normal (world units/s), from impulse or pre-bounce relative vel.
        /// </summary>
        public float ClosingSpeed;

        /// <summary>Solver estimated impulse (PhysX); 0 for synthetic toroidal contacts.</summary>
        public float EstimatedImpulse;
    }

    /// <summary>
    /// Per-ship sticky contact bookkeeping for impact-once and grind throttle against one target.
    /// </summary>
    public struct ShipRamContactElement : IBufferElementData
    {
        /// <summary>Asteroid or enemy ship currently (or recently) colliding.</summary>
        public Entity Target;

        /// <summary>Server ElapsedTime when the next grind pulse is allowed.</summary>
        public double NextGrindTime;

        /// <summary>1 when we saw a real collision event/resolve with this target last tick.</summary>
        public byte WasColliding;

        /// <summary>Missed collision ticks while sticky-grinding; cleared on a fresh event.</summary>
        public byte MissedTicks;
    }
}

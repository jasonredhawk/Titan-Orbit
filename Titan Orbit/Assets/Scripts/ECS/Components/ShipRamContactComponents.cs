using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only singleton tag for the frame's pending ramming contacts queue.
    /// Filled by Physics collision events and cross-seam toroidal asteroid resolves; consumed by
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

        /// <summary>
        /// Server ElapsedTime of the last impact burst against this target.
        /// 0 means this contact has not impacted yet. Held so a bounce or a quiet
        /// PhysX gap cannot re-arm a full ram a few ticks later.
        /// </summary>
        public double LastImpactTime;
    }

    /// <summary>
    /// Prediction-only latch for one ship↔ship overlap. Remembers that the contact
    /// sample was already queued and that the local display ease already started.
    /// It does not move the hull. Not ghosted — prediction rollback restores it
    /// with the hull, and a resim of the same hit does not send a second sample.
    /// </summary>
    public struct ShipShipSlideLatch : IComponentData
    {
        /// <summary>The other hull this latch belongs to. Null when idle.</summary>
        public Entity Other;

        /// <summary>1 after this overlap has queued <c>ShipShipContactSampleRpc</c>.</summary>
        public byte SampleSent;

        /// <summary>1 after this overlap has started the local display ease.</summary>
        public byte DisplayNoted;
    }

    /// <summary>Client singleton tag for <see cref="ShipShipContactSampleElement"/>.</summary>
    public struct ShipShipContactSampleQueueTag : IComponentData { }

    /// <summary>
    /// One server contact sample on the client. Keyed by the other hull's
    /// <see cref="GhostOwner.NetworkId"/>. Replaced when a newer
    /// <see cref="ShipShipContactSampleRpc"/> arrives for that id.
    /// </summary>
    public struct ShipShipContactSampleElement : IBufferElementData
    {
        public int OtherNetworkId;
        public float3 OtherVelocity;
        public float OtherMovementMass;
        public float OtherRammingMass;
    }

    /// <summary>
    /// Collider-local XZ radius (before <c>LocalTransform.Scale</c>) for the client
    /// ship-touch test. Written once when the remote hull stops pairing with ships.
    /// </summary>
    public struct ShipPlanarHullRadius : IComponentData
    {
        public float Unscaled;
    }
}

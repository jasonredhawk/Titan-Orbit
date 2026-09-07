using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Classified Unity Physics contact this tick. One <see cref="ICollisionEventsJob"/>
    /// fills this buffer; bounce, friction, and ram consume it. No per-ship
    /// <c>CastCollider</c> / <c>CalculateDistance</c> of compound hulls against the world.
    /// </summary>
    public static class ShipPhysicsContactKind
    {
        public const byte Asteroid = 0;
        public const byte Ship = 1;
        public const byte Planet = 2;
        public const byte Moon = 3;
    }

    /// <summary>Singleton tag for the per-tick classified contact buffer.</summary>
    public struct ShipPhysicsContactQueueTag : IComponentData { }

    /// <summary>
    /// One solver contact involving a ship. Ship is always <see cref="Ship"/>;
    /// <see cref="NormalShipFromOther"/> points from Other toward Ship (XZ).
    /// </summary>
    public struct ShipPhysicsContactElement : IBufferElementData
    {
        public Entity Ship;
        public Entity Other;
        public float3 NormalShipFromOther;
        public float ClosingSpeed;
        public byte Kind;
    }
}

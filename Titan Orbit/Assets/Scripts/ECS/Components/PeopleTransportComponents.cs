using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Query filter tag — server ghost people-transport projectile (combat / delivery).
    /// Client float VFX uses <see cref="PeopleTransportPresentationTag"/> instead (RPC / VFX bridge).
    /// </summary>
    public struct PeopleTransportTag : IComponentData { }

    /// <summary>
    /// Server-only in-flight people transport (combat + delivery). Not a ghost — clients Instantiates
    /// VFX from <see cref="PeopleTransportSpawnRpc"/> (including late-join catch-up) and follow
    /// <see cref="PeopleTransportPoseRpc"/>.
    /// Bullet hits use this entity’s <see cref="Unity.Transforms.LocalTransform"/> on the server.
    /// </summary>
    public struct PeopleTransportState : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// Matches <see cref="PeopleTransportSpawnRpc.Sequence"/> so clients can apply pose/end RPCs
        /// to the correct VFX flight.
        /// </summary>
        public uint Sequence;

        /// <summary>[TITAN-ORBIT] Population units remaining in this transport capsule.</summary>
        public float Amount;

        /// <summary>[TITAN-ORBIT] Hull points — transport can be shot down mid-flight.</summary>
        public float Health;

        /// <summary>[ECS/DOTS] World-units-per-second velocity on the XZ plane.</summary>
        public float3 Velocity;

        /// <summary>[ECS/DOTS] Toroidal spawn position for trail VFX and min-travel checks.</summary>
        public float3 SpawnPosition;

        /// <summary>[UNITY] Server ElapsedTime when this transport was spawned.</summary>
        public float SpawnTime;

        /// <summary>[TITAN-ORBIT] Target cruise speed in world units per second.</summary>
        public float CruiseSpeed;

        /// <summary>
        /// Destination ship network id; 0 when transport is unload (planet-bound).
        /// </summary>
        public int TargetShipNetworkId;

        /// <summary>[TITAN-ORBIT] Source planet <see cref="PlanetState.PlanetId"/>.</summary>
        public int SourcePlanetId;

        /// <summary>[TITAN-ORBIT] Destination planet <see cref="PlanetState.PlanetId"/>.</summary>
        public int TargetPlanetId;

        /// <summary>Source ship network id when launched from a ship cargo hold.</summary>
        public int SourceShipNetworkId;

        /// <summary>[TITAN-ORBIT] 1 = loading population from planet onto ship; 0 = unloading toward target.</summary>
        public byte IsLoad;

        /// <summary>[TITAN-ORBIT] Owning team as byte (cast to <see cref="TeamId"/>).</summary>
        public byte Team;
    }

    /// <summary>
    /// Status byte on <see cref="PeopleTransportPoseRpc"/> — client VFX lifecycle.
    /// </summary>
    public static class PeopleTransportPoseStatus
    {
        /// <summary>Server sim pose for this tick — client snaps / dead-reckons the float.</summary>
        public const byte Active = 0;

        /// <summary>Delivered to the intended target (ship for load, planet for unload).</summary>
        public const byte Consumed = 1;

        /// <summary>Shot down or aborted — client despawns without +N.</summary>
        public const byte Destroyed = 2;

        /// <summary>
        /// Load flight refunded to the source planet (ship left orbit / destination gone).
        /// Client shows +N on the planet, never on the ship.
        /// </summary>
        public const byte Returned = 3;
    }

    /// <summary>
    /// [HYBRID] Client-local (non-ghost) tag for cosmetic people-transport floats.
    /// Created from <see cref="PeopleTransportSpawnRpc"/> / in-process VFX bridge — never waits on GhostSpawn.
    /// </summary>
    public struct PeopleTransportPresentationTag : IComponentData { }

    /// <summary>
    /// [HYBRID] Presentation-only flight state on the client. Magnet-steered by
    /// <c>PeopleTransportVisualSyncSystem</c>; drawn by <c>EcsWorldVisualizer</c>.
    /// </summary>
    public struct PeopleTransportPresentation : IComponentData
    {
        /// <summary>Population amount (visual scale).</summary>
        public float Amount;

        /// <summary>Current planar velocity.</summary>
        public float3 Velocity;

        /// <summary>Spawn position (for min-travel before arrive despawn).</summary>
        public float3 SpawnPosition;

        /// <summary>Baked destination from server (always valid for magnet).</summary>
        public float3 TargetPosition;

        /// <summary>Cruise speed for magnet steering.</summary>
        public float CruiseSpeed;

        /// <summary>Load destination ship network id (optional live retarget).</summary>
        public int TargetShipNetworkId;

        /// <summary>1 = load, 0 = unload.</summary>
        public byte IsLoad;

        /// <summary>Team tint byte.</summary>
        public byte Team;

        /// <summary>Seconds until auto-despawn (cosmetic lifetime).</summary>
        public float RemainingLifetime;

        /// <summary>Dedupe key from server spawn sequence.</summary>
        public uint Sequence;
    }

    /// <summary>
    /// Server-only packed escort capsule. Pose is formation-follow while waiting
    /// or magnet kinematics while that slot is the active unloader — never ghosted.
    /// Amount is the intact load size (a +36 stays +36).
    /// </summary>
    public struct PeopleEscortSlot : IBufferElementData
    {
        /// <summary>Planar world center (Y forced to 0).</summary>
        public float3 Position;

        /// <summary>Planar velocity (hover follow and landing magnet).</summary>
        public float3 Velocity;

        /// <summary>People packed in this sphere.</summary>
        public float Amount;

        /// <summary>Hull points. Replicated to clients via <see cref="PeopleEscortVitalElement"/>.</summary>
        public float Health;

        /// <summary>Landing cruise speed (world units / sec).</summary>
        public float CruiseSpeed;

        /// <summary>Position when this capsule launched toward the planet.</summary>
        public float3 SpawnPosition;

        /// <summary>1 when launched one-way at the planet; 0 otherwise.</summary>
        public byte InFlight;

        /// <summary>1 when called to ship center (preload / ready). Not launched yet.</summary>
        public byte Ready;

        /// <summary>Planet this launched capsule is committed to; 0 if not launched.</summary>
        public int TargetPlanetId;

        /// <summary>Seconds since this capsule launched (min-time contact gate).</summary>
        public float FlightElapsed;

        /// <summary>
        /// 1 when this capsule has caught its formation / ready seat and should
        /// ride the ship pose. 0 while still swarming in at its own cruise.
        /// </summary>
        public byte Riding;

        /// <summary>
        /// Stable aft-pack seat. Assigned at spawn and never reused until this
        /// slot is destroyed — adding or losing a capsule must not move the others.
        /// </summary>
        public byte SeatId;
    }

    /// <summary>
    /// Server-only landing latch. <see cref="PlanetId"/> 0 = follow beside the ship.
    /// Launches are paced by the old unload accumulator — several capsules may be in flight.
    /// Not ghosted — clients infer unload from orbit ring + dwell + planet ownership.
    /// </summary>
    public struct PeopleEscortLandingState : IComponentData
    {
        /// <summary>Planet being dropped on; 0 when escorts follow the ship.</summary>
        public int PlanetId;
    }

    /// <summary>
    /// Ghosted escort HP / amount for nameplates. One element per live capsule
    /// (matched by <see cref="SeatId"/>). 4 bytes each — not a per-capsule ghost.
    /// Must bake on the starship ghost so GhostFields replicate.
    /// Owner-predicted ships do not receive this (avoids rollback hitch in the orbit ring);
    /// interpolated remotes still get live bars.
    /// </summary>
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyInterpolatedClients)]
    [InternalBufferCapacity(16)]
    public struct PeopleEscortVitalElement : IBufferElementData
    {
        [GhostField] public byte SeatId;
        [GhostField] public byte Health;
        [GhostField] public byte Amount;
        [GhostField] public byte InFlight;
    }
}

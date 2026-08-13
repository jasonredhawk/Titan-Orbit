using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;

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

        /// <summary>Delivered or returned successfully — client shows +N and despawns.</summary>
        public const byte Consumed = 1;

        /// <summary>Shot down or aborted — client despawns without +N.</summary>
        public const byte Destroyed = 2;
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
}

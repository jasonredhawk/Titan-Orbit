using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Per-connection camera rectangle used to decide which ship ghosts and
    /// combat events are worth sending. The client writes it with <see cref="ViewInterestCommand"/>.
    /// The server copies the latest rectangles into <see cref="ViewInterestLookup"/> once per tick
    /// so bullet and gem notifies do not scan connections themselves.
    /// <para>
    /// World: the component lives on the server connection entity. Map size comes from
    /// <see cref="MapStateSingleton"/> when the lookup is published — never a made-up 1000×1000.
    /// </para>
    /// </summary>
    public struct ConnectionViewInterest : IComponentData
    {
        /// <summary>View center X (world units).</summary>
        public float CenterX;

        /// <summary>View center Z (world units).</summary>
        public float CenterZ;

        /// <summary>Half the visible ground width, including no hysteresis.</summary>
        public float HalfW;

        /// <summary>Half the visible ground height, including no hysteresis.</summary>
        public float HalfH;

        /// <summary>1 while the full minimap (expanded, death, or comms dock) is open.</summary>
        public byte FullMap;

        /// <summary>1 after the client has sent at least one camera report.</summary>
        public byte HasReport;

        /// <summary>1 when the rectangle moved enough that newly visible gems need a catch-up.</summary>
        public byte ViewMoved;

        /// <summary>1 after <see cref="PrevCenterX"/> holds a real previous rectangle.</summary>
        public byte HasPrev;

        /// <summary>Previous center X, for gem enter-view catch-up.</summary>
        public float PrevCenterX;

        /// <summary>Previous center Z.</summary>
        public float PrevCenterZ;

        /// <summary>Previous half width.</summary>
        public float PrevHalfW;

        /// <summary>Previous half height.</summary>
        public float PrevHalfH;

        /// <summary>Server elapsed seconds of the last minimap position flush.</summary>
        public double LastBlipSendTime;

        /// <summary>Server elapsed seconds of the last scoreboard flush.</summary>
        public double LastRosterSendTime;

        /// <summary>Hash of the last roster payload. Unchanged rows wait for the heartbeat.</summary>
        public int LastRosterHash;

        /// <summary>Generation byte stamped on the last blip flush.</summary>
        public byte BlipGeneration;

        /// <summary>Generation byte stamped on the last roster flush.</summary>
        public byte RosterGeneration;
    }

    /// <summary>
    /// [TITAN-ORBIT] Shared numbers for view culling. Margins are world units on the XZ torus.
    /// Enter is smaller than exit so a ship sitting on the screen edge does not spawn and despawn
    /// every tick.
    /// </summary>
    public static class ViewInterestTuning
    {
        /// <summary>Half-extent used until the first camera report arrives (around the ship).</summary>
        public const float DefaultHalfExtent = 48f;

        /// <summary>Extra world units before a ship starts replicating.</summary>
        public const float EnterMargin = 8f;

        /// <summary>Extra world units before a ship stops replicating.</summary>
        public const float ExitMargin = 18f;

        /// <summary>
        /// Extra world units around the camera for gem spawn, consume, and the local cull.
        /// The client keeps crystals this far past the screen, so the server must still
        /// tell that same band about scoops. Catch-up uses the same pad.
        /// </summary>
        public const float GemKeepMargin = 24f;

        /// <summary>Expanded-minimap position flush period (seconds).</summary>
        public const float BlipInterval = 0.25f;

        /// <summary>Scoreboard refresh even when scores did not change (seconds).</summary>
        public const float RosterHeartbeat = 2f;

        /// <summary>Minimum gap between scoreboard flushes when scores are changing.</summary>
        public const float RosterMinInterval = 0.5f;

        /// <summary>Ships packed into one <see cref="ShipBlipSnapshotRpc"/>.</summary>
        public const int BlipsPerPacket = 8;

        /// <summary>Rows packed into one <see cref="ShipRosterSnapshotRpc"/>.</summary>
        public const int RosterPerPacket = 4;

        /// <summary>Flag bit: hull is a MEGA.</summary>
        public const byte FlagMega = 1;

        /// <summary>Flag bit: hull is dead.</summary>
        public const byte FlagDead = 2;

        /// <summary>Flag bit: player has not picked a team.</summary>
        public const byte FlagAwaiting = 4;

        /// <summary>
        /// [TITAN-ORBIT] UI writes this; the camera reporter reads it. The Game assembly cannot
        /// see the minimap MonoBehaviour, so the flag lives here.
        /// </summary>
        public static bool ClientWantsAllShipPositions;
    }

    /// <summary>
    /// [TITAN-ORBIT] Toroidal overlap tests against one camera rectangle. The view is much smaller
    /// than half the map, so <see cref="ToroidalMapEcs.ShortestOffsetXZ"/> is the right test.
    /// </summary>
    public static class ViewInterestMath
    {
        /// <summary>
        /// True when <paramref name="point"/> lies inside the axis-aligned ground rect, grown by
        /// <paramref name="margin"/> on each side. Y is ignored.
        /// </summary>
        public static bool PointInView(
            float centerX,
            float centerZ,
            float halfW,
            float halfH,
            float3 point,
            float mapW,
            float mapH,
            float margin)
        {
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return false;

            float3 center = new float3(centerX, 0f, centerZ);
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(center, point, mapW, mapH);
            return math.abs(offset.x) <= halfW + margin && math.abs(offset.z) <= halfH + margin;
        }

        /// <summary>
        /// True when the shortest-path segment from <paramref name="from"/> to <paramref name="to"/>
        /// crosses the ground rect. A zero-length segment is a point test.
        /// </summary>
        public static bool SegmentOverlapsView(
            float centerX,
            float centerZ,
            float halfW,
            float halfH,
            float3 from,
            float3 to,
            float mapW,
            float mapH,
            float margin)
        {
            if (!ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return false;

            float3 center = new float3(centerX, 0f, centerZ);
            float3 a = ToroidalMapEcs.ShortestOffsetXZ(center, from, mapW, mapH);
            float3 delta = ToroidalMapEcs.ShortestOffsetXZ(from, to, mapW, mapH);
            float hw = halfW + margin;
            float hh = halfH + margin;
            return SegmentHitsAabb(a.x, a.z, a.x + delta.x, a.z + delta.z, -hw, hw, -hh, hh);
        }

        /// <summary>
        /// Liang–Barsky clip of a segment against an axis-aligned box. True when any part of the
        /// segment lies inside.
        /// </summary>
        static bool SegmentHitsAabb(
            float x0,
            float z0,
            float x1,
            float z1,
            float minX,
            float maxX,
            float minZ,
            float maxZ)
        {
            float t0 = 0f;
            float t1 = 1f;
            if (!Clip(-(x1 - x0), x0 - minX, ref t0, ref t1))
                return false;
            if (!Clip(x1 - x0, maxX - x0, ref t0, ref t1))
                return false;
            if (!Clip(-(z1 - z0), z0 - minZ, ref t0, ref t1))
                return false;
            if (!Clip(z1 - z0, maxZ - z0, ref t0, ref t1))
                return false;
            return true;
        }

        /// <summary>One edge of the Liang–Barsky clip. Updates the entering and leaving times.</summary>
        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (math.abs(p) < 1e-6f)
                return q >= 0f;

            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                    return false;
                if (r > t0)
                    t0 = r;
            }
            else
            {
                if (r < t0)
                    return false;
                if (r < t1)
                    t1 = r;
            }

            return true;
        }
    }

    /// <summary>
    /// One player's stored view, copied out of ECS so combat notifies can loop it without a query.
    /// </summary>
    public struct ViewInterestSlot
    {
        /// <summary>Server connection entity. Targeted RPCs use this as <c>TargetConnection</c>.</summary>
        public Entity Connection;

        /// <summary><see cref="NetworkId"/> of that connection.</summary>
        public int NetworkId;

        /// <summary>View center X.</summary>
        public float CenterX;

        /// <summary>View center Z.</summary>
        public float CenterZ;

        /// <summary>Half width.</summary>
        public float HalfW;

        /// <summary>Half height.</summary>
        public float HalfH;

        /// <summary>1 when the full map is open.</summary>
        public byte FullMap;
    }

    /// <summary>
    /// [TITAN-ORBIT] Main-thread cache of every in-game view, rebuilt at the start of the server
    /// sim tick. Combat code reads <see cref="At"/> and does not allocate.
    /// </summary>
    public static class ViewInterestLookup
    {
        static NativeList<ViewInterestSlot> s_Slots;
        static float s_MapW;
        static float s_MapH;

        /// <summary>How many connections were published this tick.</summary>
        public static int Count => s_Slots.IsCreated ? s_Slots.Length : 0;

        /// <summary>True when <see cref="MapStateSingleton"/> had a real rolled size this tick.</summary>
        public static bool HasMap => ToroidalMapEcs.IsValidMapSize(s_MapW, s_MapH);

        /// <summary>Map width published with the slots. Zero when the map is not ready.</summary>
        public static float MapW => s_MapW;

        /// <summary>Map height published with the slots.</summary>
        public static float MapH => s_MapH;

        /// <summary>Slot written by the view system this tick.</summary>
        public static ViewInterestSlot At(int index) => s_Slots[index];

        /// <summary>
        /// [UNITY] Domain reload off: the native list would otherwise survive into the next Play.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            if (s_Slots.IsCreated)
                s_Slots.Dispose();
            s_Slots = default;
            s_MapW = 0f;
            s_MapH = 0f;
        }

        /// <summary>Drops last tick's slots and remembers the map size. Call once per server tick.</summary>
        public static void BeginFrame(float mapW, float mapH)
        {
            if (!s_Slots.IsCreated)
                s_Slots = new NativeList<ViewInterestSlot>(32, Allocator.Persistent);
            s_Slots.Clear();
            s_MapW = mapW;
            s_MapH = mapH;
        }

        /// <summary>Appends one connection. No-op before <see cref="BeginFrame"/>.</summary>
        public static void Add(in ViewInterestSlot slot)
        {
            if (!s_Slots.IsCreated)
                return;
            s_Slots.Add(slot);
        }
    }

    /// <summary>
    /// [NETCODE] Creates one targeted RPC entity per connection whose view overlaps an event.
    /// A fight on the other side of the map adds zero entities. The in-process host bridge is
    /// separate and does not use the network.
    /// </summary>
    public static class ViewInterestFanout
    {
        /// <summary>
        /// Sends <paramref name="rpc"/> to every connection that can see <paramref name="point"/>,
        /// plus <paramref name="alwaysNetworkId"/> when that id is non-zero (the shooter).
        /// </summary>
        public static void EmitPoint<T>(
            ref EntityCommandBuffer ecb,
            in T rpc,
            float3 point,
            float margin,
            int alwaysNetworkId)
            where T : unmanaged, IRpcCommand
        {
            if (!ViewInterestLookup.HasMap)
                return;

            float mapW = ViewInterestLookup.MapW;
            float mapH = ViewInterestLookup.MapH;
            int count = ViewInterestLookup.Count;
            for (int i = 0; i < count; i++)
            {
                ViewInterestSlot slot = ViewInterestLookup.At(i);
                bool force = alwaysNetworkId != 0 && slot.NetworkId == alwaysNetworkId;
                bool see = ViewInterestMath.PointInView(
                    slot.CenterX, slot.CenterZ, slot.HalfW, slot.HalfH, point, mapW, mapH, margin);
                if (!force && !see)
                    continue;

                Entity rpcEntity = ecb.CreateEntity();
                ecb.AddComponent(rpcEntity, rpc);
                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = slot.Connection });
            }
        }

        /// <summary>
        /// Sends <paramref name="rpc"/> when the segment crosses the view, or to
        /// <paramref name="alwaysNetworkId"/> (so your own shot is not culled at the edge).
        /// </summary>
        public static void EmitSegment<T>(
            ref EntityCommandBuffer ecb,
            in T rpc,
            float3 from,
            float3 to,
            float margin,
            int alwaysNetworkId)
            where T : unmanaged, IRpcCommand
        {
            if (!ViewInterestLookup.HasMap)
                return;

            float mapW = ViewInterestLookup.MapW;
            float mapH = ViewInterestLookup.MapH;
            int count = ViewInterestLookup.Count;
            for (int i = 0; i < count; i++)
            {
                ViewInterestSlot slot = ViewInterestLookup.At(i);
                bool force = alwaysNetworkId != 0 && slot.NetworkId == alwaysNetworkId;
                bool see = ViewInterestMath.SegmentOverlapsView(
                    slot.CenterX, slot.CenterZ, slot.HalfW, slot.HalfH, from, to, mapW, mapH, margin);
                if (!force && !see)
                    continue;

                Entity rpcEntity = ecb.CreateEntity();
                ecb.AddComponent(rpcEntity, rpc);
                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = slot.Connection });
            }
        }
    }
}

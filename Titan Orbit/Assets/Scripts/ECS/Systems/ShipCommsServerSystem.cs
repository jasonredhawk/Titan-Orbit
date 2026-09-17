using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: accepts <see cref="ShipCommsCommand"/>, checks the speaker has a living ship
    /// that is not jammed in enemy territory, rate-limits the connection, then sends
    /// <see cref="ShipCommsRpc"/> to All clients, teammates, or teammates-as-Commander
    /// (earned killer / miner / troop title + command-deck words). Recipients currently flying in a
    /// non-friendly triangle are skipped so jammed hulls cannot hear callouts.
    /// <para>
    /// World: ServerSimulation. Group: SimulationSystemGroup. Not Burst-compiled — we load the
    /// managed <see cref="ShipCommsKeywordCatalog"/> to validate indices.
    /// </para>
    /// <para>
    /// [NETCODE] Owner comes from <see cref="ReceiveRpcCommandRequest.SourceConnection"/> →
    /// <see cref="NetworkId"/>. The command has no client-supplied id, so a player cannot put
    /// chips above someone else's hull. <c>TeamOnly</c> is a channel request (All / Team /
    /// Commander); this system reads the speaker's <see cref="ShipState.Team"/> and
    /// earned command seat and targets those connections so a client cannot leak team chat
    /// to enemies or spoof command-deck words. Local Host injects the command with
    /// <c>ReceiveRpcCommandRequest</c> already set (see <c>ShipCommsRpcClient</c>) because
    /// client→server SendRpc can drop under Instantiates load.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipCommsServerSystem : ISystem
    {
        /// <summary>
        /// [TITAN-ORBIT] Minimum seconds between accepted callouts on one connection.
        /// Cheap anti-spam — comms are social, not a combat input.
        /// </summary>
        const double RateLimitSeconds = 1.5d;

        /// <summary>
        /// Drains inbound comms commands and broadcasts accepted sentences.
        /// <para>
        /// [ECS/DOTS] ISystem structs must stay unmanaged — do not store the ScriptableObject
        /// catalog as a field (that made TypeManager try to new this as a class system and
        /// threw "does not inherit from ComponentSystemBase").
        /// </para>
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // ScriptableObject catalog — static-cached inside LoadDefault, safe to call here.
            ShipCommsKeywordCatalog catalog = ShipCommsKeywordCatalog.LoadDefault();

            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            double now = SystemAPI.Time.ElapsedTime;

            // --- Drain client → server commands ---
            // [NETCODE] ReceiveRpcCommandRequest pairs the RPC with the sending connection.
            foreach (var (cmd, req, rpcEntity) in SystemAPI
                         .Query<RefRO<ShipCommsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                // Always consume the RPC entity — a rejected send must not linger forever.
                ecb.DestroyEntity(rpcEntity);

                Entity connection = req.ValueRO.SourceConnection;
                if (!em.HasComponent<NetworkId>(connection))
                    continue;

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (networkId <= 0)
                    continue;

                ShipCommsCommand sentence = cmd.ValueRO;
                if (!catalog.IsValidSequence(
                        sentence.Count,
                        sentence.K0,
                        sentence.K1,
                        sentence.K2,
                        sentence.K3,
                        sentence.K4))
                    continue;

                // --- Living ship ---
                // Ghost — NetCode replica. IsDead means hull+cargo emptied; AwaitingTeamSelection
                // is the join-team plaque before the player has a flying hull.
                // Use EntityManager (not a nested SystemAPI.Query) — we are already iterating RPCs.
                if (!TryGetLivingSpeaker(em, networkId, out TeamId speakerTeam, out float3 speakerPos))
                    continue;

                // --- Enemy-territory jam ---
                // [TITAN-ORBIT] A hull inside a triangle it does not own cannot speak.
                // Open space and friendly overlaps stay clear. Client HUD shows the lock;
                // this reject is the authority so a spoofed RPC cannot leak chips.
                if (ShipCommsJam.IsPositionJammed(
                        speakerPos, speakerTeam, PlanetConnectionGraphSide.Server))
                    continue;

                // --- Commander gate ---
                // [TITAN-ORBIT] Commander keywords and the Commander channel are for
                // earned category titles (living top killer / miner / troop mover).
                // Snapshot is rebuilt first each sim tick — the client pill is only a request.
                ShipCommsChannel channel = TeamCommanderRules.Sanitize(sentence.TeamOnly);
                bool usesCommanderWords = SequenceUsesCommanderKeyword(catalog, sentence);
                bool isCommander = SystemAPI.TryGetSingleton<ShipCommandRoleSnapshot>(out var roles)
                    && roles.HoldsCommandSeat(speakerTeam, networkId);
                if (usesCommanderWords && (!isCommander || channel != ShipCommsChannel.Commander))
                    continue;
                if (channel == ShipCommsChannel.Commander && !isCommander)
                    channel = ShipCommsChannel.Team;

                // --- Rate limit ---
                // [TITAN-ORBIT] Cooldown lives on the connection entity (not the ship) so a
                // respawn cannot reset the timer by spawning a new hull. Applied only after
                // the ship check so a dead speaker does not burn the cooldown.
                if (em.HasComponent<ShipCommsCooldown>(connection))
                {
                    double last = em.GetComponentData<ShipCommsCooldown>(connection).LastSendElapsed;
                    if (now - last < RateLimitSeconds)
                        continue;

                    ecb.SetComponent(connection, new ShipCommsCooldown { LastSendElapsed = now });
                }
                else
                {
                    ecb.AddComponent(connection, new ShipCommsCooldown { LastSendElapsed = now });
                }

                Deliver(ecb, em, networkId, speakerTeam, sentence, channel);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// True when a ship ghost owned by <paramref name="networkId"/> is alive and in play.
        /// Writes that hull's team so team-only delivery can target teammates, and the
        /// world pose so enemy-territory jam can run the same point-in-triangle test
        /// the motor uses for friendly speed.
        /// Ships are few — a linear query on an RPC (not every tick) is cheap.
        /// </summary>
        /// <param name="em">Server world EntityManager.</param>
        /// <param name="networkId">GhostOwner id of the connection that sent the command.</param>
        /// <param name="team">Living hull's faction, or None when not found.</param>
        /// <param name="position">Living hull <see cref="LocalTransform.Position"/> (Y unused).</param>
        static bool TryGetLivingSpeaker(
            EntityManager em,
            int networkId,
            out TeamId team,
            out float3 position)
        {
            team = TeamId.None;
            position = float3.zero;

            // LocalTransform — DOTS world pose on the ghost. Same component the drive job
            // wraps into the canonical torus after physics.
            using var query = em.CreateEntityQuery(
                typeof(ShipTag), typeof(GhostOwner), typeof(ShipState), typeof(LocalTransform));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;

                if (states[i].IsDead || states[i].AwaitingTeamSelection)
                    return false;

                team = states[i].Team;
                position = transforms[i].Position;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when any live keyword in <paramref name="sentence"/> is a command-deck
        /// word (Everyone, Us, Form Up, …). Unused slots after Count are ignored.
        /// </summary>
        static bool SequenceUsesCommanderKeyword(
            ShipCommsKeywordCatalog catalog,
            in ShipCommsCommand sentence)
        {
            if (catalog.IsCommanderKeyword(sentence.K0))
                return true;
            if (sentence.Count >= 2 && catalog.IsCommanderKeyword(sentence.K1))
                return true;
            if (sentence.Count >= 3 && catalog.IsCommanderKeyword(sentence.K2))
                return true;
            if (sentence.Count >= 4 && catalog.IsCommanderKeyword(sentence.K3))
                return true;
            if (sentence.Count >= 5 && catalog.IsCommanderKeyword(sentence.K4))
                return true;
            return false;
        }

        /// <summary>
        /// All → every connected client. Team / Commander → each in-game connection whose
        /// living or dead hull shares the speaker's <see cref="TeamId"/>. No team on the
        /// speaker falls back to the speaker only so "TEAM" never leaks to the whole match.
        /// </summary>
        static void Deliver(
            EntityCommandBuffer ecb,
            EntityManager em,
            int networkId,
            TeamId speakerTeam,
            in ShipCommsCommand sentence,
            ShipCommsChannel channel)
        {
            byte teamOnly = (byte)channel;
            byte hasWaypoint = sentence.HasWaypoint != 0 ? (byte)1 : (byte)0;
            float waypointX = sentence.WaypointX;
            float waypointZ = sentence.WaypointZ;
            if (hasWaypoint != 0 &&
                (!IsFinite(waypointX) || !IsFinite(waypointZ)))
            {
                hasWaypoint = 0;
                waypointX = 0f;
                waypointZ = 0f;
            }

            byte focusKind = sentence.FocusKind;
            if (focusKind > ShipCommsInbox.FocusKind.Gem)
                focusKind = ShipCommsInbox.FocusKind.None;
            if ((focusKind == ShipCommsInbox.FocusKind.MapPing
                    || focusKind == ShipCommsInbox.FocusKind.Asteroid
                    || focusKind == ShipCommsInbox.FocusKind.Gem)
                && hasWaypoint == 0)
                focusKind = ShipCommsInbox.FocusKind.None;

            int planetId = sentence.PlanetId > 0 ? sentence.PlanetId : 0;
            if ((focusKind == ShipCommsInbox.FocusKind.Planet
                    || focusKind == ShipCommsInbox.FocusKind.Moon
                    || focusKind == ShipCommsInbox.FocusKind.Pad
                    || focusKind == ShipCommsInbox.FocusKind.Turret)
                && planetId <= 0)
                focusKind = ShipCommsInbox.FocusKind.None;

            var rpc = new ShipCommsRpc
            {
                NetworkId = networkId,
                Count = sentence.Count,
                K0 = sentence.K0,
                K1 = sentence.K1,
                K2 = sentence.K2,
                K3 = sentence.K3,
                K4 = sentence.K4,
                TeamOnly = teamOnly,
                HasWaypoint = hasWaypoint,
                WaypointX = waypointX,
                WaypointZ = waypointZ,
                FocusKind = focusKind,
                YouNetworkId = sentence.YouNetworkId > 0 ? sentence.YouNetworkId : 0,
                PlanetId = planetId,
                Everyone = sentence.Everyone != 0 ? (byte)1 : (byte)0,
                Us0 = sentence.Us0 > 0 ? sentence.Us0 : 0,
                Us1 = sentence.Us1 > 0 ? sentence.Us1 : 0,
                Us2 = sentence.Us2 > 0 ? sentence.Us2 : 0,
                Us3 = sentence.Us3 > 0 ? sentence.Us3 : 0,
                MeX = sentence.MeX,
                MeZ = sentence.MeZ,
                YouX = sentence.YouX,
                YouZ = sentence.YouZ,
                GroupCount = sentence.GroupCount,
                G0X = sentence.G0X, G0Z = sentence.G0Z,
                G1X = sentence.G1X, G1Z = sentence.G1Z,
                G2X = sentence.G2X, G2Z = sentence.G2Z,
                G3X = sentence.G3X, G3Z = sentence.G3Z,
                G4X = sentence.G4X, G4Z = sentence.G4Z,
                G5X = sentence.G5X, G5Z = sentence.G5Z,
                G6X = sentence.G6X, G6Z = sentence.G6Z,
                G7X = sentence.G7X, G7Z = sentence.G7Z,
            };

            if (!TeamCommanderRules.IsTeamScoped(channel))
            {
                SendToEligibleConnections(ecb, em, rpc, teamFilter: TeamId.None);
                return;
            }

            // --- Team / Commander channel ---
            // [TITAN-ORBIT] TeamId.None is the join-team plaque. A living speaker should
            // already have a faction; if they do not, only they see the chips.
            if (speakerTeam == TeamId.None)
            {
                SendToNetworkId(ecb, em, networkId, rpc);
                return;
            }

            SendToEligibleConnections(ecb, em, rpc, teamFilter: speakerTeam);
        }

        /// <summary>
        /// Sends one targeted RPC per in-game connection that may hear this callout.
        /// <paramref name="teamFilter"/> None = All channel (every faction). A real
        /// team = Team / Commander (same-team hulls only).
        /// <para>
        /// [NETCODE] We no longer use TargetConnection = Null for All. A broadcast
        /// would also land on jammed listeners. One targeted send per connection lets
        /// us skip hulls flying in enemy fill. Ships are few; this is still one RPC
        /// event, not a per-tick gather.
        /// </para>
        /// Dead hulls still receive (death screen). AwaitingTeamSelection is skipped
        /// on the Team channel. Living hulls in a non-friendly triangle are skipped
        /// on every channel.
        /// </summary>
        /// <param name="ecb">Playback buffer for the announce entities.</param>
        /// <param name="em">Server world EntityManager.</param>
        /// <param name="rpc">Accepted sentence to echo.</param>
        /// <param name="teamFilter">None = every in-game connection; else that team only.</param>
        static void SendToEligibleConnections(
            EntityCommandBuffer ecb,
            EntityManager em,
            in ShipCommsRpc rpc,
            TeamId teamFilter)
        {
            // --- Ship snapshots for team + jam ---
            // LocalTransform is the same pose the drive job wraps. Dead ships keep a
            // pose but ConnectionIsJammed treats IsDead as clear (not flying).
            using var shipQuery = em.CreateEntityQuery(
                typeof(ShipTag), typeof(GhostOwner), typeof(ShipState), typeof(LocalTransform));
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            using var connQuery = em.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamInGame));
            using var connections = connQuery.ToEntityArray(Allocator.Temp);
            using var ids = connQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);

            for (int c = 0; c < connections.Length; c++)
            {
                int connId = ids[c].Value;

                // --- Team channel: same faction, already picked a color ---
                if (teamFilter != TeamId.None && !ConnectionIsOnTeam(owners, states, connId, teamFilter))
                    continue;

                // --- All / Team: skip living hulls inside enemy fill ---
                if (ConnectionIsJammed(owners, states, transforms, connId))
                    continue;

                Entity announce = ecb.CreateEntity();
                ecb.AddComponent(announce, rpc);
                ecb.AddComponent(announce, new SendRpcCommandRequest { TargetConnection = connections[c] });
            }
        }

        /// <summary>
        /// Sends one targeted RPC to the connection whose <see cref="NetworkId"/> matches.
        /// Used when team-only has no faction to scope to.
        /// </summary>
        static void SendToNetworkId(EntityCommandBuffer ecb, EntityManager em, int networkId, in ShipCommsRpc rpc)
        {
            using var query = em.CreateEntityQuery(typeof(NetworkId), typeof(NetworkStreamInGame));
            using var connections = query.ToEntityArray(Allocator.Temp);
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            for (int i = 0; i < connections.Length; i++)
            {
                if (ids[i].Value != networkId)
                    continue;

                Entity announce = ecb.CreateEntity();
                ecb.AddComponent(announce, rpc);
                ecb.AddComponent(announce, new SendRpcCommandRequest { TargetConnection = connections[i] });
                return;
            }
        }

        /// <summary>
        /// True when <paramref name="networkId"/> owns a living ship that is currently
        /// inside a non-friendly triangle. Dead / join-plaque hulls are not jammed —
        /// they are not flying. A connection with no ship stays clear (fail open).
        /// </summary>
        static bool ConnectionIsJammed(
            NativeArray<GhostOwner> owners,
            NativeArray<ShipState> states,
            NativeArray<LocalTransform> transforms,
            int networkId)
        {
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;

                // Death screen / Join Team: not flying, so they may still hear.
                if (states[i].IsDead || states[i].AwaitingTeamSelection)
                    return false;

                return ShipCommsJam.IsPositionJammed(
                    transforms[i].Position,
                    states[i].Team,
                    PlanetConnectionGraphSide.Server);
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="networkId"/> owns a ship on <paramref name="team"/>
        /// that has already picked a faction.
        /// </summary>
        static bool ConnectionIsOnTeam(
            NativeArray<GhostOwner> owners,
            NativeArray<ShipState> states,
            int networkId,
            TeamId team)
        {
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                if (states[i].AwaitingTeamSelection)
                    return false;
                return states[i].Team == team;
            }

            return false;
        }

        /// <summary>True when the float is a usable world coordinate (not NaN / inf).</summary>
        static bool IsFinite(float value)
        {
            return value >= float.MinValue && value <= float.MaxValue;
        }
    }
}

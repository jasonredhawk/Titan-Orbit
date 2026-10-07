using System;
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
    /// Turns an accepted commander Comms Matrix sentence into a server-only team order.
    /// Regular Team and All chat does not call this. The wire format stays unchanged.
    /// </summary>
    public static class BotShipOrderLogic
    {
        public const float OrderLifetimeSeconds = 30f;

        public static void EnsureSingleton(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<BotTeamOrders>());
            if (query.CalculateEntityCount() != 0)
                return;

            Entity singleton = em.CreateEntity();
            em.AddComponentData(singleton, new BotTeamOrders());
        }

        /// <summary>
        /// Writes the speaker's team order when the sentence contains a task verb.
        /// A sentence with no task verb leaves the current order in place.
        /// Follow and Escort only stick when the subject hull is on the speaker's team.
        /// A bot <c>You</c> retasks that one hull.
        /// </summary>
        public static void TryApply(
            EntityManager em,
            ShipCommsKeywordCatalog catalog,
            TeamId team,
            int speakerNetworkId,
            float3 speakerPos,
            in ShipCommsCommand sentence,
            float now,
            bool mayOrderAll)
        {
            if (catalog == null || team == TeamId.None)
                return;

            using var query = em.CreateEntityQuery(ComponentType.ReadWrite<BotTeamOrders>());
            if (query.CalculateEntityCount() != 1)
                return;

            Entity entity = query.GetSingletonEntity();
            var orders = em.GetComponentData<BotTeamOrders>(entity);

            if (!TryBuild(em, catalog, team, speakerNetworkId, speakerPos, sentence, now, mayOrderAll, out BotTeamOrder order))
                return;

            orders.Set(team, order);
            em.SetComponentData(entity, orders);
        }

        static bool TryBuild(
            EntityManager em,
            ShipCommsKeywordCatalog catalog,
            TeamId team,
            int speakerNetworkId,
            float3 speakerPos,
            in ShipCommsCommand sentence,
            float now,
            bool mayOrderAll,
            out BotTeamOrder order)
        {
            order = default;
            BotTaskKind task = BotTaskKind.None;
            bool hasYou = false;
            int count = math.clamp((int)sentence.Count, 0, 5);
            for (int i = 0; i < count; i++)
            {
                if (!catalog.TryGetLabel(KeywordAt(sentence, i), out string label))
                    continue;
                if (Eq(label, "You"))
                    hasYou = true;
                if (task != BotTaskKind.None)
                    continue;
                task = TaskForLabel(label);
            }

            if (task == BotTaskKind.None)
                return false;

            int audience = 0;
            int extraA = 0;
            int extraB = 0;
            int extraC = 0;
            int extraD = 0;
            int botCount = 0;
            ConsiderBot(em, team, sentence.YouNetworkId, ref botCount, ref audience, ref extraA, ref extraB, ref extraC, ref extraD);
            ConsiderBot(em, team, sentence.Us0, ref botCount, ref audience, ref extraA, ref extraB, ref extraC, ref extraD);
            ConsiderBot(em, team, sentence.Us1, ref botCount, ref audience, ref extraA, ref extraB, ref extraC, ref extraD);
            ConsiderBot(em, team, sentence.Us2, ref botCount, ref audience, ref extraA, ref extraB, ref extraC, ref extraD);
            ConsiderBot(em, team, sentence.Us3, ref botCount, ref audience, ref extraA, ref extraB, ref extraC, ref extraD);

            bool circledSomeone = sentence.YouNetworkId > 0
                                  || sentence.Us0 > 0
                                  || sentence.Us1 > 0
                                  || sentence.Us2 > 0
                                  || sentence.Us3 > 0;
            byte limitAudience = 0;
            if (botCount > 0)
                limitAudience = 1;
            else if (mayOrderAll && (!circledSomeone || sentence.Everyone != 0))
                limitAudience = 0;
            else
                return false;

            bool youIsBot = hasYou && BotShipIds.IsBot(sentence.YouNetworkId);

            int subject = speakerNetworkId;
            if (hasYou && !youIsBot && sentence.YouNetworkId > 0)
                subject = sentence.YouNetworkId;

            if ((task == BotTaskKind.Follow || task == BotTaskKind.Defend) &&
                subject > 0 &&
                subject != speakerNetworkId &&
                !IsLivingTeammate(em, subject, team))
                return false;

            bool hasWaypoint = sentence.HasWaypoint != 0 &&
                               IsFinite(sentence.WaypointX) &&
                               IsFinite(sentence.WaypointZ);

            order = new BotTeamOrder
            {
                Task = task,
                AudienceNetworkId = limitAudience != 0 ? audience : 0,
                AudienceA = limitAudience != 0 ? extraA : 0,
                AudienceB = limitAudience != 0 ? extraB : 0,
                AudienceC = limitAudience != 0 ? extraC : 0,
                AudienceD = limitAudience != 0 ? extraD : 0,
                LimitAudience = limitAudience,
                SubjectNetworkId = subject,
                PlanetId = sentence.PlanetId,
                WaypointX = hasWaypoint ? sentence.WaypointX : speakerPos.x,
                WaypointZ = hasWaypoint ? sentence.WaypointZ : speakerPos.z,
                HasWaypoint = 1,
                ExpireTime = now + OrderLifetimeSeconds,
            };
            return true;
        }

        static BotTaskKind TaskForLabel(string label)
        {
            if (Eq(label, "Mining") || Eq(label, "Mine"))
                return BotTaskKind.Mine;
            if (Eq(label, "Deposit") || Eq(label, "Dock"))
                return BotTaskKind.Deposit;
            if (Eq(label, "Transport") || Eq(label, "Troops") || Eq(label, "Capture"))
                return BotTaskKind.Transport;
            if (Eq(label, "Attack") || Eq(label, "Kill") || Eq(label, "Push") || Eq(label, "Incoming"))
                return BotTaskKind.Attack;
            if (Eq(label, "Defend") || Eq(label, "Cover") || Eq(label, "Help"))
                return BotTaskKind.Defend;
            if (Eq(label, "Follow") || Eq(label, "Escort") || Eq(label, "Form Up"))
                return BotTaskKind.Follow;
            if (Eq(label, "Wait") || Eq(label, "Hold") || Eq(label, "Retreat"))
                return BotTaskKind.Hold;
            if (Eq(label, "Advance"))
                return BotTaskKind.Advance;
            return BotTaskKind.None;
        }

        static void ConsiderBot(
            EntityManager em,
            TeamId team,
            int networkId,
            ref int count,
            ref int audience,
            ref int extraA,
            ref int extraB,
            ref int extraC,
            ref int extraD)
        {
            if (count >= 5 || !BotShipIds.IsBot(networkId) || !IsLivingTeammate(em, networkId, team))
                return;
            if (networkId == audience || networkId == extraA || networkId == extraB
                || networkId == extraC || networkId == extraD)
                return;

            switch (count)
            {
                case 0: audience = networkId; break;
                case 1: extraA = networkId; break;
                case 2: extraB = networkId; break;
                case 3: extraC = networkId; break;
                default: extraD = networkId; break;
            }

            count++;
        }

        static byte KeywordAt(in ShipCommsCommand sentence, int index)
        {
            switch (index)
            {
                case 0: return sentence.K0;
                case 1: return sentence.K1;
                case 2: return sentence.K2;
                case 3: return sentence.K3;
                default: return sentence.K4;
            }
        }

        static bool IsLivingTeammate(EntityManager em, int networkId, TeamId team)
        {
            if (networkId <= 0)
                return false;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                return !states[i].IsDead &&
                       !states[i].AwaitingTeamSelection &&
                       states[i].Team == team;
            }

            return false;
        }

        static bool Eq(string label, string expected) =>
            string.Equals(label, expected, StringComparison.OrdinalIgnoreCase);

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

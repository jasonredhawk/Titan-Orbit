using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client hull identity for an AI ship, and the server write that copies a
    /// commander's material onto <see cref="ShipAccentColors"/>. The bot's own
    /// hull stays on <see cref="ShipState"/>. Stats stay there too.
    /// </summary>
    public static class BotShipPresentation
    {
        /// <summary>
        /// Level, branch, and family the client should draw. Commander material
        /// orders leave <see cref="BotShipLook"/> inactive, so this stays the
        /// bot's own hull. An active look would replace that hull.
        /// </summary>
        public static void Read(
            EntityManager em,
            Entity ship,
            in ShipState state,
            out int level,
            out int branch,
            out int familyIndex,
            out bool mega,
            out int megaCatalog)
        {
            level = math.max(1, state.ShipLevel);
            branch = math.max(0, state.BranchIndex);
            familyIndex = state.ShipFamilyConfigIndex;
            mega = false;
            megaCatalog = 0;
            if (!em.Exists(ship) || !em.HasComponent<BotShipLook>(ship))
                return;

            BotShipLook look = em.GetComponentData<BotShipLook>(ship);
            if (look.Active == 0)
                return;

            level = math.max(1, look.Level);
            branch = math.max(0, look.BranchIndex);
            familyIndex = look.FamilyIndex;
            mega = look.Mega != 0;
            megaCatalog = look.MegaCatalogIndex;
        }

        /// <summary>
        /// Chassis id for the proxy / Entities Graphics mesh. MEGA commanders use the
        /// catalog id. Everyone else uses the family ladder slot.
        /// </summary>
        public static bool TryResolveChassis(
            EntityManager em,
            Entity ship,
            in ShipState state,
            out string chassisId,
            out int level,
            out int branch)
        {
            Read(em, ship, state, out level, out branch, out int family, out bool mega, out int megaCatalog);
            if (mega)
            {
                chassisId = MegaShipCatalog.FormatChassisId(megaCatalog);
                return true;
            }

            bool lookActive = em.Exists(ship)
                              && em.HasComponent<BotShipLook>(ship)
                              && em.GetComponentData<BotShipLook>(ship).Active != 0;
            if (!lookActive
                && em.Exists(ship)
                && em.HasComponent<MegaShipState>(ship)
                && em.GetComponentData<MegaShipState>(ship).IsMega)
            {
                chassisId = MegaShipCatalog.FormatChassisId(
                    em.GetComponentData<MegaShipState>(ship).CatalogIndex);
                return true;
            }

            return ShipStatApplyLogic.TryResolveChassisId(
                state.Team,
                level,
                branch,
                out chassisId,
                allowFallback: true,
                shipFamilyConfigIndex: family);
        }

        /// <summary>
        /// Copies the commanding player's material while their order includes this bot.
        /// Family, level, branch, and MEGA stay on the bot, so the hull prefab is not
        /// replaced. Clears the copied material when the order ends.
        /// </summary>
        public static void ApplyCommanderSkin(
            EntityManager em,
            Entity bot,
            int botNetworkId,
            in BotTeamOrder order,
            float now,
            NativeArray<Entity> ships,
            NativeArray<GhostOwner> owners)
        {
            bool want = order.IsActive(now)
                        && order.Includes(botNetworkId)
                        && order.CommanderNetworkId > 0
                        && !BotShipIds.IsBot(order.CommanderNetworkId);
            ShipAccentColors accents = default;
            bool haveMaterial = want
                && TryReadCommanderMaterial(em, ships, owners, order.CommanderNetworkId, out accents);

            // [TITAN-ORBIT] An active BotShipLook swaps the hull prefab. Material
            // orders keep it cleared and write ShipAccentColors only.
            WriteLook(em, bot, default);
            WriteAccents(em, bot, haveMaterial ? accents : default);
        }

        /// <summary>Drops a copied material. The bot's own hull stays in place.</summary>
        public static void ClearCommanderSkin(EntityManager em, Entity bot)
        {
            WriteLook(em, bot, default);
            WriteAccents(em, bot, default);
        }

        static bool TryReadCommanderMaterial(
            EntityManager em,
            NativeArray<Entity> ships,
            NativeArray<GhostOwner> owners,
            int commanderNetworkId,
            out ShipAccentColors accents)
        {
            accents = default;
            int count = math.min(ships.Length, owners.Length);
            for (int i = 0; i < count; i++)
            {
                if (owners[i].NetworkId != commanderNetworkId || !em.Exists(ships[i]))
                    continue;
                if (!em.HasComponent<ShipAccentColors>(ships[i]))
                    return false;

                accents = em.GetComponentData<ShipAccentColors>(ships[i]);
                return true;
            }

            return false;
        }

        static void WriteLook(EntityManager em, Entity bot, in BotShipLook desired)
        {
            if (!em.Exists(bot))
                return;
            if (!em.HasComponent<BotShipLook>(bot))
            {
                if (desired.Active == 0)
                    return;
                em.AddComponentData(bot, desired);
                return;
            }

            BotShipLook current = em.GetComponentData<BotShipLook>(bot);
            if (current.Active == desired.Active
                && current.FamilyIndex == desired.FamilyIndex
                && current.Level == desired.Level
                && current.BranchIndex == desired.BranchIndex
                && current.Mega == desired.Mega
                && current.MegaCatalogIndex == desired.MegaCatalogIndex)
                return;

            em.SetComponentData(bot, desired);
        }

        static void WriteAccents(EntityManager em, Entity bot, in ShipAccentColors desired)
        {
            if (!em.Exists(bot) || !em.HasComponent<ShipAccentColors>(bot))
                return;

            ShipAccentColors current = em.GetComponentData<ShipAccentColors>(bot);
            if (current.CacheKey == desired.CacheKey)
                return;

            em.SetComponentData(bot, desired);
        }
    }
}

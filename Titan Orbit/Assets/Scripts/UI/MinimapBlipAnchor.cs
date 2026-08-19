using TitanOrbit.Core;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Minimap icon category for ECS-synced world blips.</summary>
    public enum MinimapBlipKind
    {
        // --- World entity categories for minimap icons ---
        Ship,
        Planet,
        HomePlanet,
        Asteroid,
        GemMoon,
    }

    /// <summary>
    /// Hidden world-space anchor used as a blip key for ECS entities on the minimap.
    /// <see cref="MinimapEcsEntitySync"/> creates/updates these from ghost state; blip
    /// renderers read Kind, Team, IsMega, chassis, cargo, and match stats for icon shape and badges.
    /// [HYBRID] bridge between ECS entities and UGUI/minimap presentation — does not drive simulation.
    /// </summary>
    public sealed class MinimapBlipAnchor : MonoBehaviour
    {
        // --- Blip classification ---
        /// <summary>Which minimap sprite/layout to use.</summary>
        public MinimapBlipKind Kind;

        /// <summary>Team tint for ships and owned planets.</summary>
        public TeamId Team;

        /// <summary>True when ship ghost is in death state.</summary>
        public bool IsDead;

        /// <summary>True for the local player's ship blip.</summary>
        public bool IsLocalPlayer;

        /// <summary>
        /// True while this hull is a purchased MEGA (from <c>MegaShipState.IsMega</c>).
        /// Minimap draws a triangle instead of the regular Cross so every client can
        /// spot capital ships without opening a nameplate.
        /// </summary>
        public bool IsMega;

        /// <summary>True when player has not picked a team yet.</summary>
        public bool AwaitingTeamSelection;

        // --- Ship identity (for silhouette lookup) ---
        /// <summary>[TITAN-ORBIT] Upgrade ladder level from <c>ShipState.ShipLevel</c>.</summary>
        public int ShipLevel;

        /// <summary>[TITAN-ORBIT] Branch within the level from <c>ShipState.BranchIndex</c>.</summary>
        public int BranchIndex;

        /// <summary>
        /// [TITAN-ORBIT] Index into <c>PlanetShipFamilyConfig.families</c>.
        /// Ships copy <c>ShipState.ShipFamilyConfigIndex</c>; planets copy
        /// <c>PlanetState.ShipFamilyConfigIndex</c> so the minimap hover tip can name the family.
        /// </summary>
        public byte ShipFamilyConfigIndex;

        // --- Ship vitals / cargo (live hold — not match scores) ---
        /// <summary>[TITAN-ORBIT] Current hull points (<c>ShipState.Health</c>).</summary>
        public float Health;

        /// <summary>[TITAN-ORBIT] Max hull (<c>ShipState.MaxHealth</c>).</summary>
        public float MaxHealth;

        /// <summary>[TITAN-ORBIT] Gems currently in cargo (<c>ShipState.CurrentGems</c>).</summary>
        public float CurrentGems;

        /// <summary>[TITAN-ORBIT] Max gem cargo (<c>ShipState.GemCapacity</c>).</summary>
        public float GemCapacity;

        /// <summary>[TITAN-ORBIT] Troops currently aboard (<c>ShipState.CurrentPeople</c>).</summary>
        public int CurrentPeople;

        /// <summary>[TITAN-ORBIT] Troop cap (<c>ShipState.PeopleCapacity</c>).</summary>
        public int PeopleCapacity;

        // --- Match-long scores (ghosted ShipMatchStats) ---
        /// <summary>[TITAN-ORBIT] Cumulative kills this match.</summary>
        public int Kills;

        /// <summary>[TITAN-ORBIT] Cumulative gems deposited this match.</summary>
        public int GemsDeposited;

        /// <summary>[TITAN-ORBIT] Cumulative troops delivered this match.</summary>
        public int PeopleDelivered;

        /// <summary>
        /// [TITAN-ORBIT] Yaw in degrees around world Y from <c>LocalTransform.Rotation</c>,
        /// used to rotate the ship silhouette so facing is readable on the minimap.
        /// </summary>
        public float YawDegrees;

        /// <summary>
        /// [NETCODE] Owner <c>GhostOwner.NetworkId</c> — stable tie-break for top-of-team badges.
        /// </summary>
        public int OwnerNetworkId;

        // --- Planet / body stats for label and scale ---
        /// <summary>Planet level for ring/label display (also = defense slot count when owned).</summary>
        public int PlanetLevel;

        /// <summary>
        /// [TITAN-ORBIT] Bit <c>i</c> set when planetary-defense slot <c>i</c> has an active turret
        /// (<c>TurretLevel &gt; 0</c>). Minimap draws a filled dot for set bits and a ring for empty pads.
        /// </summary>
        public byte DefenseTurretBuiltMask;

        /// <summary>Rounded population for planet blip label.</summary>
        public int Population;

        /// <summary>World body radius scale for blip size.</summary>
        public float BodySize;

        /// <summary>Stable planet id for connection lines UI.</summary>
        public int PlanetId;

        /// <summary>True when asteroid is depleted.</summary>
        public bool IsDestroyed;

        /// <summary>Gem moon shield visual radius for blip scale.</summary>
        public float MoonVisualSize;

        /// <summary>True for team home world planets.</summary>
        public bool IsHomePlanet;

        /// <summary>ECS entity this blip tracks — used for add/remove sync.</summary>
        public Entity SourceEntity;
    }

}

using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Editor authoring hub: lists every <see cref="ShipFamilyDefinition"/> so one inspector
    /// button can recalculate part profiles and resort every upgrade tree.
    /// The resort order is total gem cost (prefab scale, then Extra Level, then family bonuses),
    /// the same order as TitanOrbit → Ship Families → Resort All Upgrade Trees.
    /// Not used at runtime — combat reads baked family <c>components</c> / <c>upgradeTree</c>.
    /// </summary>
    [CreateAssetMenu(fileName = "ShipFamilyDefinitionCatalog", menuName = "Titan Orbit/Ship Family Definition Catalog")]
    public class ShipFamilyDefinitionCatalog : ScriptableObject
    {
        [Tooltip("Profile set used when a listed family has no Part Calc Profile Set assigned.")]
        public ShipFamilyPartCalcProfileSet partCalcProfileSet;

        [Tooltip("All ship family definition assets to batch-update.")]
        public List<ShipFamilyDefinition> families = new List<ShipFamilyDefinition>();
    }
}

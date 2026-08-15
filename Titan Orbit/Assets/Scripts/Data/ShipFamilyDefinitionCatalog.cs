using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Editor authoring hub: lists every <see cref="ShipFamilyDefinition"/> so Recalculate +
    /// Resort can run on all families from one inspector button.
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

using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Marks a remapped hull child so presentation can resolve the purchased
    /// part's family (clones keep the host slot name). Used by
    /// <see cref="TitanOrbit.Game.ShipPropulsionVisualApplier"/> for thruster flames.
    /// Restored originals have no marker.
    /// </summary>
    public sealed class ShipPartVisualSource : MonoBehaviour
    {
        /// <summary>Source <see cref="ShipFamilyDefinition.familyId"/> (e.g. CosmicShark).</summary>
        public string sourceFamilyId;
    }
}

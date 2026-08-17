using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Stub for the legacy prefab-derived card pool builder removed during the ECS rewrite.
    /// </summary>
    public static class CardDeckFromPrefabStatsGenerator
    {
        /// <summary>Shows a dialog — prefab-derived card pool is not restored yet.</summary>
        public static void BuildPrefabDerivedDeckForFamily(ShipFamilyDefinition def, bool interactiveDialogs = true)
        {
            if (!interactiveDialogs)
                return;
            UniqueCardDeckGenerator.RebuildFamilyDeck(def, interactiveDialogs);
        }
    }
}

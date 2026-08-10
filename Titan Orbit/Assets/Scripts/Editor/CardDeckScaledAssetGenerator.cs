using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Stub for the legacy scaled card-deck builder removed during the ECS rewrite.
    /// Create empty deck via ShipFamilyDefinition inspector instead until full generator returns.
    /// </summary>
    public static class CardDeckScaledAssetGenerator
    {
        /// <summary>Shows a dialog — full scaled deck pipeline is not restored yet.</summary>
        public static void BuildScaledDeckForFamily(ShipFamilyDefinition def, bool interactiveDialogs = true)
        {
            if (!interactiveDialogs)
                return;
            EditorUtility.DisplayDialog(
                "Create New Card Deck",
                "Scaled card-deck generation is not restored yet.\n\n" +
                "Use \"Create empty Card Deck Definition & assign\" below, " +
                "or generate a procedural deck at runtime via GetUpgradeCards().",
                "OK");
        }
    }
}

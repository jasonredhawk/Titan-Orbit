using System.Collections.Generic;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Composes a ship's visual hierarchy from a chassis definition and a set of equipped cards.
    /// Assumes it is attached to the same GameObject as Starship and that Starship's BankPivot/Prefab
    /// structure is already created.
    /// </summary>
    [RequireComponent(typeof(Starship))]
    public class ShipVisualComposer : MonoBehaviour
    {
        [Header("Data")]
        [SerializeField] private ShipPartCatalog partCatalog;

        private Starship starship;

        private void Awake()
        {
            starship = GetComponent<Starship>();
        }

        /// <summary>
        /// Entry point from Starship: rebuild the visual children under the Prefab container based
        /// on the current chassis/baseShipData and whatever system you later use to expose cards.
        /// This is intentionally conservative for now: it only strips non-visual components from
        /// the imported prefab and lets Starship.ApplyShipVisual handle swapping the base hull.
        /// </summary>
        public void RebuildVisuals()
        {
            if (starship == null) return;

            // Get the prefab container under BankPivot where the base hull was loaded.
            Transform root = starship.GetCardVisualRoot();
            if (root == null) return;

            // Remove any existing card-driven parts from previous rebuilds.
            // We tag them by name prefix "CardPart_".
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child == null) continue;
                if (child.name.StartsWith("CardPart_"))
                    Destroy(child.gameObject);
            }

            if (partCatalog == null) return;

            var cards = starship.EquippedCards;
            if (cards == null) return;

            // For now, simply attach each card's modulePrefab once at the root with neutral transform.
            // Later, presets from the USC component map will drive exact positions.
            foreach (var card in cards)
            {
                if (card == null || card.partPrefab == null) continue;

                ShipPartDefinition partDef = !string.IsNullOrEmpty(card.componentKey)
                    ? partCatalog.GetPart(card.componentKey)
                    : null;

                GameObject prefabToUse = partDef != null && partDef.modulePrefab != null
                    ? partDef.modulePrefab
                    : card.partPrefab;

                if (prefabToUse == null) continue;

                GameObject instance = Instantiate(prefabToUse, root);
                instance.name = "CardPart_" + (string.IsNullOrEmpty(card.componentKey) ? card.cardId : card.componentKey);
                Transform t = instance.transform;
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;

                // Strip colliders/rigidbodies/behaviours from the card part as well to keep it visual-only.
                Starship.StripNonVisualComponents(t, null);
            }
        }
    }
}


using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    // --- Type members ---
    /// <summary>
    /// Registry of authored <see cref="CardData"/> assets for one ship family deck. Assign to
    /// <see cref="Systems.CardShopSystem"/> or <see cref="ShipFamilyDefinition"/> so shop spins
    /// and orbit-station stores draw from designer data instead of
    /// <see cref="CardDeckRuntimeDefaults"/> procedural cards. ScriptableObject — order in
    /// <see cref="cards"/> does not affect gameplay; shop uses rarity weights per card.
    /// </summary>
    [CreateAssetMenu(fileName = "NewCardDeck", menuName = "Titan Orbit/Card Deck")]
    public class CardDeckDefinition : ScriptableObject
    {
        /// <summary>Optional editor label (e.g. AstroEagleScaled) — not used in sim.</summary>
        [Tooltip("Optional label for editors (e.g. AstroEagleScaled).")]
        public string deckId;

        /// <summary>All cards in this deck. [UNITY] References to CardData ScriptableObject assets.</summary>
        [Tooltip("All cards in this deck. Order does not affect gameplay.")]
        public List<CardData> cards = new List<CardData>();
    }
}

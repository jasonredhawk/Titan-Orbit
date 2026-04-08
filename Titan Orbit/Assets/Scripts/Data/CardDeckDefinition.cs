using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Registry of <see cref="CardData"/> assets (one deck). Assign to <c>CardShopSystem</c> so spins and stores use authored data instead of runtime defaults.
    /// </summary>
    [CreateAssetMenu(fileName = "NewCardDeck", menuName = "Titan Orbit/Card Deck")]
    public class CardDeckDefinition : ScriptableObject
    {
        [Tooltip("Optional label for editors (e.g. AstroEagleScaled).")]
        public string deckId;

        [Tooltip("All cards in this deck. Order does not affect gameplay.")]
        public List<CardData> cards = new List<CardData>();
    }
}

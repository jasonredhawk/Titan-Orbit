using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// One authored card effect row. Magnitude is a multiplier unless the kind name ends in Add
    /// (those are flat adds). Zero or negative multipliers are treated as 1 at apply time.
    /// </summary>
    [Serializable]
    public struct CardEffect
    {
        /// <summary>Which overlay this row applies.</summary>
        public CardEffectKind kind;

        /// <summary>
        /// Multiplier (1.15 = +15%) or flat add, depending on <see cref="kind"/>.
        /// Designer-tunable on the CardData asset.
        /// </summary>
        public float magnitude;

        /// <summary>True when this row should change gameplay (kind set and magnitude not identity).</summary>
        public bool IsActive
        {
            get
            {
                if (kind == CardEffectKind.None)
                    return false;
                if (IsAddKind(kind))
                    return Mathf.Abs(magnitude) > 0.0001f;
                return magnitude > 0.0001f && Mathf.Abs(magnitude - 1f) > 0.0001f;
            }
        }

        /// <summary>True for flat-add kinds (radius, pack size, regen) rather than multipliers.</summary>
        public static bool IsAddKind(CardEffectKind kind)
        {
            return kind == CardEffectKind.GemPickupRadiusAdd
                || kind == CardEffectKind.RocketPackSizeAdd
                || kind == CardEffectKind.MinePackSizeAdd
                || kind == CardEffectKind.DockedHullRegenAdd
                || kind == CardEffectKind.PeopleUnloadChunkAdd;
        }
    }
}

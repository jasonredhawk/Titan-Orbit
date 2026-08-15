using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable gem tractor beam and cargo pickup balance.
    /// Edit this asset in the Inspector — no code changes needed for range/power, multi-beam
    /// stacking, sticky locks, or wing/hull pickup size.
    /// Sole asset: <c>Assets/Resources/TractorBeamSettings.asset</c>
    /// (Create via Assets → Create → Titan Orbit → Tractor Beam Settings, or
    /// TitanOrbit → Create Tractor Beam Settings Asset).
    /// Loaded at play by <see cref="Game.TractorBeamSettingsLoader"/> via <c>Resources.Load</c>
    /// so Editor and player builds share one file — no Data/ duplicate.
    /// <para>
    /// Pipeline: ship wings search for gems using per-wing stats × <see cref="RangeMultiplier"/> /
    /// <see cref="PowerMultiplier"/>. Matching uses <c>GemTractorBeamAssignment</c> (sticky primary,
    /// unique gems first, then optional assists capped by <see cref="MaxCooperatingBeams"/>).
    /// Gems absorb into cargo when inside the wing (and optional hull) pickup radii below —
    /// tractor pull only moves gems into that zone; flying over gems uses the same pickup math.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "TractorBeamSettings",
        menuName = "Titan Orbit/Tractor Beam Settings",
        order = 55)]
    public class TractorBeamSettings : ScriptableObject
    {
        // -------------------------------------------------------------------------
        // Multi-beam sticky / stacking
        // -------------------------------------------------------------------------

        [Header("Sticky locks")]
        [Tooltip(
            "ON (recommended): only the first beam that claims a gem stays locked to it until " +
            "the gem leaves that wing's search range. Spare assist beams re-evaluate every tick " +
            "so they can jump to newly appeared gems. " +
            "OFF: every active wing↔gem pair stays sticky (old behavior — assists cling too).")]
        public bool PrimaryStickyOnly = true;

        [Header("Multi-beam pull (stacking)")]
        [Tooltip(
            "Max tractor beams that may pull the same gem at once. " +
            "1 = beams never pull together (each gem gets at most one beam). " +
            "2 / 5 / 10 = up to that many wings may stack on one gem when there are more beams " +
            "than free gems in range. Raise if large wing counts should pile onto sparse gems.")]
        [Min(1)]
        public int MaxCooperatingBeams = 8;

        [Tooltip(
            "When several beams pull one gem: the primary contributes 100% of its pull, and each " +
            "assist contributes this fraction of its own pull. Default 0.25 → 2 equal beams ≈ 125%, " +
            "3 ≈ 150% (not 200% / 300%). Ignored when MaxCooperatingBeams is 1.")]
        [Range(0f, 1f)]
        public float AssistPullScale = 0.25f;

        // -------------------------------------------------------------------------
        // Global multipliers on authored wing stats
        // -------------------------------------------------------------------------

        [Header("Global multipliers (on wing distance / power)")]
        [Tooltip(
            "Scales every wing's search radius after level / orbit bonuses. " +
            "1 = authored reach; 1.5 = 50% farther; 0.5 = half range.")]
        [Min(0.01f)]
        public float RangeMultiplier = 1f;

        [Tooltip(
            "Scales every wing's pull speed after gameplay clamp. " +
            "1 = authored power; 2 = twice as strong; 0.5 = half pull.")]
        [Min(0.01f)]
        public float PowerMultiplier = 1f;

        // -------------------------------------------------------------------------
        // Cargo pickup (absorb into ship) — separate from tractor search reach
        // -------------------------------------------------------------------------

        [Header("Cargo pickup zone (no tractor lock required)")]
        [Tooltip(
            "Instant cargo absorb radius at each wing tip (world units) — same idea as Hull Pickup Range, " +
            "but measured from the wing tip instead of the hull center. " +
            "Effective wing pickup = WingCollectRadius + max(gem.Size × GemSizeCollectFactor, gem.Size × 0.5). " +
            "Does NOT require a tractor beam connection: if a gem is inside this sphere " +
            "(fly a tip over it, or a beam finishes pulling it in), cargo consumes it immediately. " +
            "Tractor search reach is separate (wing distance × RangeMultiplier). " +
            "Default 0.65 (comfortable tip fly-over). Lower toward 0.25 for a tighter tractor destination.")]
        [Min(0.01f)]
        public float WingCollectRadius = 0.65f;

        [Tooltip(
            "Extra pickup radius from gem visual size, applied to both wing tips and hull. " +
            "Pickup also uses at least half of gem.Size so overlapping the visible gem counts. " +
            "Still no tractor lock required — size only widens the absorb sphere.")]
        [Min(0f)]
        public float GemSizeCollectFactor = 0.25f;

        [Tooltip(
            "Instant cargo absorb radius from the hull center (world units). " +
            "Same rule as Wing Collect Radius: no tractor lock needed — distance alone consumes. " +
            "Used when the ship has no wing buffers, and also when AlsoUseHullPickupWithWings is ON. " +
            "Legacy no-wing default 2.5.")]
        [Min(0.01f)]
        public float HullPickupRange = 2.5f;

        [Tooltip(
            "ON: ships with wings also absorb gems within HullPickupRange of the hull center " +
            "(in addition to wing-tip WingCollectRadius zones). Both zones are instant pickup — " +
            "no tractor lock required. " +
            "OFF: only wing-tip collect radii absorb (hull fly-over ignored); tip pickup still " +
            "works without a beam.")]
        public bool AlsoUseHullPickupWithWings = true;

        /// <summary>
        /// Keeps multipliers and radii sane after Inspector edits.
        /// Called from OnValidate and from the runtime loader before publishing to the cache.
        /// </summary>
        public void ClampValues()
        {
            // --- Sticky / stack ---
            MaxCooperatingBeams = Mathf.Max(1, MaxCooperatingBeams);
            AssistPullScale = Mathf.Clamp01(AssistPullScale);

            // --- Multipliers ---
            RangeMultiplier = Mathf.Max(0.01f, RangeMultiplier);
            PowerMultiplier = Mathf.Max(0.01f, PowerMultiplier);

            // --- Pickup ---
            WingCollectRadius = Mathf.Max(0.01f, WingCollectRadius);
            GemSizeCollectFactor = Mathf.Max(0f, GemSizeCollectFactor);
            HullPickupRange = Mathf.Max(0.01f, HullPickupRange);
        }

        /// <summary>[UNITY] Inspector edit → clamp so Max ≥ 1 and radii stay positive.</summary>
        void OnValidate() => ClampValues();

        /// <summary>
        /// Extra absorb padding from gem visual scale (<c>GemState.Size</c> / LocalTransform scale).
        /// Uses the larger of the designer factor and half the gem scale so overlapping the
        /// visible gem mesh counts as a touch — not only a hit on the exact center point.
        /// </summary>
        /// <param name="gemSize">Gem visual/collision size from <c>GemState.Size</c>.</param>
        /// <returns>World units added to wing-tip or hull-center pickup radius.</returns>
        public float ResolveGemSizePad(float gemSize)
        {
            float size = Mathf.Max(0f, gemSize);
            return Mathf.Max(size * GemSizeCollectFactor, size * 0.5f);
        }

        /// <summary>
        /// Effective wing-tip collect radius for one gem (base + size pad).
        /// Used by <c>GemPickupSystem</c> so tractor destination and fly-over scoop match.
        /// No tractor lock required — distance alone consumes.
        /// </summary>
        /// <param name="gemSize">Gem visual/collision size from <c>GemState.Size</c>.</param>
        /// <returns>Toroidal distance threshold from wing tip to gem center.</returns>
        public float ResolveWingCollectRadius(float gemSize)
        {
            ClampValues();
            return WingCollectRadius + ResolveGemSizePad(gemSize);
        }

        /// <summary>
        /// Effective hull-center collect radius for one gem (base + size pad).
        /// Same fly-over rule as <see cref="ResolveWingCollectRadius"/> — no tractor lock.
        /// </summary>
        /// <param name="gemSize">Gem visual/collision size from <c>GemState.Size</c>.</param>
        /// <returns>Toroidal distance threshold from hull center to gem center.</returns>
        public float ResolveHullPickupRange(float gemSize)
        {
            ClampValues();
            return HullPickupRange + ResolveGemSizePad(gemSize);
        }
    }
}

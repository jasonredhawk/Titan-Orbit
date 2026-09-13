namespace TitanOrbit.Services
{
    /// <summary>
    /// Outcome of one opt-in rewarded-video attempt.
    /// UI grants a gameplay reward only on <see cref="Completed"/>.
    /// <para>
    /// [TITAN-ORBIT] AppLixir WebGL and LevelPlay mobile both map into this enum so
    /// death / extra-slot buttons never talk to an SDK directly.
    /// </para>
    /// </summary>
    public enum TitanOrbitRewardedAdResult
    {
        /// <summary>Player watched the full video (or remove-ads / Editor simulate granted instantly).</summary>
        Completed = 0,

        /// <summary>Player skipped, closed early, or declined consent — no reward.</summary>
        Failed = 1,

        /// <summary>No fill, SDK missing, ad blocker, or this platform has no ad backend.</summary>
        Unavailable = 2,
    }
}

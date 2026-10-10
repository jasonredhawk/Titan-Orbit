using TMPro;
using UnityEngine;

namespace TitanOrbit.UI
{
    // --- Type members ---
    /// <summary>
    /// References sprites from the Shift — Complete Sci-Fi UI pack for spin-offer cards.
    /// Default instance: Resources/SpinCardShiftVisuals (optional inspector override on <see cref="OrbitStationUI"/>).
    /// The Rajdhani fields exist so player builds (WebGL) keep a reference to the SDF assets.
    /// Editor-only AssetDatabase loads are stripped from those builds.
    /// </summary>
    [CreateAssetMenu(fileName = "SpinCardShiftVisuals", menuName = "Titan Orbit/UI/Spin Card Shift Visuals")]
    public class SpinCardShiftVisuals : ScriptableObject
    {
        [Tooltip("Cut Frame Filled — outer card chrome (9-slice).")]
        public Sprite outerFrameSliced;
        [Tooltip("Background Basic — inner fill behind content.")]
        public Sprite innerPanelSliced;
        [Tooltip("Cut Frame 3px — icon frame.")]
        public Sprite iconDockSliced;
        [Tooltip("Same family as Main Button — Choose CTA.")]
        public Sprite chooseButtonSliced;
        [Tooltip("Cut Frame Glow 3px — thin accent under header.")]
        public Sprite accentLineSliced;
        [Tooltip("Background Glow — soft vignette behind inner panel.")]
        public Sprite innerGlowSliced;

        [Tooltip("Ability-details body face. Referenced here so WebGL includes the SDF without a Resources copy.")]
        public TMP_FontAsset rajdhaniSemiBold;

        [Tooltip("Bold weight the details caption and rich-text bold tags switch to. Same include reason as SemiBold.")]
        public TMP_FontAsset rajdhaniBold;
    }
}

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-side which mine pack will place next. HUD row clicks write this;
    /// <c>ShipInputBridge</c> copies it onto <see cref="ShipInput.SelectedMineSlot"/> each tick.
    /// Index is among mine HUD rows (not the raw equipment buffer).
    /// [TITAN-ORBIT] Separate from <see cref="RocketSlotSelection"/> so a mine pack never
    /// consumes a rocket charge. HUD UP/DOWN walks rockets then mines as one list.
    /// When <see cref="HudFocused"/> is true, ALT places the selected mine instead of firing
    /// a rocket. There is no separate mine key — only the focused pack activates.
    /// </summary>
    public static class MineSlotSelection
    {
        /// <summary>0-based HUD row. Clamped whenever the pack list changes.</summary>
        public static int SelectedIndex { get; private set; }

        /// <summary>
        /// True while the loadout caret is on a mine pack. <c>ShipInputBridge</c> routes ALT
        /// to PlaceMine so the focused row is the only thing that activates.
        /// </summary>
        public static bool HudFocused { get; private set; }

        /// <summary>HUD caret moved onto or off a mine pack in the unified loadout list.</summary>
        public static void SetHudFocused(bool focused) => HudFocused = focused;

        /// <summary>Moves the caret by <paramref name="delta"/> and wraps.</summary>
        public static void Cycle(int delta, int count)
        {
            if (count <= 0)
            {
                SelectedIndex = 0;
                return;
            }

            int next = SelectedIndex + delta;
            while (next < 0)
                next += count;
            SelectedIndex = next % count;
        }

        /// <summary>Jumps to a HUD row (click). No-op when the list is empty.</summary>
        public static void Select(int index, int count)
        {
            if (count <= 0)
            {
                SelectedIndex = 0;
                return;
            }

            if (index < 0)
                index = 0;
            if (index >= count)
                index = count - 1;
            SelectedIndex = index;
        }

        /// <summary>Keeps the caret valid after a purchase or consume.</summary>
        public static int Clamp(int count)
        {
            if (count <= 0)
            {
                SelectedIndex = 0;
                return 0;
            }

            if (SelectedIndex < 0)
                SelectedIndex = 0;
            if (SelectedIndex >= count)
                SelectedIndex = count - 1;
            return SelectedIndex;
        }
    }
}

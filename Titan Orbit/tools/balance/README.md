# Titan Orbit — game balance

## Primary UX: RebalanceGame asset

1. Menu **TitanOrbit → Balance → Open RebalanceGame Hub** (creates `Assets/Resources/RebalanceGame.asset` if needed).
2. **Auto-Find References** — links ProfileSet, ship families, AsteroidSettings, ramming, cargo mobility, map, etc.
3. Edit the **Balancing requests** list (natural-language goals).
4. **Export For Cursor** — prompt + asset inventory + last review → paste into Cursor for AI rebalance.
5. After assets change: **Apply Local Pipeline** (optional) → **Refresh Review**.
6. Read **Economy checks**, **Fleet aggregates**, and **Outliers** on the same Inspector — not an external spreadsheet.

Optional CSV copies still exist under this folder via the hub’s “Optional: Export CSV…” button or Silent menu items.

## Power-score cargo weighting

- Raw gem capacity still drives purchase cost (`2 × gemCap`).
- Power score / upgrade-tree bars use `gemCap / 10` and `peopleCap / 4` so cargo does not drown firepower.

See `ShipFamilyPowerScoreBreakdown.GemCapPowerScoreDivisor` / `PeopleCapPowerScoreDivisor`.

# Balance playtest checklist (Editor)

After **TitanOrbit → Balance → Apply Seeds…** and exporting reports:

1. Open a Local host / MPPM session with one ship family (e.g. AstroEagle).
2. Spawn **L1** chassis: mine a mid-size asteroid; note time-to-kill vs ~8–12s target.
3. Fill gem cargo; dock moon; confirm deposit feels like ~1–2 trips to afford next chassis (`2 × gemCap`).
4. Buy one bottom-bar attribute upgrade; cost should be `shipLevel × 5`.
5. Spawn / upgrade to **L3**; check people capacity near ~14 median target.
6. With **two** L3 ships, attempt to capture a **level-3** planet (or home-sized body): expect roughly 4–6 full cargo unload cycles.
7. Fire weapons until energy empties; confirm regen is slower than sustained fire (burst, not infinite laser).
8. Spot-check an outlier from `ShipOutliers_Summary.md` (e.g. `AstroEagle_Hippo`): flagged mainly as cargo freak / energy at high tier — propulsion count is OK after move-seed bump; structural wing mapping still optional follow-up.
9. Confirm Economy report Flags = OK (`tools/balance/EconomyCrossCheck_Report.md`).
10. Before Windows player build: `powershell -File tools/verify-join-crash-gates.ps1` from repo root (exit 0).

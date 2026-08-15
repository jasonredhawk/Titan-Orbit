# Join architecture (historical)

This file is **archive**, not an agent hard stop. Live-match late-join is required:
seed-hydrate the static asteroid belt, occupancy catch-up for destroyed/respawned rocks,
then Unity NetCode GhostSpawn Instantiates of ships / planets / nearby gems.

Cursor join-crash `.mdc` rules were deleted so they cannot veto standard Instantiates.

Optional diagnostic (not a ship gate):

```powershell
powershell -File tools/verify-join-crash-gates.ps1
```

Current soak: `JOIN-WORLD-READY.md` in this folder.

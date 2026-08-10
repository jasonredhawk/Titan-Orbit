# Instruction reference captures

Press **F8** or **F9** in Play Mode after Join Team (while flying) to run
`InstructionReferenceCaptureSession`. Click the **Game** view first so it has keyboard focus.

During each screenshot the status banner and most gameplay HUD are hidden so plates stay clean.

## What you get

Each session creates a timestamped folder:

`Titan Orbit/Captures/InstructionRefs/<yyyyMMdd_HHmmss>/`

| Contents | Maps to |
|---|---|
| `01_objective_full_map.png` | Expanded minimap (all planets + territory triangles) → **objective** |
| `02_objective_territory_world.png` | World pullback with territory → **objective** |
| `03–05_planet_*.png` | Distinct in-game planet surfaces → **planet_ships** |
| `06_local_ship.png` | Local hull → **planet_ships** |
| `07_asteroid_field.png` | Simple asteroid field → **mining** |
| `08_red_gems.png` | Gem cluster (prefer red) → **mining** |
| `09_bonus_people_transport.png` | People transport orbs if nearby → **transport** |
| `guided_transport.png` | Yellow people transports (not turrets) → **transport** |
| `guided_mining_red_gems.png` | Asteroids + red gems → **mining** |
| `guided_orbit_station.png` | Moon dock / upgrades UI → **upgrades** |
| `guided_planet_ships_a/b.png` | More distinct planets → **planet_ships** |
| `ship_ref_*.png` | Cross-family catalog thumbs → **planet_ships** |
| `manifest.json` | Maps each file → instruction card key |
| `NEXT_STEPS.txt` | Handoff note for rebuilding loading-screen art |

## Controls

| Key | Action |
|---|---|
| **F8** or **F9** | Start session (idle) / confirm guided plate |
| **Esc** or **Shift+F8** | Cancel (partial folder kept) |

Join settle / ship Instantiates block start
(`ClientJoinSettleCache.ShouldSkipShipEntityQueries`).

## Rebuild InstructionScreens (follow-up)

After a session finishes, tell the Cursor agent:

> Rebuild the five `Assets/Resources/InstructionScreens/instruction_*.png`
> cards using the PNGs in `Captures/InstructionRefs/<session>/` as
> `reference_image_paths`. Keep filenames and ~1536×1024 (3:2) aspect.
> Cool game images only — no extra text on the cards.

Card keys in the manifest: `objective`, `transport`, `mining`, `upgrades`, `planet_ships`.

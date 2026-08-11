# Titan Orbit — RebalanceGame Cursor prompt

You are rebalancing Titan Orbit ScriptableObject assets from designer requests.
Update the linked assets (ProfileSet, ShipFamilyDefinitions, AsteroidSettings, etc.).
Do **not** invent a parallel CSV pipeline — after changes, the designer clicks
**Refresh Review** on the RebalanceGame asset to see outliers / aggregates in the Inspector.

## Session notes
2–5 teams × ~20 players, domination matches ~0.5–2 hours. Capture all planets to win. Balance ship levels vs planet levels vs turrets.

## GameBalanceTargets (code anchors)
# GameBalanceTargets

- Session: 2–5 teams × 20 players, ~30–120 min domination
- Capture: 3 ships × 5 batches; home pop≈129 → L3 peopleCap≈8.6 (L1≈7.2)
- Cargo parts assumption: 1 cockpit + 2 wings (median cargo≈3) → people/part V1=1.70 (capture-tuned), gems/part V1≈20.00 (gemCap target÷2 L1 cargo parts)
- Mid asteroid Size 35: TTK 8–12s (ideal 10s); gem fill loop 45–90s
- Target L1 gemCap≈40; chassis cost = 2×gemCap (≈2 cargo trips)
- Energy: regen fraction of sustained drain = 0.35; insolvency below 0.20
- Attribute upgrade cost = shipLevel × 5


## Power-score cargo weighting
- Gem power contribution = rawGemCap / 10 (purchase cost still uses raw × 2).
- People power contribution = rawPeopleCap / 4.

## Balancing requests (enabled, by priority)

### 1. Capture with ~3 ships (priority 95)
About 3 ships with average people capacity for their level should be able to capture an equal-level average-sized planet (full population) without needing a 10-ship zerg. Coordinate with planet population caps and unload batch sizes.

### 2. Ship's health vs energy cap/regen vs firing power/rate ratio and sustained fire. (priority 95)
Ships should have enough avg energy capacity to sustain avg firing of bullets so that we get about 3 seconds of full firing, and energy regen balance it to 30% of how much that weapon consumes. This is mainly between the engine and weapon components to compliment each other. We need to also balance fire power to ship health. There should be enough avg health cap to sustain 3 seconds of avg dps.

### 3. Ship feel — fast & nimble (priority 90)
Ships should feel fast and nimble. Acceleration should make most average multi-engine/thruster fairly quick.

### 4. Tier progression (priority 85)
Low-level ships should be faster / more agile but less powerful than higher-level ships. Higher tiers trade some agility for firepower, cargo, and survivability.

### 5. Asteroid combat loop (priority 80)
Mid asteroids should take roughly 8–12 seconds for a median L1 DPS ship to kill. Gem capacity vs mining rate should support active loops, not instant full cargo.

### 6. Energy complementarity (priority 75)
Engines are the energy source; weapons spend energy to fire; overdrive bursts spend energy for speed. Sustained fire must drain the pool (regen below firePower×fireRate). Cards are sidegrades, not a second engine stack.

### 7. Planet regen pace (priority 70)
Planet population regeneration must not be too fast — freshly captured empty planets should stay vulnerable long enough for counter-play (current FullRefillSeconds ≈ 120s is a baseline).

### 8. Power score cargo weight (priority 65)
Gem capacity must not dominate ship power score vs firepower — keep gem power contribution ≈ rawGems/10, people ≈ rawPeople/4, so upgrade trees sort by combat+mobility meaningfully.

## Linked assets
- **PartCalcProfileSet**: `Assets/Resources/ShipFamilyPartCalcProfileSet.asset`
- **PlanetShipFamilyConfig**: `Assets/Resources/PlanetShipFamilyConfig.asset`
- **AsteroidSettings**: `Assets/Resources/AsteroidSettings.asset`
- **MapGenerationSettings**: `Assets/Resources/MapGenerationSettings.asset`
- **GemExplosionSettings**: `Assets/Resources/GemExplosionSettings.asset`
- **ShipRammingSettings**: `Assets/Resources/ShipRammingSettings.asset`
- **ShipCargoMobilitySettings**: `Assets/Resources/ShipCargoMobilitySettings.asset`
- **TractorBeamSettings**: `Assets/Resources/TractorBeamSettings.asset`
- **PlanetaryDefenseConfig**: `Assets/Resources/PlanetaryDefenseConfig.asset`
- **UpgradeTree**: `Assets/Resources/UpgradeTree.asset`

### Ship families (12)
- **AstroEagle**: `Assets/Prefabs/Ships/AstroEagle/AstroEagleShipFamily.asset`
- **CosmicShark**: `Assets/Prefabs/Ships/CosmicShark/CosmicSharkShipFamily.asset`
- **ForceBadger**: `Assets/Prefabs/Ships/ForceBadger/ForceBadgerShipFamily.asset`
- **GalaxyRaptor**: `Assets/Prefabs/Ships/GalaxyRaptor/GalaxyRaptorShipFamily.asset`
- **HyperFalcon**: `Assets/Prefabs/Ships/HyperFalcon/HyperFalconShipFamily.asset`
- **LightFox**: `Assets/Prefabs/Ships/LightFox/LightFoxShipFamily.asset`
- **MeteorMantis**: `Assets/Prefabs/Ships/MeteorMantis/MeteorMantisShipFamily.asset`
- **NightAye**: `Assets/Prefabs/Ships/NightAye/NightAyeShipFamily.asset`
- **ProtonLegacy**: `Assets/Prefabs/Ships/ProtonLegacy/ProtonLegacyShipFamily.asset`
- **SpaceExcalibur**: `Assets/Prefabs/Ships/SpaceExcalibur/SpaceExcaliburShipFamily.asset`
- **StarForce**: `Assets/Prefabs/Ships/StarForce/StarForceShipFamily.asset`
- **StriderOx**: `Assets/Prefabs/Ships/StriderOx/StriderOxShipFamily.asset`

## Last review snapshot (may be stale — refresh after edits)
# GameBalanceTargets

- Session: 2–5 teams × 20 players, ~30–120 min domination
- Capture: 3 ships × 5 batches; home pop≈129 → L3 peopleCap≈8.6 (L1≈7.2)
- Cargo parts assumption: 1 cockpit + 2 wings (median cargo≈3) → people/part V1=1.70 (capture-tuned), gems/part V1≈20.00 (gemCap target÷2 L1 cargo parts)
- Mid asteroid Size 35: TTK 8–12s (ideal 10s); gem fill loop 45–90s
- Target L1 gemCap≈40; chassis cost = 2×gemCap (≈2 cargo trips)
- Energy: regen fraction of sustained drain = 0.35; insolvency below 0.20
- Attribute upgrade cost = shipLevel × 5

Reviewed 241 chassis at 2026-08-11 15:21:40Z UTC.
Outliers: 175.

### Economy
- [PASS] mid_ttk_l1_sec = 10 (8-12)
- [PASS] capture_batches = 4.97 (4-6)
- [INFO] median_l3_people = 8.653 (8.6)
- [PASS] median_l1_gemCap = 38.026 (40)
- [PASS] median_wings = 2 (2)
- [INFO] health_per_size = 2.571 (AsteroidSettings)
- [INFO] gem_power_weight = raw/ 10 (power score only; purchase uses raw)


## Outliers (top 25 by severity)
- `36.24` SpaceExcalibur/SpaceExcalibur_08 L5 flags=energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats
- `23.62` SpaceExcalibur/SpaceExcalibur_16 L6 flags=energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats
- `22.41` SpaceExcalibur/SpaceExcalibur_05 L5 flags=energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats
- `21.18` SpaceExcalibur/SpaceExcalibur_04 L4 flags=energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats
- `15.29` SpaceExcalibur/SpaceExcalibur_07 L6 flags=propulsion_starvation|energy_insolvency|overgunned|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|structural_prefab
- `15.28` SpaceExcalibur/SpaceExcalibur_02 L4 flags=cargo_freak_people|cargo_freak_gems|energy_insolvency|overgunned fix=needs_wing_stat_nerf|needs_extra_engine_or_thruster_stats
- `15.14` SpaceExcalibur/SpaceExcalibur_06 L3 flags=energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats
- `14.62` SpaceExcalibur/SpaceExcalibur_13 L5 flags=cargo_freak_people|cargo_freak_gems|energy_insolvency|overgunned fix=needs_wing_stat_nerf|needs_extra_engine_or_thruster_stats
- `10.78` LightFox/LightFox_17 L5 flags=cargo_freak_people|cargo_freak_gems|energy_insolvency fix=needs_wing_stat_nerf|needs_extra_engine_or_thruster_stats
- `10.4` SpaceExcalibur/SpaceExcalibur_20 L6 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf
- `9.24` LightFox/LightFox_13 L5 flags=cargo_freak_people|cargo_freak_gems|energy_insolvency fix=needs_wing_stat_nerf|needs_extra_engine_or_thruster_stats
- `8.67` SpaceExcalibur/SpaceExcalibur_14 L4 flags=propulsion_starvation|energy_insolvency|overgunned|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|structural_prefab
- `8.04` SpaceExcalibur/SpaceExcalibur_15 L6 flags=energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats
- `7.55` GalaxyRaptor/GalaxyRaptor_04 L5 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab
- `7.48` SpaceExcalibur/SpaceExcalibur_18 L6 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|energy_insolvency|overgunned fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf
- `7.31` LightFox/LightFox_18 L6 flags=cargo_freak_people|cargo_freak_gems fix=needs_wing_stat_nerf
- `7.11` SpaceExcalibur/SpaceExcalibur_19 L5 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf
- `7.06` GalaxyRaptor/GalaxyRaptor_18 L5 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|energy_insolvency|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab
- `7.02` LightFox/LightFox_19 L6 flags=cargo_freak_people|cargo_freak_gems fix=needs_wing_stat_nerf
- `6.73` GalaxyRaptor/GalaxyRaptor_10 L5 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab
- `6.66` LightFox/LightFox_12 L5 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|energy_insolvency|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab
- `6.57` LightFox/LightFox_01 L1 flags=cargo_freak_people|cargo_freak_gems|weaponless fix=needs_wing_stat_nerf|structural_prefab
- `6.55` GalaxyRaptor/GalaxyRaptor_11 L6 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab
- `6.5` GalaxyRaptor/GalaxyRaptor_02 L3 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|energy_insolvency|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab
- `6.35` LightFox/LightFox_11 L5 flags=propulsion_starvation|cargo_freak_people|cargo_freak_gems|energy_insolvency|hippo_class_structure fix=needs_extra_engine_or_thruster_stats|needs_wing_stat_nerf|structural_prefab

## When done
1. Save all modified `.asset` / seed `.cs` files.
2. Designer opens RebalanceGame → **Apply Local Pipeline** (if seeds changed) → **Refresh Review**.
3. Confirm Economy checks PASS and outliers make sense.

# GameBalanceTargets

- Session: 2–5 teams × 20 players, ~30–120 min domination
- Capture: 2 ships × 5 batches; home pop≈129 → L3 peopleCap≈12.9 (L1≈10.8)
- Cargo parts assumption: 1 cockpit + 2 wings (median cargo≈3) → people/part V1=2.20 (capture-tuned), gems/part V1≈20.00 (gemCap target÷2 L1 cargo parts)
- Mid asteroid Size 35: TTK 8–12s (ideal 10s); gem fill loop 45–90s
- Target L1 gemCap≈40; chassis cost = 2×gemCap (≈2 cargo trips)
- Energy: regen fraction of sustained drain = 0.35; insolvency below 0.20
- Attribute upgrade cost = shipLevel × 5

# Economy cross-check

## Asteroids
- Settings: HealthPerSize=2.571, GemsPerSize=1 (asset loaded)
- Mid Size 35: HP=90.0, gems=35.0
- TTK @ median DPS: L1 10.0s (dps 9.0), L3 1.7s (dps 51.8), L6 0.3s (dps 324.0)
- Suggested HealthPerSize for ideal TTK: **2.571**

## Gems / costs
- Median L1 gemCap=38.0; chassis cost=2×gemCap (≈2 cargo trips); pure mining fill≈7.6s @ 5 g/s
- Attribute full bar one-stat L3 cost=45 gems; all 10 attrs≈450 gems
- Part price uses powerScore×1.75×(1+(L−1)×0.12) (ShipComponentStoreData) — compare to chassis in Inspector

## Capture / people
- Home pop (size 20, L3) = 129 (PlanetPopulationMath)
- Median L3 peopleCap=11.2; 2 ships cargo=22.4; batches to drain full home≈**5.76** (target 4–6)
- Target L3 peopleCap≈12.9

## Cards (procedural sidegrades)
- Kinetic dmg mult L3/r2=1.078; cargo gem add=21.0; suggested cost=36g
- Cards multiply combat / add flats; they are **not** a second Engine stack. Unused card fields (`miningRateAdd`, deposit speed mults) are still not applied in ShipStatApplyLogic.

## Flags
- OK — no hard failures (warnings may still appear above as WARN).

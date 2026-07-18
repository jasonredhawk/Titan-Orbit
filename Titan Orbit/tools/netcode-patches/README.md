# NetCode GhostSpawnSystem patch (Titan Orbit)

## Problem

On dedicated Relay late-join with hundreds of map ghosts, the Windows player hard-crashed from Instantiates floods (Burst LocalToWorld, then UnityPlayer) even after Instantiates-per-frame caps.

## Durable location

Embedded package (git-tracked via `file:com.unity.netcode`):

`Packages/com.unity.netcode/Runtime/Snapshot/GhostSpawnSystem.cs`

Canonical copy used by the Editor menu / pre-build guard:

`tools/netcode-patches/GhostSpawnSystem.cs`

## Current patch (`TO_GhostSpawn_v8_ghostMapSafe`)

1. **Safe snapshot copy** — rebuild buffers instead of resize-in-place (`TryCopySnapshotBufferSafe`).
2. **No Burst on `OnUpdate`** — managed Instantiates.
3. **1 Instantiates/frame** from delayed queues.
4. **Placeholders drain stock-style** (all GhostSpawnBuffer entries → placeholders same frame) so `SpawnedGhostEntityMap` stays valid. v7's placeholder defer+requeue caused `baseline for a ghost we do not have` + Windows crash.
5. **Predicted join path** — delayed Instantiates via placeholders (still registered same frame).
6. **Player.log** when creating ≥16 placeholders: `[TO_GhostSpawn] Created N placeholders…`

## Companion project systems (not in this file)

- Server: `TitanOrbitGhostSendTuneSystem` + `TitanOrbitGhostDistanceImportanceSystem` (stream map ghosts).
- Client: `TitanOrbitClientJoinTransformGateSystem` + `ClientJoinSettleState` (backlog-gated TransformSystemGroup).
- Hybrid: `EcsWorldVisualizer` rate-limits GO Instantiates while `ClientJoinSettleCache.Settling`.

## Re-apply / verify

Unity menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem patch**

After Windows client build, `Unity.NetCode.dll` must contain:

`TO_GhostSpawn_v8_ghostMapSafe`

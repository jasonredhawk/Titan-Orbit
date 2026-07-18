# NetCode GhostSpawnSystem patch (Titan Orbit)

## Problem

On dedicated Relay late-join, Burst-compiled `GhostSpawnSystem.OnUpdate` Instantiates the entire delayed map-ghost backlog in one frame and hard-crashed the Windows player (`Crash!!!` in `lib_burst_generated`).

An earlier Instantiates-per-frame rate limit stopped the crash but **broke NetCode spawn protocol** (Editor errors: `ObjectDisposedException`, `Ghost ID already been added`, `Received baseline for a ghost we do not have`).

## Current patch

File: `Library/PackageCache/com.unity.netcode@6437771c174a/Runtime/Snapshot/GhostSpawnSystem.cs`

1. **Remove `[BurstCompile]` from `OnUpdate`** — Instantiates run managed (stock drain timing preserved).
2. **Bounds-check `GhostType`** before indexing prefab / serializer buffers.

Canonical copy: `tools/netcode-patches/GhostSpawnSystem.cs`

## Re-apply after PackageCache restore

Unity menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem rate-limit patch**  
(menu name kept; patch is now “no Burst on OnUpdate + bounds checks”).

Then rebuild the **Windows client** (this system is client-only).

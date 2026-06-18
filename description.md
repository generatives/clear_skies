# Clear Skies — Project Description

## Overview

Clear Skies is a voxel engine and game written in C#, using **BepuPhysics2** for physics and **Silk.NET** for rendering. The game is set in a procedurally generated sky world where players build and pilot airships between floating islands.

---

## Engine

### Technology Stack
- Language: C#
- Physics: BepuPhysics2
- Rendering: Silk.NET (WebGPU)
- Architecture: ECS system with DefaultECS

### Voxel System
The engine supports two distinct categories of voxel space:

1. **Static voxels** — terrain and environment blocks anchored to the world grid, similar to Minecraft.
2. **Dynamic voxels** — grids of blocks that behave as rigid bodies within the physics simulation. These grids can move, rotate, and interact with the world and each other as single physical objects.

### Lighting
- Per-vertex lighting and shading, visually similar to Minecraft's style.
- The lighting system must support dynamic voxel grids: dynamic grids cast shadows on each other, and light sources (e.g. torches) attached to one dynamic grid illuminate voxels on other grids and on the static terrain.

### Multiplayer
The engine supports multiplayer. (Architecture details TBD — assumed client-server.)

### Modding API
The engine exposes a modding API allowing third-party modification of blocks, mechanics, and game logic.

---

## Game: Clear Skies

### Setting & Tone
A magical steampunk sky world. Players live on and travel between floating islands suspended in an infinite sky. The aesthetic blends arcane technology with Victorian-era engineering.

### World Generation
- Effectively infinite horizontal extent.
- Finite but large vertical range — tall enough to accommodate large islands, mountains, and multiple vertical layers of islands stacked at different altitudes.

### Airships
Airships are built by players using the **dynamic voxel system**. Each airship is a dynamic voxel grid that moves through the world as a physics object.

#### Airship Physics
- Realistic lift, weight, and fall mechanics.
- Uneven weight distribution causes tilt.
- A **Stabilizer block** counteracts tilt to keep the craft level.

#### Special Blocks
Airships are constructed from standard blocks plus functional blocks:

| Block | Function |
|---|---|
| Buoyant Block | Provides upward lift proportional to volume |
| Fan | Horizontal thrust |
| Jet | High-speed directional thrust |
| Magic Levitator | Arcane lift source |
| Battery | Stores power |
| Engine | Converts fuel into power |
| Stabilizer | Actively corrects tilt |
| Height Controller | Maintains or adjusts target altitude |
| Heading Controller | Maintains or adjusts direction |
| Speed Controller | Maintains or adjusts speed |
| Pilot Block | Player-operated control station for manual flight |

#### Power System
Functional blocks consume power. Power is supplied by batteries charged by engines (which burn fuel) or other generation sources (e.g. magical generators). Power availability limits which systems can operate simultaneously.

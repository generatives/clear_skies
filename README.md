# Clear Skies

## Repo
This repository contains a Voxel Game and Engine built for that game. This is a "minecraft clone" or "block game", with big textured blocks and simple, low poly graphics.

A relatively unique feature I am building into the game are "dynamic grids", grids of voxels which have physics bodies attached. There is also a ray traced lighting system which works well with those large, moving bodies.

This repo is almost entirely written by Claude Code. I do some tinkering and am closely involved in the development process, but I am not really writing code.

## Game
My goal for the game itself is to have the player build an "airship" with that dynamic grid system and use it to explore an open world full of large floating islands.

Some major gameplay themes:
- Building and extending the ship. Thinking about buoyancy and thrust, making sure things are well balanced.
- Operating the ship. The player will need to be involved in keeping the ship in the air an on course. This will be a "tactile", active process.
- Navigating the world. The player will be incentivized to both explore new areas and return to existing ones regularly. They will need to learn routes and find ways to keep track of your location.
- Surviving the environment. The player will need to deal with extreme temperatures and pressures as the explore the world. They will need to prepare for long voyages under difficult conditions.

I do not have all the details worked out but this is my general goal for the game.

## Technology
- C#
- Silk.NET
- WebGPU
- bepuphysics2
- DefaultECS

## Contributing
I am not really open to major PRs and features at the moment, there is barely anything here. But I am happy to hear suggestions, bug reports, or merge bug fixes if you have them!

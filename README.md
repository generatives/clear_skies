# Clear Skies

Click here for a quick demo video
[![Video of Person Flying Ship Through Floating Islands](https://img.youtube.com/vi/-UUtK8j4EU0/0.jpg)](https://youtu.be/-UUtK8j4EU0)

## Repo
This repository contains a Voxel Game and an Engine built for that game. This is a "minecraft clone" or "block game", with big textured blocks and simple, low poly graphics.

A relatively unique feature I am building into the game are "dynamic grids", grids of voxels which have physics bodies attached. There is also a ray traced lighting system which works well with those large, moving bodies. There is a WIP multiplayer system, targeting small groups of players (1-8, I haven't stress tested yet).

This repo is almost entirely written by Claude Code. I do some tinkering and am closely involved in the development process, but I am not really writing code.

## Game
My goal for the game itself is to have the player build an "airship" with that dynamic grid system and use it to explore an open world full of large floating islands.

Some major gameplay themes:
- Building and extending the ship. Thinking about buoyancy and thrust, making sure things are well balanced.
- Operating the ship. The player will need to be involved in keeping the ship in the air an on course. This will be a "tactile", active process.
- Navigating the world. The player will be incentivized to both explore new areas and return to existing ones regularly. They will need to learn routes and find ways to keep track of your location.
- Surviving the environment. The player will need to deal with extreme temperatures, pressures, and weather as they explore the world. They will need to prepare for long voyages under difficult conditions.

I do not have all the details worked out but this is my general goal for the game.

## Downloads
There are builds available under "Releases" here on GitHub, I automatically build for Windows x64, Windows ARM, and Linux x64. I really only play regularly on Windows x64 so those builds will be the most reliable.

The builds are all self contained so you don't need .NET installed to run them. There are "folder" builds and non-folder builds. The "folder" builds are an archive containing all the files required, including the executable. The non-folder builds are just a single executable which unpacks itself. I include the folder builds because unpacking executables are often blocked as viruses.

## Instructions
### Controls
The control scheme is pretty scattered right now since I am mostly just building baseline features.

There are 3 general control modes:
#### Free Fly Camera
Press "V" to toggle between a physical character and flying camera.
WASD to move and Mouse to look around
Space and Shift to move up and down
Q/E to change movement speed
Hold Ctrl to triple movement speed

#### Physical Character
Press "V" to toggle between a physical character and flying camera.
WASD to move and Mouse to look around
Space to jump
Ctrl to crouch, slows movement and prevents walking off edges
Shift to sprint

#### Pilot Mode
If you have selected an airship (recently spawned, walked on, or edited) you can press "F" to enter pilot mode. Doesn't work in multiplayer. You will be locked into an orbiting camera angle and can control the ship.

WASD to move laterally
Q/E to rotate
Shift/Space to move up and down
Mouse to orbit camera

### Building Ships
Press "G" to spawn a ship in front of you
You can build out a ship using blocks. You will need Fans/Thrusters for it to keep itself stable and move around. There is a simple physics simulation so they need to be places in the right directions and evenly throughout the ship for stability. You can place levers to control thrust, levers control thrust along the axis the lever moves. Steering wheels control Yaw.

Press "End" to lock and unlock a ship. Locked ships will not move, Unlocked ones are dynamic physics bodies. Press "Home" to reset the rotation on a ship, helpful if it looses stability.

If you don't want to place thrusters you can enable Free Propulsion in the Airship menu. You can also Save and Load Airships from there.

## Technology
- C#
- Silk.NET
- WebGPU
- bepuphysics2
- DefaultECS

## Contributing
I am not really open to major PRs and features at the moment, there is barely anything here. But I am happy to hear suggestions, bug reports, or merge bug fixes if you have them!

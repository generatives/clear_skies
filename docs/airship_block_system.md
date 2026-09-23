# Airship System Design
This is a description of the airship system, where players build airships out of a mix of static and active blocks, needind to balance weight, power usage, and material cost to build a functioning air ship.

## General Idea
The player builds their ship out of static blocks like wood or metal. All blocks have weight. In order to get the ship off the ground and flying the player needs to use active blocks like magical force generators or bouyant blocks. These active blocks work by applying forces inside the physics system. An automated system controls these active blocks, pushing different amounts to maintain stability, rotate, or move forward.

## Block types
### Wood
Static construction material.
Weight=1

### Metal
Static construction material.
Weight=3

### Stone
Static construction material.
Weight=6

### Push Block
Pushes in a single fixed direction, can be placed to face in any direction.
1 push block at max thrust makes up for 10 wood blocks
energy cost at max thrust is 10/sec
energy cost does not scale linearly, halfing the thrust is more than half the energy use
Weight=3

### Bouyant Block
Passively pushes the airship upwards without requiring energy
2 bouyant blocks make up for 1 wood block

### Void Block
Passively pushes the airship upwards, requiring energy to function. Less energy than Push Blocks.
1 void block makes up for 2 wood blocks
Energy cost is 1/sec

### Controller Block
Central brain for the airship, controls active blocks like Push Blocks.
Passively keeps the airship upright and stable by triggering Push Blocks.
The player can take control and actively steer the airship, again by triggering Push Blocks.
The player uses simple inputs like forward, backwards, rotate, and the controller system translates that into Push Block commands.
The player can lock in a heading and the controller will maintain the specified heading and speed.
The controller block requires energy to function
Energy cost is 1/sec
Weight=3

### Battery block
Stores energy for active blocks to use.
One battery stores 10000 energy
Weight=6

### Solar panel
Magical solar panel passively provides power to the system
One solar panel generates 1/sec
Weight=2

### Furnace Engine
Magical engine provides power to the system by burning materials (wood, coal, etc.)
One furnace engine generates 10/sec
Weight=3
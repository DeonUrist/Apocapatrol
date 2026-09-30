# Apocapatrol 1.4.4

A BepInEx 5 mod for **Apocalypter** that puts raiders on the road. Armed gangs in scrap-built cars and trucks roam the wasteland and hunt
you down. They ram you, shoot at you, and haul loot worth taking from them. The further you travel and the more bosses you kill, the
more often they come and the tougher they get.

## Raider cars

Every raider vehicle is built from the game's own frames and parts, so it looks, drives and breaks like any other car in the game. Once
its crew is dealt with it's yours to take.

- **Small cars**: PipeRat, Poloska and TinyTyrant, each in a basic and an advanced (stronger engine, better wheels) version.
- **Junkers**: heavier, harder-hitting cars, basic and advanced.
- **Trucks**: the Rustcargo, with dual rear wheels, as a plain truck or a loot truck. The advanced truck has armored wheels and a big diesel.
- Every car rolls a random look: small cars and junkers may carry a front bumper, and trucks always have one of four heavy bumpers.
- Nothing is brand new. Parts are worn (2–35 % condition), and the tank, engine oil and radiator are only partly filled (15–60 %).

## The crews

Each car carries a driver and a passenger picked from the scrapyard gang: Boltjaw, Flexa, Lugnut, Scrud, Sprokka, Scraffa and Spanna.

- They sit in their seats in a proper seated pose and can be shot like any other enemy.
- **Gunmen** (Boltjaw, Flexa, Lugnut, Scrud, Sprokka) shoot from the moving car at anything in front of it, up to 40 m away. A gunman at
  the wheel fires in short bursts between stretches of driving. Scraffa and Spanna just ride along.
- **Kill the driver** and the car rolls to a stop, unless his foot stays jammed on the gas (a small chance). The surviving passenger then
  either takes the wheel and keeps coming, or jumps out and fights on foot.
- While the driver lives you can't get into the car. Once it's empty, it's yours.
- If a car gets stuck for good or runs out of fuel, the crew may climb out and come after you on foot, or stay put in the dead car.

## How they drive

Raiders drive at you, not randomly. They aim for where you'll be, not where you are, ram you, overshoot, swing around in a wide
turn and come back for another pass. They steer around rocks, wrecks and buildings, back out when they hit a wall or get stuck, and
stay clear of cliffs. Small cars go for you when you're on foot, while trucks will happily ram your car. If you get far enough away
(700 m) they give up the chase until you come closer again. They'll also release a handbrake you pull on them.

## Getting hit

A raider car that runs into you hurts. A full-speed hit (30 km/h or faster) takes about **30 health from a small car, 50 from a
junker, 70 from a truck**. At half that speed it takes half, and slower bumps do nothing. On foot the hit also knocks you away. You get
the usual red flash and hurt sound. Walking or driving into a parked car does no damage, god mode protects you, and by default only
hits on foot count. Hits while you sit in your own car are optional.

When a truck keeps shoving your car you can still bail out: with this mod you can leave a car at up to 30 km/h instead of the game's
usual 21 km/h.

## Loot trucks

A loot truck carries a random cargo in its bed:

| Cargo | Chance | What's inside |
|---|---|---|
| Food | 18 % | 6 cans of dog food |
| Water | 14 % | 4 water cans, half the time a water barrel as well |
| Gasoline | 11 % | 4 gasoline cans, half the time a gasoline barrel as well |
| Diesel | 11 % | 4 diesel cans, half the time a diesel barrel as well |
| Medicine | 11 % | 4 bandages and 2 first aid kits |
| Weapons | 11 % | up to 3 random guns and 3–8 boxes of ammo |
| Drugs | 7 % | alcohol, weed bags and a weed plant |
| Mechanic | 7 % | 3 repair boxes and a big can of motor oil |
| Corpses | 7 % | 3–5 dead Scraffas |
| Rats | 4 % | 6–8 dead rats |

The advanced loot truck carries 1.5–2 times as much. Everything in the bed is a real item that you can pick up, use or sell.

## Convoys and patrols

Raiders show up on their own while you play, usually far ahead of your car, or behind you when you're on foot. Every 5 to 60 minutes
the game rolls what comes next. At first you only meet small groups, and bigger, better-armed ones follow as you get further from the
starting area and kill more bosses.

| Group | Appears from | What comes |
|---|---|---|
| Basic enemy cars | the start | 3 small cars |
| Basic convoy | 5 km | a truck, 2 junkers and 3–5 small cars, sometimes one advanced car among them |
| Advanced convoy | 20 km and 3 bosses | an advanced truck with the same escort, half of it advanced |
| Advanced enemy cars | 30 km and 1 boss | 3 cars, at least one advanced, maybe a junker |
| Super advanced enemy cars | 50 km and 3 bosses | 5 cars, many of them junkers, at least two advanced |

**Heat** rises by 25 % every 10 km you travel from the starting area, up to 300 %. Below 100 % heat the groups are still incomplete (one car
instead of three near the start); from 100 % on they are full size and stay that way, and higher heat only makes the tougher groups turn up
more often and shortens the wait between them. Distance and boss kills come from your save, so adding
the mod to a game in progress picks up right where you are.

A group builds up out of sight and then sets off all at once.

**Patrol size** (the first setting, 25 / 50 / 100 / 125 / 150 %) scales how many cars every group brings. Lower it if the game stutters
when a convoy appears: fewer cars means fewer crews, physics bodies and AI drivers at once. A group always brings at least one car, and a
convoy always brings its truck first.

## Cleanup

Raider cars you leave behind don't pile up in your world or your save. A raider car that stays more than 800 m away from you for
40 minutes disappears together with its crew and cargo, and if more than 30 raider cars are around, the farthest ones go first. A car
you've sat in counts as yours and is never removed, whether it's loot you took or a car you drove.

## Self-destructing cars

While a raider drives, you can't take the car apart: the wrench does nothing on its wheels, engine or seats until the driver is dead.

Once a raider car is fully vacated - the crew is dead, they bailed out, or the last passenger climbed out beside a dead driver - the car
blows up with the exploder zombie's fireball and bang (just the show: it doesn't hurt you): the frame turns black, and every part pops off. Most popped parts are wrecked (0 condition); about one in five keeps its
condition and is worth picking up (**CarPartsLootFromExplodedCars**, 12 % by default - the roll favours low numbers, so 12 gives a bit more
than 12 %). What's left is a dead chassis: you can't get in, fuel it or fit parts to it, and it disappears once
you're 1000 m away. Trucks are different: they don't turn black, their wheels stay on (with the same condition roll), they stay
parked where they stopped, their rear doors still open, and the cargo stays in the bed for you to loot. A car you've sat in never explodes. Turn **SelfDestructingCars** off and vacated cars stay as they were before: driveable,
strippable, cleaned up by the rules below.

## Saving and loading

Raider cars, their crews, the crews' health and what they were doing are saved with your game. After a load they start their engines
and carry on. The timer until the next convoy is saved too.

## Settings

Every setting can be changed in game through the Apocasetter Mods menu if you have it, or in
`BepInEx\config\com.denis.apocalypter.apocapatrol.cfg`.

- **General**: the patrol size (100 %), see above.
- **Combat**: whether gunmen shoot, how far (40 m) and how wide around the car's front they can fire, how long and how often the driver's
  bursts come, ram damage on/off, its strength (0–3×) and the full-damage speed, damage per car type, the knock-back strength, and ram
  damage while you're in your car.
- **Driving**: the chances of a jammed gas pedal (5 %), of the passenger bailing out instead of taking the wheel (25 %), and of the crew
  leaving a stuck car (50 %).
- **Self-destruct**: self-destructing cars on/off, and the share of popped parts that keep their condition (12 %).
- **AI**: how the raiders drive, including throttle, steering, how far they run out after a pass, obstacle avoidance and the give-up distance.
- **Loot**: the chance of each cargo, a loot multiplier (0–3×), and the ranges for part condition and fuel/oil/water levels.
- **Convoy spawner**: on/off, maximum heat, km per heat step, spawn distance (350 m), the cars-versus-convoy ratio, the minimum and maximum
  time between spawns, and the distance, boss kills and chance for each group.
- **Cleanup**: on/off, how long a car must be left behind before it's removed (40 min), the most raider cars at once (30), and the distance
  inside which nothing is ever removed (800 m).
- **Debug**: the key for the F8 spawner menu, detailed logging, an on-screen AI readout, and the speed at which you can leave a car (30 km/h).

## F8 menu

Press **F8** to open the spawner. **Enemy cars** and **Enemy patrol** at the top call in a group right away, as if you had already
travelled far enough. Below them every car and truck is listed, and clicking one builds it with its crew in front of you. It's meant
for testing and for picking a fight on purpose.

## Install

1. Install BepInEx 5 in the game folder.
2. Copy `Apocapatrol.dll` into `BepInEx\plugins\`.
3. Optional: install **Apocasetter** to change every setting in game.

Works together with **Apocatremors**, which eases off its own ambushes while this mod is running. It's safe to add to an existing save.
If you remove the mod later, the raider cars stay in your world as ordinary empty vehicles.

# Apocapatrol 1.20.1

A BepInEx 5 mod for **Apocalypter** that puts raiders on the road. Armed gangs in scrap-built cars and trucks roam the wasteland and hunt
you down. They ram you, shoot at you, and haul loot worth taking from them. The further you travel and the more bosses you kill, the
more often they come and the tougher they get.

## Raider cars

Every raider vehicle is built from the game's own frames and parts, so it looks, drives and breaks like any other car in the game. Once
its crew is dealt with it's yours to take.

- **Small cars**: PipeRat, Poloska and TinyTyrant, each in a basic and an advanced (stronger engine, better wheels) version.
- **Junkers**: heavier, harder-hitting cars with doors, plates and a full body kit, basic and advanced.
- **Motorcycles**: the Halfbreed, basic and advanced, ridden by bikers.
- **Trucks**: the Rustcargo, with dual rear wheels, as a plain truck or a loot truck. The advanced truck has armored wheels and a big diesel.
- Every car rolls a random look: small cars and junkers may carry a front bumper, and trucks always have one of four heavy bumpers.
- They drive with their headlights on.
- Knives and other melee weapons can slash the wheels of any car, raider or not. In the game itself a knife only cuts a loose wheel
  and passes straight through one fitted to a car. (With Apocaraider installed, its wheel rules apply instead: the wheel damage
  multiplier, hit numbers and wheels popping off.)
- Nothing is brand new. Parts are worn (2–35 % condition), and the tank, engine oil and radiator are only partly filled (15–60 %).

## The crews

Each car carries a driver and a passenger picked from the scrapyard gang: Boltjaw, Flexa, Lugnut, Scrud, Sprokka and Scraffa.

- They sit in their seats in a proper seated pose and can be shot like any other enemy.
- **Gunmen** (Boltjaw, Flexa, Lugnut, Scrud, Sprokka) shoot from the moving car at anything in front of it, up to 40 m away. A gunman at
  the wheel fires in short bursts between stretches of driving. Scraffa just rides along.
- **Kill the driver** and the car comes to a stop, unless his foot stays jammed on the gas (a small chance). Four seconds later the
  surviving passenger acts, whatever the car is doing: takes the wheel and keeps coming, or jumps out and fights on foot.
- A car with doors opens the right door first: the passenger's own door before he jumps out, the driver's door before a dead
  driver is thrown out.
- While the driver lives you can't get into the car. Once it's empty, it's yours.
- If a car gets stuck a third time in a row, the crew climbs out and comes after you on foot. A car that runs out of fuel is left the
  same way.

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

## Loot in the cars

Raider cars (not trucks or motorcycles) may carry something on the back seat: a box of ammo of a random calibre (70 %) or a bandage
(30 %). With **LootMultiplier** at 1 a basic car always has one item; an advanced car rolls twice that (two items). Each full 100 %
is a sure item and the rest is the chance of one more (1.5 = one item and a 50 % chance of a second; advanced: three items).

## Loot trucks

A loot truck carries a random cargo in its bed:

| Cargo | Chance | What's inside |
|---|---|---|
| Food | 18 % | 6–10 cans of dog food |
| Water | 14 % | 4 water cans, half the time a water barrel as well |
| Gasoline | 11 % | 4 gasoline cans, half the time a gasoline barrel as well |
| Diesel | 11 % | 4 diesel cans, half the time a diesel barrel as well |
| Medicine | 11 % | 5 bandages and 2–3 first aid kits |
| Weapons | 11 % | 2–4 random guns and 3–8 big boxes of ammo |
| Drugs | 7 % | alcohol, weed bags and a weed plant |
| Mechanic | 7 % | 3 repair boxes, a big can of motor oil, and a 15 % chance of a brand-new V8 engine (30 % on the advanced truck) |
| Corpses | 7 % | 3–5 dead Scraffas |
| Rats | 3 % | 6–8 dead rats |

The advanced loot truck carries 1.5–2 times as much. Everything in the bed is a real item that you can pick up, use or sell.

## Convoys and patrols

Raiders show up on their own while you play, usually far ahead of your car, or behind you when you're on foot. Every 5 to 60 minutes
the game rolls what comes next. At first you only meet small groups, and bigger, better-armed ones follow as you get further from the
starting area and kill more bosses.

| Group | Appears from | What comes |
|---|---|---|
| Basic enemy cars | the start | 3 small cars |
| Basic bikers | the start | 3 motorcycles |
| Basic convoy | 5 km | a loot truck, 2 junkers and 3–5 small cars, sometimes one advanced car among them (in 30 % of convoys the small cars are motorcycles) |
| Advanced convoy | 20 km and 3 bosses | an advanced loot truck with the same escort, half of it advanced |
| Advanced enemy cars | 30 km and 1 boss | 3 cars, at least one advanced, maybe a junker |
| Advanced bikers | 30 km and 1 boss | 3 motorcycles and 2 advanced ones |
| Super advanced enemy cars | 50 km and 3 bosses | 5 cars, many of them junkers, at least two advanced |
| Super bikers | 50 km and 3 bosses | a junker leading 3 motorcycles and 3 advanced ones |

**Heat** rises by 25 % every 10 km you travel from the starting area, up to 300 %. Below 100 % heat the groups are still incomplete (one car
instead of three near the start); from 100 % on they are full size and stay that way, and higher heat only makes the tougher groups turn up
more often and shortens the wait between them. Distance and boss kills come from your save, so adding
the mod to a game in progress picks up right where you are.

A group builds up out of sight and then sets off all at once.

**Patrol size** (the first setting, 25 / 50 / 100 / 125 / 150 %) scales how many cars every group brings. Lower it if the game stutters
when a convoy appears: fewer cars means fewer crews, physics bodies and AI drivers at once. A group always brings at least one car, and a
convoy always brings its truck first.

Only one group is out hunting you at a time. When the next one is due while raiders from an earlier group are still alive: if all of them
are at least 300 m away, they vanish with their cars (crew, parts and cargo) and the new group comes; if any of them is closer, the new
group waits and the next try comes sooner than usual.

## Cleanup

Raider cars you leave behind don't pile up in your world or your save. A raider car that stays more than 800 m away from you for
10 minutes disappears together with its crew and cargo, and if more than 30 raider cars are around, the farthest ones go first. A car
you've sat in counts as yours and is never removed, whether it's loot you took or a car you drove.

## Self-destructing cars

While a raider drives, you can't take the car apart: the wrench does nothing on its wheels, engine or seats until the driver is dead.

Once a raider car is fully vacated - the crew is dead, they bailed out, or the last passenger climbed out beside a dead driver - the car
blows up with the exploder zombie's fireball and bang (just the show: it doesn't hurt you): the frame turns black, and every part pops off. Most popped parts are wrecked (0 condition); about one in five keeps its
condition and is worth picking up. What's left is a dead chassis: you can't get in, fuel it or fit parts to it, and it disappears once
you're 1000 m away. Trucks are different: they don't turn black, their wheels stay on (with the same condition roll), they stay
parked where they stopped, their rear doors still open, and the cargo stays in the bed for you to loot. A car you've sat in never explodes. Turn **SelfDestruct** off and vacated cars stay as they were before: driveable,
strippable, cleaned up by the rules below.

## Paint jobs

The raiders' cars wear their own paint. Put a PNG named after the car into the mod's `Textures` folder and every car of that
kind the mod spawns (basic and advanced, in patrols and convoys) wears it; the same cars you find in the world keep the game's
look. `junker.png`, `poloska.png`, `tinytyrant.png`, `piperat.png` (the PipeRat's pipe frame), `piperat_dashboard.png` (its
dashboard) and `rustcargo.png` (the truck's cab) are recognised. Keep the layout of
the game's texture (its originals: Junker 1024 x 1024, Poloska and TinyTyrant 4096 x 4096, Rustcargo and PipeRat 2048 x 2048; the PipeRat's
textures repeat along its pipes, so recolour them rather than paint a picture on them). Any square size works - smaller files load faster;
all of them are loaded once when the game starts. Keep the
Junker's transparent spots - those are its rust holes. Any other car texture works too: a PNG named after the game's texture
replaces it on every raider car that uses it.

Cargo trucks show what they carry on the two long sides of their container. The roof, the ends and the rear doors keep the
game's look.

| Load | Side picture |
|---|---|
| Water, Gasoline, Diesel, Medicine, Weapons, Drugs, Mechanic | `Textures\cargo_water.png`, `cargo_gasoline.png`, `cargo_diesel.png`, `cargo_medicine.png`, `cargo_weapons.png`, `cargo_drugs.png`, `cargo_mechanic.png` |
| Dog food, rats, corpses | `Textures\cargo_food.png` |
| Empty truck, or anything else | `Textures\cargo.png` |

Each file is one side of the container as you see it standing next to the truck: upright, left to right, about twice as wide as
tall (2048 x 1024 works well). Both sides show the same picture and read the right way round. Transparent pixels let the
container's own texture show through. A missing file falls back to `cargo.png`; without that the sides stay as they are.

The inside of the container (both walls, floor, roof and the closed front end) can have its own look for every truck:
`Textures\cargo_inside.png`. It repeats every 2 m, so a square, seamless texture works best (1024 x 1024 = 2 x 2 m of
wall). Without the file the inside keeps the game's texture.

Turn **CustomPaintjobs** off (under Debug) to skip all of this: raider cars then look like the game's own.

## Saving and loading

Raider cars, their crews, the crews' health and what they were doing are saved with your game. After a load they start their engines
and carry on. The timer until the next convoy is saved too. All of it is stored inside the game's own save file, so it travels with
Steam Cloud and copied saves; paint jobs come back after a load as well. (Saves made with versions before 1.9.0 keep their raider data in
`BepInEx\config\Apocapatrol\Saves`, which is still read; their cars get their paint back on the next load.)

## Settings

A short list, changeable in game through the Apocasetter Mods menu if you have it, or in
`BepInEx\config\com.denis.apocalypter.apocapatrol.cfg`. Everything else is tuned in and fixed.

**Scaling**
- **PatrolSizePercent** (100 %): how many cars every spawn brings, 25–150 %. Lower it on a weak PC.
- **AudioVoices** (64): how many sounds the game may play at once. The game ships with 32, which a firefight with several raider cars
  overflows - shots and hits then cut out, your own included. Applied when the game starts; 0 keeps the game's setting.
- **SelfDestruct** (on): vacated raider cars blow up (see above).
- **LootMultiplier** (1): how much loot trucks carry, 0–3×, and the chance of a back-seat item in the cars (× 2 in advanced cars).
- **MinConvoyCooldown** (5 min) / **MaxConvoyCooldown** (60 min): the wait between two raider spawns is rolled between these, a
  little shorter as the heat rises. 0 as the maximum turns automatic spawns off. A change counts from the next roll.

**Combat**
- **RangedCombat** (on): gunmen in the cars shoot at you.
- **ShootDistance** (40 m): how close you have to be before they open fire.
- **RamDamage** (on): a raider car that hits you on foot hurts you.
- **RamDamageInCar** (off): ...and also while you sit in your own car.

**Debug**
- **TemplateSpawnerKey** (none): the key for the spawner menu, off by default.
- **VerboseLog** (off): detailed logging.
- **AiOverlay** (off): an on-screen readout per raider car; with no raider car driving, the time until the next spawn roll.
- **CustomPaintjobs** (on): raider cars wear the paint jobs from the `Textures` folder. Off = they keep the game's own look
  (cars spawned from then on).

## Spawner menu

For testing: set **TemplateSpawnerKey** to a key (F8, say) and press it to open the spawner. At the top, set a **distance travelled**
(the heat it gives is shown next to it) and a number of **bosses killed** - they start at your save's values - and see which patrols and
convoys that unlocks and how likely each is. **Spawn (game roll)** calls in a group exactly as the game would at that point (patrol or
convoy, then which one, at that heat - smaller groups below 100 % heat); **Patrol** and **Convoy** roll only among the patrols or the
convoys. The spawn timer and raiders already out are left alone. Below them every car and truck is listed, and clicking one builds it with its crew in front of you. It's meant
for testing and for picking a fight on purpose.

## Your own raider cars

Build a car in game the way you like it (frame, wheels, engine, seats, plates, spikes, roof rack, anything the wrench attaches,
even items locked in the bed), then save it as a template with **Apocatemplater** (a small companion mod, dump key F9 by
default): sit in the car or look at it and press the key. The template lands in `BepInEx\plugins\Apocapatrol\CarTemplates\`
as a `.json` file and shows up in the spawner menu the next time you open it, marked `[file]`.

Every template joins the raider patrols and convoys by its `kind` (`small`, `medium` - the junker's role, `junker` still
works -, `truck` or `motorcycle`) and `tier` (`basic` or `advanced`), with `weight` as its relative chance among the cars of the
same role. A car can have several kinds, separated by `;` (for example `"kind": "small;motorcycle"`): it counts as each of them. Open the file in a text editor to change the
crew (`driver`, `passenger`), what it rams (`None`, `Pedestrians`, `Cars`), the bed (`cargo`: empty, `Random`, a loot type like
`Food`, or the item list the dump wrote), the front bumper roll (`bumpers`), or set `spawns` to `false` to keep it out of the
patrols (spawner menu only). A file with the name of a built-in car replaces that car.

Templates in the mod's own `CarTemplates` folder of the source are built into `Apocapatrol.dll`, so they ship with the mod.

## Install

1. Install BepInEx 5 in the game folder.
2. Copy the `Apocapatrol` folder (with `Apocapatrol.dll` and `Textures`) into `BepInEx\plugins\`. If you have an older
   `BepInEx\plugins\Apocapatrol.dll` lying loose in `plugins`, delete it.
3. Optional: install **Apocasetter** to change every setting in game.

Works together with **Apocatremors**, which eases off its own ambushes while this mod is running. It's safe to add to an existing save.
If you remove the mod later, the raider cars stay in your world as ordinary empty vehicles.

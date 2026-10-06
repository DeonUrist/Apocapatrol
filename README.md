# Apocapatrol 2.1.2

2.1.2: the rider screams "Witness me!" to the end first and leaps a moment later (about 1.9 s in all), aimed at where you are then -
time to get away.

# Apocapatrol 2.1.1

2.1.1 blast-lance riders: they only throw when a flat throw really reaches you (35 m at most) and aim where you will be; when the
crew bails out (or the car is abandoned, taken or blows up) the rider jumps off and fights on foot; drive close to a rider and - always
when it is wounded, otherwise with a 10 % chance - it screams "Witness me!" and leaps at your car, exploding on impact (aimed ahead).
`[Debug] WitnessMeChance` (test setting, will be removed) sets that chance. Needs the `Sounds` folder next to the DLL.

# Apocapatrol 2.1.0

2.1.0 (prototype): **Blast-lance riders** - a template (cars only, no trucks or motorcycles) can carry a raider crouching on the roof who
throws blast lances at you every few seconds (`[Combat] BlastlanceRiders`, on by default). Set it with **E** on a template or in the
save-template window; the spot is the top of the car unless the template has a `riderPos`. With Apocaplayer installed the rider uses its
crouch and throw animations. The separate **Apocaspotter** plugin shows the spot as glowing markers and lets you move and save it.
Also: the **Rustliner** bus can be templated and crewed now (prototype - report seat problems).

2.0.16: raider cars no longer roll over in turns. The driving AI knows how top-heavy its car is (track width and the height of its
centre of mass) and keeps the turn within what the chassis can take: less lock at speed, braking for a turn it cannot make at its speed,
and steering straightened when the body starts to lean. Low cars drive as before; lifted ones on big wheels take their turns wider.

2.0.15 fixes 2.0.13/2.0.14: templates saved or edited with those versions lost their driver, passenger and rams lines (the car spawned
empty). Re-save or press E on such a template once to put the crew back.

2.0.14: a raider car whose template has a cassette in the radio blasts it at full volume from the spawn until you switch the radio
off. [General] Raider music, on by default.

2.0.13 editor: a small **rename** next to every patrol, convoy and loot option name; an **E** next to every template name edits its
driver, passenger and car loot (trucks: a fixed cargo option, or the convoy's rolled cargo); up/down triangles reorder patrols, convoys,
car templates and truck templates (the order is kept in PatrolsAndLoot.json); dropdown lists are sorted alphabetically.

2.0.12: motorcycle riders (Motorcycle, Halfbreed) hold their arms apart on the handlebar grips instead of together.

2.0.11: drivers (cars, trucks, motorcycles) always sit in the driving pose, also with a gun and also a passenger who took the wheel;
they switch to the shooting pose only while they actually fire, then back to the wheel.

2.0.10: templates saved from the motorcycle (the one without a back) have no loot setting: the Car loot choice is not offered.

2.0.9: when raiders spawn (automatic or test patrols and convoys, not template spawns) a red warning top left says
"You hear a sound of distant engines" for a few seconds. [General] SpawnWarning, on by default.

2.0.8: raider cars only switch their headlights on when it is dark (the sun below the horizon) and switch them off again at dawn.
A save never stores a half-built raider car: spawns wait while the game saves, a car joins the save only once it is complete, and
automatic spawns hold back when an Apocasaver autosave is about to run.

2.0.7 (review fixes): the exit-speed limit finds the player camera again (PlayerCameraHolder/PlayerCamera), so you can leave a car below
30 km/h as intended; a car being built when a save loads no longer throws; loot ids compare case-insensitively in the editor; the
font lookup and a few per-frame allocations are cached; a failed motorcycle save hook no longer stops the whole mod from loading.

F3 starts with all groups collapsed and no template selected. Vehicle template creation uses anchored, scrollable dropdowns with flat rows;
click outside or press Escape to close them. Selection is deferred until drawing finishes and the form behind each dropdown is disabled,
preventing click-through and the Empty car loot selection from reopening its menu. The editor, Loot tab and separate template-saving window implement the selected Salvage ledger design.


The UI uses the prototype's brown/gold palette, continuous ledger rows, square fields and joined tabs, with the game's Helveticrap heading font,
rust background and thin metal frame. Both template lists have their own scroll. Chevron, star, trash and Remove textures match the prototype.
Loot quantities sit immediately left of their percentages; Remove is used for item/roster contents, while trash is reserved for whole objects.
All dropdowns share an anchored scroll menu with click-off/Escape dismissal. Dialog buttons stay below their fields. The window scales as one
layout to fit the display while preserving the prototype's proportions. Existing vehicle rosters, loot contents, item chances, template parts and key bindings are preserved. Legacy truck loot references are ignored; cargo selection now belongs to convoys.


2.0.6 extends the group list to its panel bottom, centers button glyphs vertically, renames the footer actions **SPAWN** / **RESET**, and removes
header/footer helper text. The footer shows only the selected template’s body, driver, passenger and Car Loot (or **Convoy Cargo** for trucks).
Attached blastlances are recorded using their native `CheckBool.Attached` state and retain placement, nested parents, weapon tags and attachment
state when rebuilt. Loose and bed-locked items remain excluded. Obsolete tier multipliers, automatic tier sizing, hidden tier configuration and
legacy string-based cargo generation have been removed; Basic/Advanced words in existing names have no special behavior.

Raider patrols and convoys for Apocalypter, with an in-game vehicle-roster and loot editor.

## Editor

Bind **ApocaPatrol Config Key** under Apocasetter → Apocapatrol → Debug, or in
`BepInEx/config/com.denis.apocalypter.apocapatrol.cfg`. Existing bindings are preserved; the default is None.

- **Patrols** and **Convoys**: expand a type to edit minimum distance, boss kills, spawn chance, and its vehicle roster.
- Only the selected group is expanded; selecting another collapses the previous group. Collapsing a selected group clears the Add Template target.
- Every roster entry produces one vehicle. Adding the same template twice produces two copies. Convoys can contain multiple trucks.
- Spawn chances normalize across eligible group types. **PatrolSpawnChancePercent** in Apocasetter controls the global patrol/convoy split.
- Empty patrols show a gray **Basic PipeRat** fallback. A convoy without a truck shows a gray **Empty truck** fallback.
- **Allowed cargo** on each expanded convoy opens a two-list allocation window: add/remove options from Convoy Loot. Empty allowed lists spawn no cargo.
- **Uniform cargo** shares one allowed cargo type and one item roll across the convoy trucks. Otherwise each truck rolls independently from that convoy’s allowed pool.
- The right column has separate scrolling car and truck lists. Cars can be patrol members or convoy escorts; trucks belong to convoys.
- Template rows show only the template name. Both lists have room for at least three full entries; Add/Spawn actions sit at the bottom.
- Create and delete group definitions with the buttons beside their names.

## Loot

The **Loot** tab manages named options for patrol cars and convoy trucks. Add any discovered, physical pickup prefab, including weapons,
supplies, loose vehicle parts, and carcasses. Items are discovered from the loaded game's prefabs; Apocaspawner names are used when available.

Each item has an **independent absolute chance**, not a weight competing with other items:

- 50%: 50% chance of one item.
- 100%: one guaranteed item.
- 150%: one guaranteed item and 50% chance of a second.
- 200%: two guaranteed items.

Car templates reference a named **Car Loot** option. Truck templates do not store loot choices. Convoys select their allowed cargo options; cargo selection chances are weights within that allowed pool, independently of the absolute chances for items.

Each convoy loot option includes a **Truck bed texture** dropdown listing all successfully loaded `Textures/cargo_*.png` images, including custom files. None keeps the native bed appearance. Textures are determined by the selected cargo configuration, not inferred from its item names.
Deleting a loot option clears its references; those templates spawn no loot until another option is selected or defaults are reset.

## Create your own vehicle template

Bind **TemplateDumpKey** under Debug (None by default). It works **only while seated in a vehicle**. Looking at a vehicle or standing nearby does nothing.

The key opens a separate save window containing a name and crew, plus a **Car Loot** dropdown for cars. Trucks have no loot dropdown. Storage trucks are recognized from
the Rustcargo chassis and its frame's storage. Parts, part-on-part attachments, fitted poses, and adjusted hinges are recorded automatically.
Wheel motion, engine wobble, and items locked in the bed are not recorded.
Bus/Rustliner templating is temporarily disabled until crew seating is fixed.

**Cancel** or Escape discards the draft. Leaving the vehicle also cancels it. Only **Save template** writes JSON, into **PlayerTemplates**.
An empty name generates `<Body>_Custom`, `_Custom_2`, etc.; named existing templates cannot be overwritten accidentally.
Templates have no basic/advanced tier property; those words are simply part of their names.

## Templates and favourites

The mod ships 16 JSON defaults in **BaseTemplates** and keeps immutable copies inside the DLL for recovery. It loads these defaults together
with local **BaseTemplates** and **PlayerTemplates** files. Missing disk defaults remain available from the DLL.

- Star a custom player template to move it into **BaseTemplates**.
- Un-star a custom favourite to move it back into **PlayerTemplates**.
- Shipped defaults have locked stars and disabled trash buttons.
- Favourite templates have disabled trash buttons. Un-favourite a custom template before deleting it.
- Red trash moves a custom template to the **Windows Recycle Bin** and removes every reference to it from group rosters.

Old `CarTemplates` files are imported once as player templates; originals are kept. Imports that collide with a shipped name receive a legacy name.

## RESET

RESET restores shipped templates, patrol/convoy definitions, rosters, requirements, chances, Uniform cargo settings, and default loot options.
It backs up **BaseTemplates** into **OldTemplatesFolder**; collisions receive `_1`, `_2`, and so on.

The confirmation asks whether to delete custom templates. Unchecked keeps them in their current folders, including favourites.
Checked moves custom templates, including favourites, into the Windows Recycle Bin. Custom loot options are retained.

Changes are saved automatically in **PatrolsAndLoot.json** beside the DLL. Invalid settings are preserved in an `invalid` backup before defaults are used.

## Integrated motorcycle

**motorcycle_template** uses MotorcycleMod 0.1.1's default equipped two-wheel Crossbreed-derived chassis: motorcycle tyres, 594cc/23HP engine,
motorcycle radiator, exhaust, driver seat, and 10 litres of fuel. It has one **Scrud SMG driver** and no passenger. The underlying Motorcycle body is bare; all fitted parts come from the JSON template.
It uses **Empty car loot** so customized Standard car loot cannot add loose car/truck engines. Unchanged 2.0.2 defaults upgrade with a backup; edited templates are preserved.

The balance controller and stable Easy Save prefab identity are included in Apocapatrol. MotorcycleMod is not required; if it is installed,
Apocapatrol shares its chassis and existing settings. Rear fender collision extending beyond the tyre is disabled in both factories, including save reconstruction. Vanilla Crossbreed is unchanged. The default Basic bikers roster uses three copies of
motorcycle_template. Existing unmodified Basic bikers rosters are upgraded once; custom rosters remain untouched.

The native verifier tested geometry, wheel setup, balance, acceleration, save reconstruction, SMG rider seating, and the actual Apocapatrol
template-build coroutine, empty chassis and rear player capsule clearance: **81 checks passed** in an isolated runtime using the installed game assemblies/prefabs.

## Debug spawning

Enable **AllowDebugSpawns** in Apocasetter → Debug to reveal:

- **Spawn template**, below Add template: build the selected vehicle 8 m ahead.
- **S**, beside each group's trash: force that group's exact roster.
- **Test spawning**, beside RESET: use the save's real distance/boss requirements, global patrol/convoy distribution, and eligible group chances.

These controls are hidden by default. Debug spawning does not reset the automatic spawn timer or clear existing groups.
The editor pauses the game, blocks input through Apocasetter when installed, and restores the previous cursor and time state when closed.

## Driving, combat, textures and saves

Existing driving, crew takeover/bailout, melee wheel damage, ram damage, cleanup, self-destruct, and save/load behavior remain in use.
Automatic spawning keeps the cooldown and older-group checks; vehicle counts come from the rosters.
Crew leaving Rustcargo, Rustchief, or Rustallion cabs use twice the sideways exit clearance to avoid getting stuck in the cab.

Body textures are selected by the **vehicle body**, never by template name. Truck container textures follow the **loot contents**.
Existing paint files under `Textures` still work. Car crews and their state remain in the game's save file.

## Installation and build

Install BepInEx 5, then place the **Apocapatrol** folder under `BepInEx/plugins`. It contains the DLL, BaseTemplates, Textures, and theme/game.
Do not leave a second Apocapatrol DLL in the plugins root. Apocasetter is recommended for bindings and general settings.

`dotnet build Apocapatrol.csproj -c Release` builds and deploys the DLL, textures, and UI theme to the configured game directory.
Add `-p:DeployToGame=false` for a build without deployment. Missing shipped template files are materialized by the loader; existing disk edits are preserved.
`build.sh` also includes the JSON defaults and new editor sources.

The 2.0.6 verification suite covers JSON round trips, exact rosters, loot probabilities, global distribution, fallback groups, protected defaults,
favourite transfers, native Windows recycling, RESET, backup collisions, corruption preservation, and path guards.
It also checks key migration/order, single-group selection, large-cab clearance, and the bus template blacklist.

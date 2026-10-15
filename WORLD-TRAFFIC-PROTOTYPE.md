# Apocapatrol 2.11.0 world traffic prototype

The first settings section is **Experimental**:

| Setting | Default | Behaviour |
| --- | ---: | --- |
| car spawn limit | true | Caps physical cars and pending builds in an encounter |
| Max cars in event | 5 | Per-group cap; convoy cargo trucks have priority |
| Max cars in double event | 6 | Shared total for encounters materialized near the player |
| Max patrols | 3 | Ceiling for patrol groups, including arrived groups |
| Max convoys | 1 | Ceiling for convoy groups, including arrived groups |
| Camp traffic range | 2500 m | Loaded, uncleared camps in this radius each support one group per role |
| simple convoy bandits AI | true | NPC Overhaul 1.9.14: AI off in seats, persistent combat after dismount |
| Real patrol names | true | Use actual group names; off uses Patrol or Convoy |
| hide route type | false | Gray routes and Distant traffic names for both roles; overrides real names |

Coarse elevation is cached with road hints. A worker evaluates at most 16 waypoint bands with 12
alternatives per band and eight height samples per connection. Length, climbs, rough slopes and high
ground are penalized; road influence remains a gentle preference. There is no full road/world graph,
per-frame height scan or terrain physics read. Missing height data retains the cached snake fallback.

Only loaded, uncleared camps contribute to local population. No such camps means virtual groups are
removed and no new traffic is generated. Live encounters finish/unload normally. Known uncleared
bases remain eligible destinations even when their terrain is unloaded. A base is Cleared only after
its spawners finish and all observed residents die; live despawns and empty/unloaded camps cannot
trigger it. Cleared flags persist, appear on the game debug map, exclude new endpoints and cancel
virtual journeys to/from that base. Live traffic headed there is redirected to an uncleared base.

Fixed group caps replace kilometre density. Zero disables replenishment for that role. Lowering a cap
retires excess virtual groups away from the player; nearby/live groups finish or unload normally. The old
total MaxGroups/MaxRealCars budgets still provide upper bounds. Existing physical groups are not deleted
in front of the player when enabling car limits. New spawns/materializations respect the caps, and older
world rosters keep deferred slots simulated so they can appear on a later encounter. Trucks are loaded first.

Convoy crews do not bail from failed obstacle recovery or dry-tank checks. Recovery duration and steering
remain bounded and retry continuously. A living passenger takes over a dead convoy driver instead of
rolling a voluntary bailout, unless the player captures the vehicle. Combat deaths, wrecks, missing fuel
and stolen vehicles are still real game conditions; this change does not repair or teleport vehicles.
Schema 11 saves persist encounter grouping and convoy role, including timer-spawned convoy escorts.

Driving planning runs about 10 Hz beyond 80 m and up to 20 Hz nearby, reusing NWH controls between
decisions. Travelling obstacle checks do not scan collider ancestors for health/weapon FSMs. Mounted
combat eligibility no longer recalculates the driving waypoint. Sight uses fixed hit buffers, and chassis
collision lists are cached once construction finishes. The standalone minimap, Fit camps, player-follow
viewport and its drawing loop have been removed. Only the game's open debug map draws routes, with
a reusable texture updated at most 2.5 times per second.

With NPC Overhaul 1.9.13, marked crew and fresh dismounted raiders use a simple player sight check,
basic attack/hold/chase, infrequent thoughts and baked-map waypoints (including low-step handling).
They skip engine/gunshot investigations, cover/flanking searches, neighbour/friend fans and idle routines.
Turning the option off restores ordinary NPC Overhaul behaviour. Without NPC Overhaul, it has no effect.

2.9.1 uses inexpensive road-influenced travel. On generation, the mod traces the actual road terrain-layer inputs
through MapMagic's portals to both final road spline sources. It samples and clips those curves before
the game clears generation products, then stores them in MapMagic's stable local world frame.
The cache lives in `BepInEx/config/Apocapatrol.roads/<world-recipe>/<tile>.roads`; seed, generator wiring,
terrain dimensions, generator settings and MapMagic build identity separate different recipes.
Files survive terrain unloading, restarts and the game's kilometre origin jumps.

Existing saves reconstruct terrain during normal loading, allowing road discovery to capture previously visited
areas. Coarse samples are taken about every 64 m of spline control-polygon length and reduced to at most
192 hints per tile. Old dense road cache files remain readable and receive the same bounded hint summary.
Only files along requested journeys are loaded, through a serial background IO queue. The game thread no
longer loads the whole road directory or reads terrain paint/height arrays to reconstruct missing roads.

Each journey receives 7–17 cached waypoints, with a maximum of 32 when skirting known camp footprints.
Interior points are attracted toward nearby cached road hints within `MaxRoadConnectorMetres`.
Missing hints use deterministic snake-like points immediately; a missing connection never prevents departure.
A single background worker builds these arrays. There is no road connectivity graph or exact road prediction.
Existing straight routes and older dense routes are replaced from the group's current position, including
physical groups that are travelling. Pursuing patrols wait until they resume travel. Cached fallback geometry
is retained until useful road hints arrive. No physical car is teleported during route adoption or catch-up.

Drivers use cached cumulative distances, a nearby waypoint window and precomputed corner speeds.
They use a speed-dependent target along the route and brake before sharp bends. Escorts and
materialized convoy vehicles are positioned by distance along the curve; the cargo truck leads regardless
of template roster order. Debug routes are red for patrols and yellow for convoys, and display the same
sparse waypoint chain used by simulation and driving. The game debug map uses a cached texture refreshed
at most 2.5 times a second. Full road geometry and the standalone minimap are not drawn.

2.8.1 fixes the drifting overview and the game's origin shifts. Traffic records now use MapMagic-local coordinates,
so kilometre-scale NWH FloatingOrigin jumps and loaded world offsets do not move camps, routes or simulated groups.
The standalone overview introduced in that version has been removed in 2.10.0.

Opt-in prototype of persistent patrol and convoy traffic. The normal automatic spawn clock is replaced while this mode is enabled.

## Try it

Install the prototype DLL in `BepInEx/plugins/Apocapatrol`, keeping the existing assets and templates. Restart the game. In `BepInEx/config/com.denis.apocalypter.apocapatrol.cfg`, set:

```ini
[World traffic prototype]
Enabled = true
DebugMap = true
FollowRoads = true
```

The package's `prototype-settings.ini` is an example section to merge into your existing config; it is not a replacement for your whole configuration. Normal installations keep `Enabled = false` by default. NPC Overhaul is optional: its live structure bounds, baked floor anchors, daylight curve and engine noise curve are used when available, through a read-only adapter. No NPC Overhaul DLL change is needed.

Travel begins once at least two placed `Camp_<number>` instances have been discovered. Camp coordinates remain known after their terrain chunks unload and are saved with the selected game slot. A camp means a placed camp prefab in this first prototype; faction ownership, defeated-base state and occupation are not tracked yet.

## Behaviour

- A group departs a known camp's exterior apron and follows a route to another known camp's exterior apron. Its roster and cargo are chosen at departure, using existing progression gates, group weights, templates and size scaling. Routes avoid known camp footprints.
- Distant groups advance as small records without spawning vehicles. Near the player they build the existing real vehicles, crews, trailers and loot. At greater distance the physical representation unloads and the same record continues from its actual position. You can follow it and encounter it again.
- Vehicle condition, fluids, crew health and turret casualties survive unloading. Taken vehicles, wrecks, dismounted crews and vehicles with missing parts or detached trailers retire from the simulated roster and remain physical salvage under existing cleanup rules.
- Convoy cargo drivers keep their destination goal and do not pursue or fire at the player. Escort drivers engage when the player is within `ConvoyGuardRange` of the lead convoy position (300 m by default), then return to the column when the player leaves that range. Mounted/passenger guards can fight in that proximity; existing weapon range, fire arc and vanilla target acquisition still apply.
- Patrols initially travel. Sight requires a forward field of view and line of sight; range changes with daylight. Running engine noise scales with horsepower and is reduced at idle. With NPC Overhaul loaded, its configured noise/daylight curves are reused. After losing sight/sound, a patrol searches the last known position briefly and then returns to its route. Guard-mode detection is evaluated on physical groups within the load radius.
- Attack runs alternate direct interception and wider left/right approaches, distributed by roster slot. Existing obstacle feelers, reverse recovery and rollover protection remain active. Camp approaches use named entrances or baked floor anchors and independently check a vehicle corridor. A narrow or blocked approach holds the car outside rather than pushing into a wall.
- At arrival, a group parks while the player is near the group. The unattended destination timer resets on proximity. After its grace expires away from the player, the record is removed and population may be replenished.
- Route lines and group labels appear only on the game's `MapCheat_Camera` / `RawImage_Map` debug map while it is open.
- World journeys share Apocapatrol's embedded game-save data and sidecar. Schema 11 reads schemas 1–10; old-schema compatibility is covered by native tests. Older Apocapatrol versions cannot read schema 11.

## Main settings

| Setting | Default | Meaning |
| --- | ---: | --- |
| FollowRoads | true | Influence sparse routes with lazily loaded road hints |
| MaxRoadConnectorMetres | 350 m | Maximum attraction distance from a waypoint to a road hint |
| MaxGroups | 24 | Total simulated and live group budget |
| MaxRealCars | 24 | Physical vehicle budget including pending builds |
| LoadRange | 650 m | Distance to materialize a group |
| UnloadRange | 1000 m | All members must be beyond this; at least LoadRange + 100 |
| DestinationGraceSeconds | 180 s | Continuous unattended time before removal |
| PatrolSpeedKmh | 55 | Simulated and desired cruising speed |
| ConvoySpeedKmh | 38 | Simulated and desired cruising speed |
| ConvoyGuardRange | 300 m | Escort engagement radius around convoy |
| PatrolDaySight / PatrolNightSight | 300 / 100 m | Sight range without NPC Overhaul's daylight interpolation |
| EngineMinRange / EngineMaxRange | 100 / 350 m | Fallback hearing for 50–500 HP engines |
| PatrolMemorySeconds | 20 s | Last-known search before resuming travel |

The General Enabled switch prevents new departures while existing journeys continue. Fixed role caps count
arrived groups as well as travelling groups, avoiding replenishment growth while the player holds a destination.

## Verification and limits

Run `verification/Run.ps1 -TrafficOnly` for native Unity tests covering travel, arrival grace, density, detection decisions, convoy roles, route detours, real production vehicle construction, unload, catch-up/reload, damage/health retention, stripped-car protection, and save compatibility. Run `verification/Run.ps1 -WarRig` for the existing trailer/vehicle regression suite.

Origin tests invoke the actual NWH FloatingOrigin Jump method twice with kilometre-scale X/Z and vertical shifts.
They check physical and simulated alignment, stable camp identities, map pixels, load distances, driving goals,
attack caches and save reloads with a different MapMagic offset. Traffic payload version 2 stores stable positions;
version 1 is converted once using the loaded game's MapMagic offset. That migration cannot reconstruct historical
coordinate errors already written by 2.8.0 across several origin jumps; newly discovered camps and journeys use the corrected frame.

Road discovery covers generated tiles and cached tiles, not unseen distant terrain. Sparse routes are
influenced by nearby roads; they do not promise to trace every bend, prove road connectivity or avoid every
remote obstacle. Missing road data uses the stable snake fallback. This deliberately favours performance.
Physical cars use the existing terrain/obstacle feelers. Native tests cover the actual seeded road recipe,
bounded hints, sparse/fallback routes, physical route adoption, curved map rasterization, persistence and lifecycle with physics frozen; driving quality, camp
entrances and debug-map alignment still need a normal in-game playtest. Entrance access reads NPC
Overhaul internals by reflection, so changes to that mod may fall back to scene discovery. Groups whose
terrain is not loaded or whose template dependency is unavailable wait before retrying materialization.

For playtesting: discover two camps, watch a red patrol or yellow convoy line, intercept it, leave beyond UnloadRange and catch up again. Check a convoy truck continues past you while escorts engage. Compare patrol detection at day/night and with weak/strong running engines, then visit its destination and verify it stays until you leave.

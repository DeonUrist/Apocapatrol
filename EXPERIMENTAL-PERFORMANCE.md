# Experimental performance changes — 2.11.0

2.11.0 adds cached coarse elevation to the existing single route worker. Routes still contain at most
17 terrain waypoints (the older camp-detour fallback caps at 32). No terrain/physics queries run while
driving to rank mountains. Heights use 65 × 65 samples per cached tile; disk reads remain lazy and off
the game thread. Local population is capped by loaded, uncleared camps as well as the user maxima.

NPC Overhaul 1.9.14 corrects simple AI to an explicit combat handoff: no seated AI; after dismount,
persistently chase the real player using baked-map waypoints, shoot with line of sight, and skip
ghosts, target acquisition, forgetting, sound investigations and idle patrols. Thoughts remain paced
at 0.6 seconds or slower with distance. Mounted guns and vehicle driving retain Apocapatrol control.

Implemented: five cars per event, six shared cars for overlapping events (both configurable), reservation
accounting during asynchronous builds, truck priority, fixed patrol/convoy counts, lower-frequency driver
decisions, cached collider lists, allocation-free sight buffers, cheaper travelling obstacle classification,
and game-debug-map-only route drawing. No standalone minimap remains.

Convoy obstacle recovery retries indefinitely without stuck/dry-tank bailout. Passenger takeover prefers
the driver's seat for convoys. Normal combat deaths and vehicle failures remain possible. Event identity
and convoy roles are retained in schema 11; earlier saves remain readable.

NPC Overhaul 1.9.13 adds an opt-in per-NPC API used only by Apocapatrol's marked crew and dismounted
raiders. Simple AI still navigates baked maps and fights, with player-only sight checks, slower thoughts,
one short obstacle check, no sound/engine investigations, no idle patrol, and no cover/friend-fan searches.
It can be toggled back to normal NPC Overhaul AI. NPC Overhaul is optional.

Further candidates, suggested rather than enabled automatically:

1. Limit human roof/trailer turrets per encounter. A car cap alone can still leave many animated gunmen,
   each with combat FSMs, rig updates, collision ignores and audio. This is likely the next valuable cap.
2. Reduce the materialization range (650 m currently) and unload range together. This lowers the number
   of NWH cars, wheel/suspension physics, crew rigs and audio sources active around the player.
3. Stagger physical construction more strongly or allow only one vehicle construction at a time. Each
   vehicle is already spread across frames, but overlapping builders can still instantiate parts in the same frame.
4. Add distance-based rendering/animation detail to mounted crew. Keep gameplay/weapon checks running
   while reducing expensive skeletal and visual updates for cars far from view.

Driving simplification cannot eliminate the underlying Unity/NWH physics cost of every active chassis,
wheel, trailer and collision. The event and actor budgets should be measured in a normal gameplay profile.

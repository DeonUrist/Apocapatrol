# Road routing performance review — 2.9.1

The 2.9.0 road planner rebuilt and searched a detailed graph for every requested journey on the game
thread. Population and route-refresh loops could request many routes in one frame. Driving repeatedly
walked entire waypoint arrays, and each GUI pass could rotate and draw thousands of individual textures.
World activation also read every road cache file synchronously; fallback recovery read complete terrain
paint and height arrays. Existing physical groups were excluded from route adoption, leaving their old
two-point map lines straight.

2.9.1 replaces graph routing with 7–17 sparse road-influenced points, capped at 32 when avoiding known
camps. A single worker constructs route arrays once. Missing hints produce a deterministic snake route.
Generated road samples and loaded dense tiles are compacted to at most 192 hints. Relevant cache files
load lazily on a serial background queue; no terrain paint reconstruction or full road graph remains.
Existing physical and simulated groups adopt the cached route without teleporting. Driving and both maps
use that same array. Map lines are rasterized into one reusable texture per map at most five times a second.
Driving reuses cumulative distances and corner speeds, and scans only nearby legs. NPC camp processing
is spread across frames instead of repeating incomplete bakes in a burst.

Native Unity measurements using the installed cache (9 tiles, 8,839 original points):

| Measurement | 2.9.0 | 2.9.1 |
| --- | ---: | ---: |
| Synchronous route calculation, warm | 13–22 ms/request | 0.7–1.1 ms/request |
| Production main-thread route request, warm | full synchronous calculation | 0.03–0.09 ms/request |
| First route request | 32 ms | 4.3 ms (initialization) |
| 1,000 driving targets on a 20,000-point legacy route | 3,279 ms | 47 ms |

The synchronous helper is used by verification; production requests use the background worker.
These measurements isolate routing work with native physics frozen. A normal playtest is still needed
to assess total frame time, physical driving and other installed mods.

Regression checks cover curved map rasterization, sparse hinted routes, stable snake fallback, fixed
waypoint budgets, physical patrol route adoption, actual MapMagic road capture, old cache compatibility,
lazy file loading, persistence, real unload/reload, vehicle state retention and actual NWH origin jumps.
Traffic JSON now preserves float coordinates exactly through the existing JSON writer by encoding them
as doubles, preventing rounding of curved positions at large world coordinates.

Reproduce with `verification/Run.ps1 -RoadPerfOnly`, `-TrafficOnly`, and `-WarRig`.

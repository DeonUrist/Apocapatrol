# Merchant bus prototype — 2.14.4

For roof and trailer turrets, unchecked **Standing** means the original crouched animation;
checked means standing. This preserves the configured anchor positions and merchant layout.

Open the Apocapatrol editor and select **Merchants → Coyote merchant**. Its roster uses **Coyote Trader
bus**, based on the supplied Coyote_Trader/Trader_Bus vehicle layout. Select exactly one of the
vanilla **General merchant**, **Mechanic** or **Organic mechanic** types. Selecting another type
replaces the selection. Older templates and buses with multiple types keep their first valid type.
Enable the existing debug spawn option to use **Spawn template** for parked layout testing. Its
driver, passengers and turrets have inactive AI; the engine stays off. Group **SPAWN** and **S** create
a new simulated passing route, subject to the configured population cap and unique shop-type limit.
The bus crosses north–south or east–west, starting and ending outside the visible native debug
map with approximately 100 m of padding beyond its actual boundary. It passes near the player. Sparse
waypoints reuse cached road/elevation hints. Nearby buses load physically, then return to simulation
when the player leaves; stock and remaining turret HP persist.

The bus merchant and other bus passengers have a separate relaxed seated pose. Press **Insert**
near the passenger for the live arm/leg editor, and **Num5** to save the shared preset. The key
is bindable under **Debug**. See [BUS-PASSENGER-POSE.md](BUS-PASSENGER-POSE.md) for controls.

Under **Experimental**, **Max merchants** is 0–3 (default 3). **Merchant spawn chance** is 0–100%
(default 100). **Merchant check interval (minutes)** is 1–60 (default 5). The first automatic
check waits one interval, and all population changes/replacements wait for subsequent checks.
At each check, max × chance sets the expected number of merchant routes: whole slots are guaranteed
and the fractional slot is rolled. With max 3, 50% chooses 1 or 2 equally; 11% rolls a 33% chance
of 1. At 100%, each check refills all missing slots, subject to eligible enabled shop types and the
overall traffic cap. At 0%, merchant route SPAWN is disabled, pending routes are canceled and virtual
routes are removed at the next check. Excess physically loaded shops finish their visit before
removal; setup buses remain available for editing. The old minimum/maximum interval settings are
replaced. Only one General, Mechanic or Organic merchant
can exist, including parked setup buses and pending routes. Merchant departures require world
traffic enabled, but do not require landmarks or the legacy convoy spawn timer. Existing setups
receive missing default shop-type groups once; later group deletions are respected.

**Alt+Q within 30 m** or your car's **horn within 100 m** stops a loaded merchant for five minutes.
It resumes afterward, or immediately returns to virtual travel when unloaded by moving away.
Repeated signals during a stop do not extend it. The driver follows its destination and never
pursues the player. Merchant guards can fight nearby loaded raiders; crossing merchant/raider map
routes never creates a simulated battle or skull marker.

Human merchant turrets receive a hidden **×4 maximum HP** bonus. This does not affect driver or
passenger HP, or damage. Saving, unloading and catching up preserves wounds without reapplying the
bonus. Dead turrets stay dead.

The selected type has its native selling trigger/crate, wanted-items board, price list and four
buyable sale items from that vanilla trader's stock list. General merchants also retain the native
liquid-selling box. Use the game's ordinary purchase and selling interactions. Sold-out items are
not replenished by loading the game. Normal prices and wanted-item lists persist in the patrol save
sidecar, alongside stock identities. Trading closes while the player is hostile to the Coyotes.

Wanted-list names and prices are calculated using hidden, immobile metadata probes. Those probes
cannot unpack or spawn items and are removed after the board is initialized; they do not drop
bedrolls or other loose objects above the vehicle.

Install `ApocapatrolMerchantSpotter.dll` alongside Apocapatrol. Press **Numpad `*`** to enable/disable its
editor; it selects the closest merchant vehicle within 80 m. Turn off the existing TurretSpotEditor
while using it because both use the numpad.

Rebind it in **MODS → Merchant Object Spotter → Settings → Debug → ToggleKey**. Changes apply
immediately. **Enabled** opens the editor without a shortcut. The editor survives loading a game
and always shows its status when enabled, even if no merchant bus is nearby. An ordinary spawned
bus has no shop objects; use the Merchants tab to spawn it.

The toggle is configurable as `ToggleKey` in
`BepInEx/config/com.denis.apocalypter.merchantspotter.cfg`. Version 1.0.0 used F10, which conflicts
with the game's God Hands shortcut; change an existing F10 binding to `NumpadMultiply`.

| Key | Action |
| --- | --- |
| Numpad `/` | Cycle Box, Board, Names, Prices, Sale items |
| Numpad `0` | Next object of that type; includes each enabled shop type |
| Numpad `8` / `2` | Move forward / backward in the bus's frame |
| Numpad `4` / `6` | Move left / right |
| Numpad `7` / `9` | Move down / up |
| Ctrl + `4` / `6` | Rotate yaw |
| Ctrl + `7` / `9` | Rotate pitch |
| Ctrl + `8` / `2` | Rotate roll |
| Shift | Multiply movement/rotation by five |
| Numpad `+` / `-` | Double / halve movement step |
| Numpad `5` | Save all current in-car object positions and rotations |

Yellow marks the selected object; green shows its forward direction. Moving a board also moves its
labels, and moving a selling box moves the native trigger with the crate. Sale-item locations move
their displayed stock. Layouts are relative to the bus, so world origin shifts do not change them.
Layouts are stored in `BepInEx/plugins/Apocapatrol/MerchantLayouts.json`, keyed by vehicle template and
shared object IDs. General, Mechanic and Organic use identical box, board, label and sale-item
positions. Older type-specific layouts migrate automatically, preferring the existing General
positions. Saving a type without a liquid box preserves its saved position for General. The file
is reapplied to new and restored buses, and existing buses using that template. Initial positions are starting points for
numpad setup. The current nine configured merchant object positions and both standing Gunnar
turret positions ship as defaults; later saved player edits take priority over embedded layouts.

Faction can be selected in the vehicle's **E** editor or the template recorder. Only that faction's
crew choices are offered, including trailer turrets. Changing faction clears incompatible occupants
so you can choose replacements. Changing a group's faction removes incompatible vehicles from that
group's roster, while keeping their template files. Selecting a patrol/convoy/merchant group filters the right-hand list
to compatible vehicle templates; incompatible entries are hidden. Existing incompatible JSON
roster entries are ignored during spawning and never create mixed crews.

Merchants are yellow on the **game debug map** only. Hide route type makes them gray and labels them
Distant traffic. The spotter adds object markers/help text, and no extra map.

Native verification exercises all three shop types, wanted boards, stock registration, money
deduction, selling payouts, immediate purchase snapshots, schema-14 persistence, shared layout
save/load and the faction popup. Traffic fixtures cover camp-independent routes, caps, unique
types, shout/horn stops, unloaded HP continuity and exclusion from simulated combat. Physics
fixtures do not measure live terrain driving or firefight balance.

# Apocapatrol (prototype 0.2.2)

BepInEx 5 plugin for **Apocalypter** — groundwork for AI-driven raider cars. Right now it is a debug builder:
press `SpawnKey` (F7) in game and a complete car is assembled in front of you with the game's own part-attach recipe.

Default build: **PipeRat** frame, 4 × `small_wheel_1`, `1.2L I4 59HP 87Nm Gasoline` engine, `radiator_small`, `steeringwheel_7`,
`poloska_seat_front_homemade` on the driver seat, handbrake released, and a ragdolled `Scraffa_Dead` on the driver position (`[Driver]`).
Every part is configurable by prefab name or in-game item name (`[Build]`; edit live in the Apocasetter Mods menu).

What happens on the key:
1. The frame prefab is instantiated `SpawnDistance` m ahead, named and registered like a vanilla spawn (`ArrayList_Cars`).
2. Each part is instantiated at its hinge (`hinge_wheel_FL/FR/RL/RR`, `hinge_engine`, `hinge_radiator`, `hinge_steeringwheel`) and attached
   exactly like the game's `vehPart_Attach` FSM does: Rigidbody destroyed, tag `vehPart`, layer 8, parented with local pos/rot reset;
   parts are registered in `ArrayList_Items` so the car saves like a player-built one.
3. `FillFuel`: the tank (`Fuel/LiquidAmount`) is filled to capacity. `ReleaseHandbrake`: the handbrake lever FSM is stepped to `HandbrakeOff`.
   `[Driver] Driver`: the ragdoll prefab is put on the car's `sitPos` (+ `OffsetX/Y/Z`), its root Rigidbody made kinematic and parented
   to the seat so it rides along, bones keep flopping on their CharacterJoints, collisions with the car are ignored.
4. `StartEngine`: the frame's `START` key FSM is stepped through `Ignition` → `Start`; if NWH reports the engine not running after 3 s,
   `NwhStartFallback` calls `powertrain.engine.StartEngine()` directly.
5. `DriveTestSeconds` > 0: throttle is pushed with nobody inside — checks that the NWH vehicle drives without a player (needed for AI cars).
   The car's own `DriveTrigger/INPUT` FSMs copy the keyboard axes into `input.*` every frame, so they are paused while the mod drives and
   restored afterwards (or as soon as the player gets in, which ends the test). The automatic gearbox is shifted out of Neutral into 1st
   (`input.ShiftInto = 1`, it stays in N otherwise). At the end the car is braked and the handbrake set.

Everything is logged to `BepInEx\LogOutput.log` (`VerboseLog` adds prefab lookups, hinge FSM states, speeds).

Build: `build.sh` (mcs against the game's `Managed` + `BepInEx\core`) or `dotnet build` (deploys to `BepInEx\plugins`).

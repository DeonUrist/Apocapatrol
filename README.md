# Apocapatrol (prototype 0.4.0)

BepInEx 5 plugin for **Apocalypter** — groundwork for AI-driven raider cars. Right now it is a debug builder:
press `SpawnKey` (F7) in game and a complete car is assembled in front of you with the game's own part-attach recipe.

Default build: **PipeRat** frame, 4 × `small_wheel_1`, `1.2L I4 59HP 87Nm Gasoline` engine, `radiator_small`, `steeringwheel_7`,
`poloska_seat_front_homemade` on the driver seat, handbrake released (to be able to drive), and a live **Scraffa** at the wheel (`[Driver]`).

## Seated pose (Pose.cs)
The game has no sit animation, so after the Animator has posed the driver each frame the mod swings the Mixamo bones into a seat:
thighs forward (`[Pose] ThighAngle`), shins back down (`KneeAngle`), upper arms forward (`ArmAngle`), forearms up (`ElbowAngle`), and
moves the body so the hips land on `sitPos` + `[Driver] OffsetX/Y/Z`. All values are live, so tune them in the Apocasetter menu while looking at him.

## The driver (Crew.cs)
A live enemy prefab is put on the car's `sitPos`: its AI and body-mover FSMs (`DisabledFsms`: Movement, Unstuck, Rotate, Detection, Attack,
ranged attack, Codex, sounds) are switched off before they start and kept off every frame; `Health`, `Damage` and the `Bodypart` colliders stay
vanilla, so it can be shot and killed like any enemy (its own death flow drops the carcass). Root Rigidbody kinematic, parented to the seat,
collisions with the car ignored. While it lives the player cannot take the car: the `DriveTrigger` enter collider and the `Drive` FSM are off.
After `DriveDelaySeconds` (and once the engine runs) it drives: shifts into 1st and holds `DriveThrottle`, steering straight for now.
When it dies: with `StuckPedalChance` % its leg stays on the gas (throttle kept, car keeps going); otherwise the gas is simply released and the car rolls out
on engine braking and drag until it stands still. Either way the seat is free again and the player can drive the car. A `*_Dead` prefab as `Driver` gives the old ragdoll
passenger instead; an empty `Driver` falls back to the `[Build]` drive test.
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

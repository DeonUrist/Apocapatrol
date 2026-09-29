# Apocapatrol (prototype 0.15.0)

BepInEx 5 plugin for **Apocalypter** — groundwork for AI-driven raider cars. Right now it is a debug builder:
press `SpawnKey` (F7) in game and a complete car is assembled in front of you with the game's own part-attach recipe.

Default build: **PipeRat** frame, 4 × `small_wheel_1`, `1.2L I4 59HP 87Nm Gasoline` engine, `radiator_small`, `steeringwheel_7`,
`poloska_seat_front_homemade` on both front seats (`Seat`, `PassengerSeat`), handbrake released (to be able to drive), a live **Scraffa** at the
wheel and a **Flexa** on the passenger seat (`[Driver] Driver`, `[Passenger] Passenger`). The passenger has no AI either: it sits (same pose and
offsets, at sitPos shifted by the distance between the two seat hinges) until killed; its death changes nothing about the driving.

## Car templates - the park (Templates.cs)
The park is hardcoded: `CarTemplate.Park[]` lists each car as name, body, wheel, engine, radiator, steering wheel, driver seat, passenger seat,
driver, passenger (prefab names or in-game item names, empty = no part) and `ramsTargets`: what the driving AI runs into on purpose - `None`
(rams nothing: the player, their car, creatures and cars are all obstacles; it makes drive-by runs `DriveByOffset` m beside the player instead),
`Pedestrians` (runs over the player on foot and creatures, avoids cars - drive-bys on a driving player) or `Cars` (rams the player's car and
other vehicles too, and pedestrians). All small cars in the park are `Pedestrians`. A car built from the `[Build]` fields uses `[AI] RamTargets`.
**Template spawner**: `F8` (`[Debug] TemplateSpawnerKey`) opens a window listing the park; click a car to build it in front of you (uses
Apocasetter's theme and input blocker when installed, a plain window otherwise). `[Build] Template` names the one the spawn key builds (empty = the
`[Build]`/`[Driver]`/`[Passenger]` fields as before). To add a car: set it up in the fields, press `F9` (`TemplateKey`) - the log prints the
matching `new CarTemplate(...)` line named `[Build] TemplateName` - and paste it into `Park`. Currently: **PipeRat_Basic** (PipeRat, 4× small_wheel_1,
1.2L I4 engine, radiator_small, steeringwheel_7, poloska homemade seats, Scraffa driving, Sprokka passenger), **PipeRat_Advanced** (PipeRat,
4× small_wheel_2, 2.8L V6 engine, Medium Radiator, steeringwheel_7, poloska homemade seats, Spanna driving, Lugnut passenger), **Poloska_Basic**
(PipeRat_Basic on a Poloska frame, Boltjaw passenger), **Poloska_Advanced** (Poloska frame, the 2.8L V6, otherwise PipeRat_Basic: Scraffa driving,
Sprokka passenger), **TinyTyrant_Basic** / **TinyTyrant_Advanced** (PipeRat_Basic / PipeRat_Advanced on a TinyTyrant frame), **Junker_Basic** / **Junker_Advanced** (Junker, small_wheel_1, 2.3L I4 engine, Medium
Radiator, Sprokka driving, Lugnut / Flexa passenger) - all eight ram Pedestrians. A convoy spawner will draw from the park.

## Seated pose (Pose.cs)
The game has no sit animation, so after the Animator has posed an occupant each frame the mod overrides the Mixamo bones into a seated pose and
moves the hips onto the seat anchor + `[Driver] OffsetX/Y/Z`. The shared `[Pose]` settings control both legs. Arms have independent profiles for
all seven human types: `[Pose.Boltjaw]`, `[Pose.Flexa]`, `[Pose.Lugnut]`, `[Pose.Scrud]`, `[Pose.Sprokka]`, `[Pose.Scraffa]`, and `[Pose.Spanna]`.
Each profile exposes `Vertical`, `Horizontal`, and `Rotation` for the left and right `Arm`, `Elbow`, and `Hand` (18 live controls per human).
Positive vertical swings forward/up, positive horizontal turns toward the occupant's right, and rotation twists around the limb axis. All values
are live in Apocasetter, so each model's hands can be aligned independently while the game is running.

The tuned profiles, shared pose, and seat offsets are built-in defaults and hidden normally. To tune them again, uncomment
`PoseConfiguration = true` in the generated `[General]` comments and restart; `[Pose]`, `[Pose.<human>]`, and the driver offsets will be generated.
The occupant FSM suppression list is hardcoded and is never exposed in configuration.

The five ranged profiles additionally expose `WeaponPositionX/Y/Z` (metres in left-hand local space) and `WeaponRotationX/Y/Z` (degrees). These
offset every weapon variant belonging to that human from its original prefab transform and are also applied live after animation. The weapon's
muzzle flash and other child effects move with it; targeting raycasts and firing logic are deliberately not repositioned.

## The driver (Crew.cs)
A live enemy prefab is put on the car's `sitPos`: its AI and body-mover FSMs (`DisabledFsms`: Movement, Unstuck, Rotate, Detection, Attack,
ranged attack, Codex, sounds) are switched off before they start and kept off every frame; `Health`, `Damage` and the `Bodypart` colliders stay
vanilla, so it can be shot and killed like any enemy (its own death flow drops the carcass). Root Rigidbody kinematic, parented to the seat,
collisions with the car ignored. While it lives the player cannot take the car: the `DriveTrigger` enter collider and the `Drive` FSM are off.
After `DriveDelaySeconds` (and once the engine runs; 0 = at once, -1 = never) it drives: the driving AI below takes the wheel (`[AI] Enabled`),
or with the AI off it shifts into 1st and holds `DriveThrottle` straight ahead. Inputs are written on the physics step (FixedUpdate), where NWH reads them.
When it dies: with `StuckPedalChance` % its leg stays on the gas (throttle kept, car keeps going); otherwise the gas is simply released and the car rolls out
on engine braking and drag until it stands still. Either way the seat is free again and the player can drive the car. A `*_Dead` prefab as `Driver` gives the old ragdoll
passenger instead; an empty `Driver` falls back to the `[Build]` drive test.
Every part is configurable by prefab name or in-game item name (`[Build]`; edit live in the Apocasetter Mods menu).

## The driving AI (Pilot.cs)
Hit and run. The target is the player - the player's car while they drive, the player on foot otherwise; the car behaves the same either way.
- **Charge**: full `[AI] Throttle` (eased off only once rolling and pointing well away), aim at an intercept point (`LeadTime` seconds ahead of the target's movement, re-taken every `CommitSeconds`) and steer toward it.
  The wheel turns at `SteerRate` (full-lock units per second) and is limited at speed (`MaxSteerAtSpeed`), so the car sweeps toward the player
  in a wide arc instead of pivoting onto them; above `TurnSafeSpeed` with the target far off the nose it lifts off and brakes lightly.
- **Overshoot**: once the target is passed (behind the car, within `PassWidth` of its track) or rammed (collision with the player / their car),
  the car keeps going for `RunOutMeters` (at most `RunOutMaxSeconds`), then
- **Turnaround**: full lock toward the target until it faces them again (back to Charge). Too slow and too far off for two seconds = a three-point turn.
- **Recover**: a frontal hit (steep contact normal - ground bumps under the nose do not count) against anything that is not the target, not moving for
  `StuckSeconds` in a forward state, or shoving slowly against an obstacle (a parked car, a wall) for a second, reverses for
  `ReverseSeconds` with the wheels turned so the nose swings away from the obstacle (or toward the target), then charges again. While reversing, three rear feelers and rear collisions end the reverse early and the car goes forward instead. Recoveries
  within `RecoverWindow` of each other reverse longer and, from the third on, in a random direction; after `MaxRecovers` the car gives up for
  `WaitSeconds`. How NWH reverses (gear -1 + throttle, or brake input at standstill) is detected on the first recovery and logged.
- **Feelers**: five rays from just outside the bumper (`FrontOffset`), the centre one `FeelerRange` + `FeelerSpeedFactor` × speed long, the side
  ones at ±22° / ±48° and shorter, plus a probe for missing ground ahead (cliffs). A hit steers away (`AvoidGain`, the centre ray toward the freer
  side), throttles down and brakes when it is close. Surfaces flatter than `MaxSlopeDeg` are ground, steeper ones (rocks, walls, wrecks, other
  cars) are obstacles. NOT obstacles: the player and the player's car, creatures/NPCs (anything with a Health/Detection FSM), loose items lighter
  than `IgnoreMassBelow` kg - those get rammed or run over. Within `RamDistance` of the target with it roughly ahead and nothing closer in the way, avoidance is off entirely (a parked car between them is
  still avoided).
- Beyond `GiveUpDistance` from the player the car coasts (Idle) until they come closer. On its side or roof it just waits. `InvertSteering` flips
  the steering sign should the car turn away from the target.
`[Debug] AiOverlay` draws one line per AI car on screen (state and why, speed, gear, target distance/angle, steering, pedals, feeler hit
distances); the verbose log has the same every 5 s plus every state change with its reason. Nothing of the AI state is saved: a car restored
while driving starts charging again.

## The passenger (Passenger.cs)
A configured `[Passenger] Passenger` is independent of the driver and appears even if the driver is empty, a ragdoll, or fails to spawn. Its
seat point is the driver's `sitPos` shifted by the distance between the driver and passenger seat hinges. It uses the same seated pose and offsets,
keeps the same AI/body FSMs disabled, and retains vanilla health and death. It never changes driving, throttle, or driver-seat locking.

Boltjaw, Flexa, Lugnut, Scrud, and Sprokka use ranged passenger logic when their expected `RangedAttackWait` FSM and `AttackRaycast_Ranged` child
are present. Scraffa and Spanna are always passive like the driver. For ranged humans, vanilla detection, target selection,
range/line-of-sight checks, weapon, cadence, effects and damage remain active, but `Attack` is allowed only while the vanilla-selected target is
inside the car's front firing arc (`[Passenger] FireArcHalfAngle`, 90 degrees by default). `Movement` remains disabled, so the passenger always
uses exactly the same fixed arms-and-legs pose and offsets as the driver. The root stays locked to the seat; only the three-bone spine chain turns
and pitches that fixed upper-body pose toward the target. Native arm animation is never enabled. The ranged attack's `Sound2` FSM remains active
so weapon shots keep their vanilla audio. Their native close-range `d_melee` branch is redirected to ranged damage and the melee `Damage` FSM stays
disabled, so approaching a seated shooter never makes it switch to melee. `MaxAimPitch`, `AimTurnSpeed`, and `RangedCombat` are live in Apocasetter; outside the arc the passenger
stops firing and smoothly faces forward again.
The ranged prefabs' separate right-hand melee props (`machete`, `old_knife`, or `shiv`) are also kept inactive after PlayMaker updates, preventing
the close-range state from visually drawing a blade without affecting the left-hand firearm, burst cadence, muzzle effects, or firing sound.
Their melee `Damage` and contact `FireDamage` FSM actions are neutralized and `FireDamageCollider` is disabled as well, so native FSMs cannot
re-enable an invisible close-range hit. Projectile damage continues through the separate `Damage Ranged` FSM.

### A shooting driver
With `[Driver] RangedCombat` a ranged human at the wheel gets the same treatment as a ranged passenger (vanilla detection, ranged-only attack
inside `FireArcHalfAngle`, spine aiming) but only in **bursts**: every `FireIntervalMin`..`FireIntervalMax` seconds, once a target is inside the
arc, it fires for `FireBurstSeconds`. Between bursts its Attack FSM is off and it sits in the generic driver pose (`[Pose]` ArmAngle / ElbowAngle /
ArmCloser, hands on the wheel); the per-human `[Pose.<human>]` shooting profile and weapon offsets apply only while it fires. A passenger promoted
to the wheel switches to this mode.

### When the car is stuck for good
When the pilot has reversed out `MaxRecovers` times in a row without getting anywhere, `[Driver] StuckBailChance` % (50) of the time the whole crew
gets out (driver on the left, passenger on its side, `BailDistance` m from their seats, as fresh vanilla mobs with their remaining health) and the
car is left standing, seat free; otherwise the car waits `WaitSeconds` and tries again.

### When the driver dies
A passenger that outlives the driver acts as soon as the car stands still (a stuck pedal is kicked off after `StuckPedalTakeoverSeconds`, 6 s):
with `BailChance` % (25) it **bails out** - it is replaced by a fresh instance of its prefab `BailDistance` m beside its seat, stood on the ground, fully vanilla AI, registered like a
spawned enemy (so the game saves it), with the health it had left; otherwise it **takes the wheel**: the dead driver's carcass is first thrown out to the left by physics (`EjectSpeed`; its colliders ignore the car and the new
driver, joints to the car are cut) and the passenger climbs over only once it is `EjectDistance` from the seat - if it has not got there after 3 s
(caught on something) it is put down at that distance once.
Occupant-vs-car collision ignores are re-applied at the takeover and once a second while seated: Unity drops an ignore pair whenever a
collider is toggled (weapon props, hitboxes), and a kinematic occupant with one live pair shoves the car around unopposed, then it climbs onto the driver seat, the seat
locks again, and after `TakeoverSeconds` (2) it drives off with the driving AI. If the player got into the car first, the passenger always bails.
The save sidecar follows: a promoted passenger is saved as the driver with an empty passenger seat.

## Saving and loading
Vanilla saves the registered car and its attached parts, but cannot save Apocapatrol's seat pins or autonomous-driving components. When the game
saves, Apocapatrol therefore writes a small versioned binary sidecar for that save slot under `BepInEx/config/Apocapatrol/Saves`. It records every patrol
car's unique clone name, crew types and health, passenger presence, and the driver's waiting/driving/dead state. After vanilla finishes loading the
car, the mod reconstructs its occupants, pose, collision ignores, passenger combat, seat lock, and driving controller. A car that was already being
driven restarts its engine if necessary and resumes immediately; a waiting driver retains elapsed delay. Dead occupants remain absent.

Sidecars are written atomically and checked against the save slot, world seed, vehicle name/body and saved position. A missing, corrupt or mismatched
sidecar is logged and ignored without changing the game save. Persistence begins with saves made by version 0.8.0 or later; older saves contain no
reliable patrol marker and are intentionally not guessed. `[Driver] RegisterDriver` is deprecated and ignored because vanilla item registration
would restore enemies loose rather than seated.

What happens on the key:
1. The frame prefab is instantiated `SpawnDistance` m ahead, named and registered like a vanilla spawn (`ArrayList_Cars`).
2. Each part is instantiated at its hinge (`hinge_wheel_FL/FR/RL/RR`, `hinge_engine`, `hinge_radiator`, `hinge_steeringwheel`) and attached
   exactly like the game's `vehPart_Attach` FSM does: Rigidbody destroyed, tag `vehPart`, layer 8, parented with local pos/rot reset;
   parts are registered in `ArrayList_Items` so the car saves like a player-built one.
3. `FillFuel`: the tank (`Fuel/LiquidAmount`) is filled to capacity. `ReleaseHandbrake`: the handbrake lever FSM is stepped to `HandbrakeOff`.
   `[Driver] Driver` is put on the car's `sitPos`; `[Passenger] Passenger` independently uses the matching front-seat position. Each root Rigidbody is made kinematic and parented
   to the seat so it rides along, bones keep flopping on their CharacterJoints, collisions with the car are ignored.
4. `StartEngine`: the frame's `START` key FSM is stepped through `Ignition` → `Start`; if NWH reports the engine not running after 3 s,
   `NwhStartFallback` calls `powertrain.engine.StartEngine()` directly.
5. `DriveTestSeconds` > 0: throttle is pushed with nobody inside — checks that the NWH vehicle drives without a player (needed for AI cars).
   The car's own `DriveTrigger/INPUT` FSMs copy the keyboard axes into `input.*` every frame, so they are paused while the mod drives and
   restored afterwards (or as soon as the player gets in, which ends the test). The automatic gearbox is shifted out of Neutral into 1st
   (`input.ShiftInto = 1`, it stays in N otherwise). At the end the car is braked and the handbrake set.

Everything is logged to `BepInEx\LogOutput.log` (`VerboseLog` adds prefab lookups, hinge FSM states, speeds).

Build: `build.sh` (mcs against the game's `Managed` + `BepInEx\core`) or `dotnet build` (deploys to `BepInEx\plugins`).

using System;
using System.Collections;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    internal enum CrewPhase { Waiting, Driving, DeadStuck, DeadRolling, Released }

    // Lives on the car. Owns the driver: keeps its AI off, drives while it is alive, and keeps the player out of the
    // driver seat until it dies. On death the gas is either released (car rolls out by itself) or stays stuck.
    internal class Crew : MonoBehaviour
    {
        private GameObject _car, _driver;
        private PlayMakerFSM _health;         // driver [Health]
        private PlayMakerFSM _drive;          // DriveTrigger [Drive]
        private Collider _enterTrigger;       // DriveTrigger SphereCollider = the "press F to drive" trigger
        private Rigidbody _rb;
        private InputControl _ctl;
        private Pilot _pilot;                 // the driving AI while the driver drives and [AI] Enabled
        private static readonly string[] MutedFsms =
            { "Movement", "Unstuck", "Rotate", "Detection", "Attack", "RangedAttackWait", "Damage Ranged", "Codex", "Sound", "Sound2", "Sound3" };

        private float _seated;                // seconds since the driver sat down
        private bool _driving, _dead, _stuck, _done;
        private float _rolling;
        private float _delayOverride = -1f;   // a promoted passenger drives off after TakeoverSeconds instead of DriveDelaySeconds
        private float _deadFor;               // seconds since the driver died
        private bool _paxDecided, _paxBails;  // the surviving passenger's decision (rolled once)
        private float _nextIgnore;            // periodic re-apply of occupant-vs-car collision ignores
        private Patrol.CorpseEject _eject;           // the dead driver being thrown out; the passenger climbs over once it is Done
        private bool _promoting;
        private bool _revived;                // the after-load start-up (handbrake off, ignition) has been run or requested
        private float _nextHandbrakeCheck;
        private float _dryFor;                // seconds the tank has been empty while the car stands
        private bool _outOfFuel;
        private float _nextGuard;             // the AI-mute guard runs a few times a second, not every frame
        private float _nextEngineCheck;       // engine polling while waiting to drive off (a held convoy car asked NWH every frame)
        private PlayMakerFSM _handbrake, _tank;   // cached: handbrake [Handbrake], Fuel [LiquidAmount]
        private FsmFloat _healthVar;          // the driver's Health variable (looked up by name every frame before)
        private PatrolMarker _marker;         // the car's marker (attached after the crew on a fresh build: resolved lazily)
        private PlayMakerFSM[] _partDetach;   // the parts' de_Attach FSMs: off while the crew drives, so the wrench cannot take the wheels off
        private bool _vacated;                // the crew is gone (dead or bailed): the self-destruct has been scheduled

        private PatrolMarker Marker { get { if (_marker == null && _car != null) _marker = _car.GetComponent<PatrolMarker>(); return _marker; } }

        // A car built for a convoy waits for Convoy to release the whole group at once (so the group departs together and
        // the builds do not pile up on one frame); the driver sits with the engine running until then.
        internal bool Hold;

        internal static Crew Attach(GameObject car, GameObject driver)
        {
            var c = car.GetComponent<Crew>() ?? car.AddComponent<Crew>();
            c.Init(car, driver);
            return c;
        }

        private void Init(GameObject car, GameObject driver)
        {
            _car = car; _driver = driver;
            _rb = car.GetComponent<Rigidbody>();
            _health = driver != null ? driver.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Health") : null;
            _healthVar = null;
            _drive = Patrol.FindFsm(car, "DriveTrigger", "Drive");
            _handbrake = Patrol.FindFsm(car, "handbrake", "Handbrake");
            var tank = Patrol.FindChild(car.transform, "Fuel");
            _tank = tank != null ? tank.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "LiquidAmount") : null;
            var dt = Patrol.FindChild(car.transform, "DriveTrigger");
            if (dt != null) _enterTrigger = dt.GetComponents<Collider>().FirstOrDefault(c => c is SphereCollider);
            _ctl = new InputControl(car);
            _partDetach = car.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == "de_Attach").ToArray();

            // nobody else drives while the driver lives: no enter trigger (no F prompt), no Drive FSM (Activate events are ignored).
            // The car's DistanceKinematic FSM is left alone: re-enabling it restarts it in KinematicOn, which freezes a moving car.
            SeatLocked(driver != null);
            Plugin.Verbose("Crew: driver " + (driver != null ? driver.name : "absent") + " seated, " + "drives off in " + (_delayOverride >= 0f ? _delayOverride : 0f) + " s"
                + (_enterTrigger != null ? "" : " (no enter trigger found!)") + (_drive != null ? "" : " (no Drive FSM found!)"));
        }

        internal static Crew Restore(GameObject car, GameObject driver, CrewPhase phase, float seated)
        {
            var c = car.GetComponent<Crew>() ?? car.AddComponent<Crew>();
            c.Init(car, driver);
            c._seated = Mathf.Max(0f, seated);
            // A restored "Driving" car is treated like a seated driver whose engine is not running yet: Revive() (called by the
            // restore) releases the handbrake and runs the ignition, then Update's normal path drives off. Before 0.22.1 it went
            // straight to StartDriving with the handbrake on and the START FSM off - the car just sat there.
            if (phase == CrewPhase.Driving) { }
            else if (phase == CrewPhase.DeadStuck)
            {
                c._dead = true; c._stuck = true; c._driving = true;
                c.SeatLocked(false); c._ctl.Take();
            }
            else if (phase == CrewPhase.DeadRolling)
            {
                c._dead = true; c._driving = true;
                c.SeatLocked(false); c._ctl.Take();
            }
            else if (phase == CrewPhase.Released)
            {
                c._dead = true; c._done = true; c._vacated = true;   // vacated before the save: no retroactive self-destruct
                c.SeatLocked(false);
            }
            return c;
        }

        // After a save load: the car comes back with its handbrake FSM in its start state (on) and the START FSM off.
        // Same start-up as a fresh build, once the car's FSMs are initialized. Safe to call more than once.
        internal void Revive()
        {
            if (_revived || _done || Explode.IsWreck(_car)) return;
            _revived = true;
            StartCoroutine(ReviveRoutine());
        }

        private IEnumerator ReviveRoutine()
        {
            float until = Time.realtimeSinceStartup + 5f;
            PlayMakerFSM hb = null;
            while (Time.realtimeSinceStartup < until)
            {
                if (_car == null) yield break;
                hb = Patrol.FindFsm(_car, "handbrake", "Handbrake");
                var start = Patrol.FindFsm(_car, "START", "Start");
                if (hb != null && hb.Fsm.Initialized && start != null && start.Fsm.Initialized) break;
                yield return null;
            }
            if (_car == null || _done || Explode.IsWreck(_car)) yield break;
            Patrol.Handbrake(_car, false);
            if (!Nwh.EngineRunning(_car)) yield return Patrol.StartUp(_car);
            if (_car == null) yield break;
            Plugin.Verbose("Crew: revived " + _car.name + " after load: engine running " + Nwh.EngineRunning(_car) + ", handbrake released, " + Nwh.Diag(_car));
        }

        internal GameObject Driver { get { return _driver; } }
        internal float SeatedSeconds { get { return _seated; } }
        internal CrewPhase Phase
        {
            get
            {
                if (_done) return CrewPhase.Released;
                if (_dead) return _stuck ? CrewPhase.DeadStuck : CrewPhase.DeadRolling;
                return _driving ? CrewPhase.Driving : CrewPhase.Waiting;
            }
        }

        // Nobody enters and nothing comes off while the driver lives: the enter trigger, the Drive FSM and the parts' de_Attach FSMs
        // (the wrench sends de_Attach to the part; a disabled FSM ignores it) are off together.
        private void SeatLocked(bool locked)
        {
            if (_enterTrigger != null) _enterTrigger.enabled = !locked;
            if (_drive != null) _drive.enabled = !locked;   // restart on re-enable lands in outCar, which has no actions
            if (_partDetach != null)
                foreach (var f in _partDetach)
                    if (f != null && f.enabled == locked) f.enabled = !locked;   // de_Attach's start state is idle: a restart is harmless
        }

        // The car is fully vacated: the crew is dead or got out. With SelfDestructingCars the car explodes shortly after.
        private void Vacated(string why)
        {
            if (_vacated) return;
            _vacated = true;
            Explode.Schedule(this, _car, Marker, why);
        }

        // the car blew up: nothing drives it any more
        internal void OnExploded()
        {
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
            if (_ctl != null && _ctl.Taken) _ctl.Release();
            SeatLocked(false);
            _dead = true; _done = true; _driving = false; _stuck = false;
        }

        // Switch off the AI / body-mover FSMs; called right after Instantiate (before their Start) and every frame as a guard,
        // because Detection/Damage carry EnableFSM actions that could switch them back on.
        internal static void MuteAi(GameObject who, string[] allowedFsms = null)
        {
            if (who == null) return;
            MuteAi(who.GetComponents<PlayMakerFSM>(), allowedFsms);
        }

        internal static void MuteAi(PlayMakerFSM[] fsms, string[] allowedFsms)
        {
            if (fsms == null) return;
            foreach (var f in fsms)
                if (f != null && f.enabled && Array.IndexOf(MutedFsms, f.FsmName) >= 0
                    && (allowedFsms == null || Array.IndexOf(allowedFsms, f.FsmName) < 0)) f.enabled = false;
        }

        // a shooting driver keeps its combat FSMs (its guard gates the Attack FSM); everything else is muted
        private PlayMakerFSM[] _driverFsms; private GameObject _driverFsmsOf;   // the driver's FSMs, fetched once (the guard runs twice a second)
        internal void MuteAi()
        {
            if (_driver == null) return;
            if (_driverFsms == null || _driverFsmsOf != _driver) { _driverFsms = _driver.GetComponents<PlayMakerFSM>(); _driverFsmsOf = _driver; }
            MuteAi(_driverFsms, _driver.GetComponent<PassengerGuard>() != null ? PassengerGuard.CombatFsms : null);
        }

        // healthVar: the FSM's Health variable, resolved once by the caller and kept (GetFsmFloat is a name search every call)
        private static bool Alive(GameObject who, PlayMakerFSM health, ref FsmFloat healthVar)
        {
            if (who == null) return false;                           // Health FSM destroyed it (carcass spawned)
            if (who.transform.parent == null) return false;          // out of the seat somehow
            if (health != null && health.Fsm.Initialized)
            {
                if (healthVar == null) healthVar = health.FsmVariables.GetFsmFloat("Health");
                if (healthVar != null && healthVar.Value <= 0f) return false;
            }
            return true;
        }

        private void Update()
        {
            if (_car == null) { Destroy(this); return; }
            if (Time.timeScale <= 0f) return;
            if (Time.time >= _nextIgnore)
            {
                _nextIgnore = Time.time + 2f;
                var marker = Marker;
                var pax = marker != null ? marker.Passenger : null;
                if ((!_dead && _driver != null) || pax != null)
                {
                    var carCols = _car.GetComponentsInChildren<Collider>(true);   // once for both occupants
                    if (!_dead && _driver != null) Patrol.IgnoreCollisionsIfChanged(_driver, _car, carCols);
                    if (pax != null) Patrol.IgnoreCollisionsIfChanged(pax, _car, carCols);
                }
            }
            if (_dead && PassengerReacts()) return;
            if (_dead && !_vacated && !PlayerInside()) { var mk = Marker; if (mk == null || mk.Passenger == null || !PassengerAlive(mk, mk.Passenger)) Vacated("crew dead"); }
            if (_done) return;

            if (!_dead)
            {
                if (!DriverAlive()) { OnDriverDied(); return; }
                if (Time.time >= _nextGuard) { _nextGuard = Time.time + 0.5f; MuteAi(); }
                _seated += Time.deltaTime;
                float delay = _delayOverride >= 0f ? _delayOverride : 0f;
                if (!_driving && !_outOfFuel && _seated >= delay && Time.time >= _nextEngineCheck)
                {
                    _nextEngineCheck = Time.time + 0.25f;
                    bool running = Nwh.EngineRunning(_car);
                    if (running && !Hold) StartDriving();
                    else if (!running && _seated >= delay + 8f && !_revived) { Plugin.Verbose("Crew: engine of " + _car.name + " never started; reviving"); Revive(); }
                }
                if (Time.time >= _nextHandbrakeCheck)
                {
                    _nextHandbrakeCheck = Time.time + 1f;
                    if (!_outOfFuel && CheckDry()) return;
                    // the player pulled the handbrake on a driven car (or it came back on after a load): the driver lets it go
                    if (_driving && HandbrakeOn()) { Patrol.Handbrake(_car, false); Plugin.Verbose("Crew: driver of " + _car.name + " released the handbrake"); }
                }
                return;
            }

            // Dead driver: preserve a stuck pedal until takeover, or hold zero while the car rolls out (inputs written in FixedUpdate).
            if (PlayerInside()) { Plugin.Verbose("Crew: player took the car after its driver died"); _ctl.Release(); _done = true; return; }
            if (_stuck) return;
            _rolling += Time.deltaTime;
            if (_rb != null && _rb.velocity.magnitude > 0.3f && _rolling < 120f) return;
            Plugin.Verbose("Crew: car rolled to a stop after " + _rolling.ToString("0.0") + " s");
            _ctl.Release();
            _done = true;
        }

        // Once a second: a dry tank while the car stands (driving, or restored/waiting with a dead engine) = stuck for good.
        // StuckBailChance decides whether the crew gets out; otherwise it sits in the dead car. Returns true when it fired.
        private bool CheckDry()
        {
            float fuel = -1f;
            if (_tank != null && _tank.Fsm.Initialized) { var v = _tank.FsmVariables.GetFsmFloat("Liquid"); if (v != null) fuel = v.Value; }
            bool standing = _rb == null || _rb.velocity.magnitude < 1f;
            if (fuel >= 0f && fuel <= 0.05f && standing && _seated > 5f) _dryFor += 1f; else _dryFor = 0f;
            if (_dryFor < 3f) return false;
            _outOfFuel = true;
            Plugin.Verbose("Crew: " + _car.name + " ran out of fuel");
            if (!OnStuck())
            {
                if (_pilot != null) { _pilot.Detach(); _pilot = null; }
                if (_driving) Nwh.SetInput(_car, 0f, 0f, 0f);
                Plugin.Verbose("Crew: the crew stays in the dry car");
            }
            return true;
        }

        private bool HandbrakeOn()
        {
            if (_handbrake == null || !_handbrake.Fsm.Initialized) return false;
            string cur = _handbrake.ActiveStateName;
            return cur == "HandbrakeOn" || cur == "over" || cur == "Sound 2";
        }

        // convoy: every car of the group is released together
        internal void Release() { Hold = false; }

        // NWH samples its input on the physics step, so the inputs are written here, not in Update.
        private void FixedUpdate()
        {
            if (_car == null || _done || Time.timeScale <= 0f) return;
            if (!_dead)
            {
                if (!_driving) return;
                if (_pilot != null) _pilot.Step();
                return;
            }
            Nwh.SetInput(_car, _stuck ? Plugin.AiThrottle.Value : 0f, 0f, 0f);
        }

        private bool DriverAlive() { return Alive(_driver, _health, ref _healthVar); }

        private static bool PassengerAlive(PatrolMarker marker, GameObject pax) { return marker != null && Alive(pax, marker.PassengerHealthFsm, ref marker.PassengerHealthVar); }

        // ------------------------------------------------------------ the surviving passenger

        // After the driver's death a live passenger either takes the wheel or bails out (BailChance %), as soon as the car
        // stands still. A stuck pedal is kicked off after StuckPedalTakeover seconds so the car can stop. If the player took
        // the car first, the passenger always gets out. Returns true while it handled the frame.
        private bool PassengerReacts()
        {
            var marker = Marker;
            var pax = marker != null ? marker.Passenger : null;
            if (pax == null || !PassengerAlive(marker, pax)) return false;
            _deadFor += Time.deltaTime;
            if (!_paxDecided)
            {
                _paxDecided = true;
                _paxBails = UnityEngine.Random.Range(0f, 100f) < Plugin.BailChance.Value;
                Plugin.Verbose("Crew: passenger " + pax.name + " will " + (_paxBails ? "bail out" : "take the wheel") + " once the car stops ("
                    + Plugin.BailChance.Value + " % bail roll)");
            }
            if (_promoting)
            {
                if (_eject != null && !_eject.Done) return true;      // carcass still on its way out
                _promoting = false; _eject = null;
                FinishPromote(pax, marker);
                return true;
            }
            bool playerIn = PlayerInside();
            if (playerIn) _paxBails = true;
            if (_stuck && _deadFor >= Plugin.StuckPedalTakeoverSeconds.Value)
            {
                _stuck = false; _rolling = 0f;
                Nwh.SetInput(_car, 0f, 0f, 0f);
                Plugin.Verbose("Crew: passenger kicked the dead driver's foot off the pedal");
            }
            float speed = _rb != null ? _rb.velocity.magnitude : 0f;
            if (speed > 1f && !playerIn) return false;                  // still rolling: let the roll-out logic run
            if (_stuck) return false;
            if (_paxBails)
            {
                Patrol.BailOut(_car, pax, marker);
                if (!playerIn) Vacated("passenger bailed out");
                return true;
            }
            Promote(pax, marker);
            return true;
        }

        // Step 1: throw the dead driver out; the climb-over follows once the carcass is clear (or right away if there is none).
        private void Promote(GameObject pax, PatrolMarker marker)
        {
            _eject = Patrol.EjectCorpses(_car, pax);
            if (_eject == null) { FinishPromote(pax, marker); return; }
            _promoting = true;
            Plugin.Verbose("Crew: " + pax.name + " waits for the dead driver to clear the seat");
        }

        // Called by the Pilot when it gives up (MaxRecovers reached). With [Driver] StuckBailChance the whole crew gets out
        // instead of waiting: the car is left standing as an ordinary vehicle. Returns true if the crew left.
        internal bool OnStuck()
        {
            if (_dead || _done) return false;
            if (UnityEngine.Random.Range(0f, 100f) >= Plugin.StuckBailChance.Value) return false;
            var marker = Marker;
            Plugin.Verbose("Crew: stuck for good, the crew bails out (" + Plugin.StuckBailChance.Value + " % roll)");
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
            Nwh.SetInput(_car, 0f, 0f, 0f);
            if (_ctl != null && _ctl.Taken) _ctl.Release();
            SeatLocked(false);
            var pax = marker != null ? marker.Passenger : null;
            if (pax != null && PassengerAlive(marker, pax)) Patrol.BailOut(_car, pax, marker);
            if (_driver != null && DriverAlive())
            {
                if (marker != null) marker.DriverLeft();
                Patrol.BailOut(_car, _driver, marker != null ? marker.DriverPrefab : _driver.name.Replace("(Driver)", ""), -1f);
            }
            _driver = null; _health = null;
            _dead = true; _done = true; _driving = false;    // Phase = Released: saved as an empty car
            Vacated("crew bailed out");
            return true;
        }

        // Step 2: the passenger climbs onto the driver seat and Crew starts over with it as the driver.
        private void FinishPromote(GameObject pax, PatrolMarker marker)
        {
            if (_ctl != null && _ctl.Taken) _ctl.Release();
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
            Nwh.SetInput(_car, 0f, 0f, 0f);
            var drv = Patrol.MoveToDriverSeat(_car, pax);
            if (drv == null) { Plugin.Log.LogWarning("Crew: could not seat the passenger as driver"); _paxBails = true; return; }
            marker.Promote(drv);
            _dead = false; _stuck = false; _done = false; _driving = false;
            _rolling = 0f; _deadFor = 0f; _paxDecided = false; _paxBails = false;
            _delayOverride = Plugin.TakeoverSeconds;
            Init(_car, drv);
            _seated = 0f;
            MuteAi();
            Plugin.Verbose("Crew: " + drv.name + " took the wheel, " + "drives off in " + _delayOverride + " s" + "; car " + Speed() + " km/h, " + Nwh.Diag(_car));
        }

        private bool PlayerInside()
        {
            return _drive != null && _drive.Fsm.Initialized && _drive.ActiveStateName == "inCar";
        }

        private void StartDriving()
        {
            _driving = true;
            _ctl.Take();
            _pilot = Pilot.Attach(_car);
            Plugin.Verbose("Crew: driver drives off at " + Speed() + " km/h, " + Nwh.Diag(_car));
        }

        private void OnDriverDied()
        {
            _dead = true;
            SeatLocked(false);                 // the player may take the car from now on
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
            if (Nwh.GearIndex(_car) < 0) Nwh.ShiftInto(_car, 1);   // died while reversing: a stuck pedal pushes forward, not back
            if (!_driving)
            {
                Plugin.Verbose("Crew: driver died before driving off; seat free");
                _done = true;
                return;
            }
            // The INPUT FSMs are given back right away so the player can drive as soon as they get in; until then the
            // INPUT object is inactive, so whatever we leave in the NWH input stays put.
            _ctl.Release();
            bool stuck = UnityEngine.Random.Range(0f, 100f) < Plugin.StuckPedalChance.Value;
            if (stuck)
            {
                _stuck = true;
                _ctl.Take();
                Nwh.SetInput(_car, Plugin.AiThrottle.Value, 0f, 0f);
                Plugin.Verbose("Crew: driver died at " + Speed() + " km/h - gas pedal stuck (" + Plugin.StuckPedalChance.Value + " % roll), seat free");
                return;
            }
            _ctl.Take();
            Nwh.SetInput(_car, 0f, 0f, 0f);
            Plugin.Verbose("Crew: driver died at " + Speed() + " km/h - gas released, rolling out, seat free");
        }

        private void OnDestroy()
        {
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
        }

        private string Speed() { return _rb != null ? (_rb.velocity.magnitude * 3.6f).ToString("0.0") : "?"; }
    }
}

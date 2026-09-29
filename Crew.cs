using System;
using System.Linq;
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
            _drive = Patrol.FindFsm(car, "DriveTrigger", "Drive");
            var dt = Patrol.FindChild(car.transform, "DriveTrigger");
            if (dt != null) _enterTrigger = dt.GetComponents<Collider>().FirstOrDefault(c => c is SphereCollider);
            _ctl = new InputControl(car);

            // nobody else drives while the driver lives: no enter trigger (no F prompt), no Drive FSM (Activate events are ignored).
            // The car's DistanceKinematic FSM is left alone: re-enabling it restarts it in KinematicOn, which freezes a moving car.
            SeatLocked(driver != null);
            Plugin.Log.LogInfo("Crew: driver " + (driver != null ? driver.name : "absent") + " seated, " + "drives off in " + (_delayOverride >= 0f ? _delayOverride : 0f) + " s"
                + (_enterTrigger != null ? "" : " (no enter trigger found!)") + (_drive != null ? "" : " (no Drive FSM found!)"));
        }

        internal static Crew Restore(GameObject car, GameObject driver, CrewPhase phase, float seated)
        {
            var c = car.GetComponent<Crew>() ?? car.AddComponent<Crew>();
            c.Init(car, driver);
            c._seated = Mathf.Max(0f, seated);
            if (phase == CrewPhase.Driving)
            {
                if (!Nwh.EngineRunning(car)) Nwh.StartEngine(car);
                c.StartDriving();
            }
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
                c._dead = true; c._done = true;
                c.SeatLocked(false);
            }
            return c;
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

        private void SeatLocked(bool locked)
        {
            if (_enterTrigger != null) _enterTrigger.enabled = !locked;
            if (_drive != null) _drive.enabled = !locked;   // restart on re-enable lands in outCar, which has no actions
        }

        // Switch off the AI / body-mover FSMs; called right after Instantiate (before their Start) and every frame as a guard,
        // because Detection/Damage carry EnableFSM actions that could switch them back on.
        internal static void MuteAi(GameObject who, string[] allowedFsms = null)
        {
            if (who == null) return;
            foreach (var f in who.GetComponents<PlayMakerFSM>())
                if (f.enabled && Array.IndexOf(MutedFsms, f.FsmName) >= 0
                    && (allowedFsms == null || Array.IndexOf(allowedFsms, f.FsmName) < 0)) f.enabled = false;
        }

        // a shooting driver keeps its combat FSMs (its guard gates the Attack FSM); everything else is muted
        internal void MuteAi()
        {
            MuteAi(_driver, _driver != null && _driver.GetComponent<PassengerGuard>() != null ? PassengerGuard.CombatFsms : null);
        }

        private static bool Alive(GameObject who, PlayMakerFSM health)
        {
            if (who == null) return false;                           // Health FSM destroyed it (carcass spawned)
            if (who.transform.parent == null) return false;          // out of the seat somehow
            if (health != null && health.Fsm.Initialized)
            {
                var h = health.FsmVariables.GetFsmFloat("Health");
                if (h != null && h.Value <= 0f) return false;
            }
            return true;
        }

        private void Update()
        {
            if (_car == null) { Destroy(this); return; }
            if (Time.timeScale <= 0f) return;
            if (Time.time >= _nextIgnore)
            {
                _nextIgnore = Time.time + 1f;
                if (!_dead && _driver != null) Patrol.IgnoreCollisionsIfChanged(_driver, _car);
                var marker = _car.GetComponent<PatrolMarker>();
                if (marker != null && marker.Passenger != null) Patrol.IgnoreCollisionsIfChanged(marker.Passenger, _car);
            }
            if (_dead && PassengerReacts()) return;
            if (_done) return;

            if (!_dead)
            {
                if (!DriverAlive()) { OnDriverDied(); return; }
                MuteAi();
                _seated += Time.deltaTime;
                float delay = _delayOverride >= 0f ? _delayOverride : 0f;
                if (!_driving && _seated >= delay && Nwh.EngineRunning(_car)) StartDriving();
                return;
            }

            // Dead driver: preserve a stuck pedal until takeover, or hold zero while the car rolls out (inputs written in FixedUpdate).
            if (PlayerInside()) { Plugin.Log.LogInfo("Crew: player took the car after its driver died"); _ctl.Release(); _done = true; return; }
            if (_stuck) return;
            _rolling += Time.deltaTime;
            if (_rb != null && _rb.velocity.magnitude > 0.3f && _rolling < 120f) return;
            Plugin.Log.LogInfo("Crew: car rolled to a stop after " + _rolling.ToString("0.0") + " s");
            _ctl.Release();
            _done = true;
        }

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

        private bool DriverAlive() { return Alive(_driver, _health); }

        // ------------------------------------------------------------ the surviving passenger

        // After the driver's death a live passenger either takes the wheel or bails out (BailChance %), as soon as the car
        // stands still. A stuck pedal is kicked off after StuckPedalTakeover seconds so the car can stop. If the player took
        // the car first, the passenger always gets out. Returns true while it handled the frame.
        private bool PassengerReacts()
        {
            var marker = _car.GetComponent<PatrolMarker>();
            var pax = marker != null ? marker.Passenger : null;
            if (pax == null || !Alive(pax, marker.PassengerHealthFsm)) return false;
            _deadFor += Time.deltaTime;
            if (!_paxDecided)
            {
                _paxDecided = true;
                _paxBails = UnityEngine.Random.Range(0f, 100f) < Plugin.BailChance.Value;
                Plugin.Log.LogInfo("Crew: passenger " + pax.name + " will " + (_paxBails ? "bail out" : "take the wheel") + " once the car stops ("
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
                Plugin.Log.LogInfo("Crew: passenger kicked the dead driver's foot off the pedal");
            }
            float speed = _rb != null ? _rb.velocity.magnitude : 0f;
            if (speed > 1f && !playerIn) return false;                  // still rolling: let the roll-out logic run
            if (_stuck) return false;
            if (_paxBails)
            {
                Patrol.BailOut(_car, pax, marker);
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
            Plugin.Log.LogInfo("Crew: " + pax.name + " waits for the dead driver to clear the seat");
        }

        // Called by the Pilot when it gives up (MaxRecovers reached). With [Driver] StuckBailChance the whole crew gets out
        // instead of waiting: the car is left standing as an ordinary vehicle. Returns true if the crew left.
        internal bool OnStuck()
        {
            if (_dead || _done || !_driving) return false;
            if (UnityEngine.Random.Range(0f, 100f) >= Plugin.StuckBailChance.Value) return false;
            var marker = _car.GetComponent<PatrolMarker>();
            Plugin.Log.LogInfo("Crew: stuck for good, the crew bails out (" + Plugin.StuckBailChance.Value + " % roll)");
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
            Nwh.SetInput(_car, 0f, 0f, 0f);
            if (_ctl != null && _ctl.Taken) _ctl.Release();
            SeatLocked(false);
            var pax = marker != null ? marker.Passenger : null;
            if (pax != null && Alive(pax, marker.PassengerHealthFsm)) Patrol.BailOut(_car, pax, marker);
            if (_driver != null && DriverAlive())
            {
                if (marker != null) marker.DriverLeft();
                Patrol.BailOut(_car, _driver, marker != null ? marker.DriverPrefab : _driver.name.Replace("(Driver)", ""), -1f);
            }
            _driver = null; _health = null;
            _dead = true; _done = true; _driving = false;    // Phase = Released: saved as an empty car
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
            Plugin.Log.LogInfo("Crew: " + drv.name + " took the wheel, " + "drives off in " + _delayOverride + " s" + "; car " + Speed() + " km/h, " + Nwh.Diag(_car));
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
            Plugin.Log.LogInfo("Crew: driver drives off at " + Speed() + " km/h, " + Nwh.Diag(_car));
        }

        private void OnDriverDied()
        {
            _dead = true;
            SeatLocked(false);                 // the player may take the car from now on
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
            if (Nwh.GearIndex(_car) < 0) Nwh.ShiftInto(_car, 1);   // died while reversing: a stuck pedal pushes forward, not back
            if (!_driving)
            {
                Plugin.Log.LogInfo("Crew: driver died before driving off; seat free");
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
                Plugin.Log.LogInfo("Crew: driver died at " + Speed() + " km/h - gas pedal stuck (" + Plugin.StuckPedalChance.Value + " % roll), seat free");
                return;
            }
            _ctl.Take();
            Nwh.SetInput(_car, 0f, 0f, 0f);
            Plugin.Log.LogInfo("Crew: driver died at " + Speed() + " km/h - gas released, rolling out, seat free");
        }

        private void OnDestroy()
        {
            if (_pilot != null) { _pilot.Detach(); _pilot = null; }
        }

        private string Speed() { return _rb != null ? (_rb.velocity.magnitude * 3.6f).ToString("0.0") : "?"; }
    }
}

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
        private float _rolling, _nextLog;

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
            Plugin.Log.LogInfo("Crew: driver " + (driver != null ? driver.name : "absent") + " seated, " + (Plugin.DriveDelaySeconds.Value < 0f ? "never drives" : "drives in " + Plugin.DriveDelaySeconds.Value + " s")
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

        internal void MuteAi() { MuteAi(_driver); }

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
            if (_done) return;

            if (!_dead)
            {
                if (!DriverAlive()) { OnDriverDied(); return; }
                MuteAi();
                _seated += Time.deltaTime;
                float delay = Plugin.DriveDelaySeconds.Value;
                if (!_driving && delay >= 0f && _seated >= delay && Nwh.EngineRunning(_car)) StartDriving();   // -1 = never
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
                if (_pilot != null) _pilot.Step(); else Drive();
                return;
            }
            Nwh.SetInput(_car, _stuck ? Plugin.DriveThrottle.Value : 0f, 0f, 0f);
        }

        private bool DriverAlive() { return Alive(_driver, _health); }

        private bool PlayerInside()
        {
            return _drive != null && _drive.Fsm.Initialized && _drive.ActiveStateName == "inCar";
        }

        private void StartDriving()
        {
            _driving = true;
            _ctl.Take();
            if (Plugin.AiEnabled.Value) _pilot = Pilot.Attach(_car);
            Plugin.Log.LogInfo("Crew: driver drives off (" + (_pilot != null ? "driving AI" : "straight ahead") + ", throttle " + Plugin.DriveThrottle.Value + ")");
        }

        private void Drive()
        {
            if (Nwh.GearIndex(_car) <= 0) Nwh.ShiftInto(_car, 1);   // the automatic stays in N until told once
            Nwh.SetInput(_car, Plugin.DriveThrottle.Value, 0f, 0f);
            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Plugin.Verbose("Crew: driving, " + Speed() + " km/h gear " + Nwh.Gear(_car));
            }
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
                Nwh.SetInput(_car, Plugin.DriveThrottle.Value, 0f, 0f);
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

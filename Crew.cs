using System;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
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
        private string[] _mutedNames;

        private float _seated;                // seconds since the driver sat down
        private bool _driving, _dead, _done;
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
            _health = driver.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Health");
            _drive = Patrol.FindFsm(car, "DriveTrigger", "Drive");
            var dt = Patrol.FindChild(car.transform, "DriveTrigger");
            if (dt != null) _enterTrigger = dt.GetComponents<Collider>().FirstOrDefault(c => c is SphereCollider);
            _mutedNames = (Plugin.DriverDisabledFsms.Value ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            _ctl = new InputControl(car);

            // nobody else drives while the driver lives: no enter trigger (no F prompt), no Drive FSM (Activate events are ignored).
            // The car's DistanceKinematic FSM is left alone: re-enabling it restarts it in KinematicOn, which freezes a moving car.
            SeatLocked(true);
            Plugin.Log.LogInfo("Crew: driver " + driver.name + " seated, drives in " + Plugin.DriveDelaySeconds.Value + " s"
                + (_enterTrigger != null ? "" : " (no enter trigger found!)") + (_drive != null ? "" : " (no Drive FSM found!)"));
        }

        private void SeatLocked(bool locked)
        {
            if (_enterTrigger != null) _enterTrigger.enabled = !locked;
            if (_drive != null) _drive.enabled = !locked;   // restart on re-enable lands in outCar, which has no actions
        }

        // Switch off the AI / body-mover FSMs; called right after Instantiate (before their Start) and every frame as a guard,
        // because Detection/Damage carry EnableFSM actions that could switch them back on.
        internal void MuteAi()
        {
            if (_driver == null) return;
            foreach (var f in _driver.GetComponents<PlayMakerFSM>())
                if (f.enabled && Array.IndexOf(_mutedNames, f.FsmName) >= 0) f.enabled = false;
        }

        private void Update()
        {
            if (_done) return;
            if (_car == null) { Destroy(this); return; }
            if (Time.timeScale <= 0f) return;

            if (!_dead)
            {
                if (!DriverAlive()) { OnDriverDied(); return; }
                MuteAi();
                _seated += Time.deltaTime;
                if (!_driving && _seated >= Plugin.DriveDelaySeconds.Value && Nwh.EngineRunning(_car)) StartDriving();
                if (_driving) Drive();
                return;
            }

            // dead, gas released: hold throttle 0 / no brakes until the car has rolled to a stop, or the player takes it over
            if (PlayerInside()) { Plugin.Log.LogInfo("Crew: player took the car while it was rolling"); _done = true; return; }
            Nwh.SetInput(_car, 0f, 0f, 0f);
            _rolling += Time.deltaTime;
            if (_rb != null && _rb.velocity.magnitude > 0.3f && _rolling < 120f) return;
            Plugin.Log.LogInfo("Crew: car rolled to a stop after " + _rolling.ToString("0.0") + " s");
            _done = true;
        }

        private bool DriverAlive()
        {
            if (_driver == null) return false;                       // Health FSM destroyed it (carcass spawned)
            if (_driver.transform.parent == null) return false;      // out of the seat somehow
            if (_health != null && _health.Fsm.Initialized)
            {
                var h = _health.FsmVariables.GetFsmFloat("Health");
                if (h != null && h.Value <= 0f) return false;
            }
            return true;
        }

        private bool PlayerInside()
        {
            return _drive != null && _drive.Fsm.Initialized && _drive.ActiveStateName == "inCar";
        }

        private void StartDriving()
        {
            _driving = true;
            _ctl.Take();
            Plugin.Log.LogInfo("Crew: driver drives off (throttle " + Plugin.DriveThrottle.Value + ")");
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
                Nwh.SetInput(_car, Plugin.DriveThrottle.Value, 0f, 0f);
                Plugin.Log.LogInfo("Crew: driver died at " + Speed() + " km/h - gas pedal stuck (" + Plugin.StuckPedalChance.Value + " % roll), seat free");
                _done = true;
                return;
            }
            Nwh.SetInput(_car, 0f, 0f, 0f);
            Plugin.Log.LogInfo("Crew: driver died at " + Speed() + " km/h - gas released, rolling out, seat free");
        }

        private string Speed() { return _rb != null ? (_rb.velocity.magnitude * 3.6f).ToString("0.0") : "?"; }
    }
}

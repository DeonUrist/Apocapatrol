using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    // Lives on the car. Owns the driver: keeps its AI off, drives while it is alive, handles its death,
    // and keeps the player out of the driver seat until then.
    internal class Crew : MonoBehaviour
    {
        private GameObject _car;
        private GameObject _driver;
        private PlayMakerFSM _health;
        private List<PlayMakerFSM> _muted = new List<PlayMakerFSM>();
        private InputControl _ctl;
        private PlayMakerFSM _drive;          // DriveTrigger [Drive]
        private Collider _enterTrigger;       // DriveTrigger SphereCollider (the "press F to drive" trigger)
        private PlayMakerFSM _distKinematic;  // car [DistanceKinematic]
        private Rigidbody _rb;

        private float _seated;                // seconds since the driver sat down
        private bool _driving, _dead, _stuck, _done;
        private float _braking;
        private float _nextLog;

        internal bool DriverAlive { get { return !_dead && _driver != null && _driver.transform.parent != null; } }

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
            foreach (var f in driver.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Health") _health = f;
            _drive = Patrol.FindFsm(car, "DriveTrigger", "Drive");
            var dt = Patrol.FindChild(car.transform, "DriveTrigger");
            if (dt != null) _enterTrigger = dt.GetComponents<Collider>().FirstOrDefault(c => c is SphereCollider);
            foreach (var f in car.GetComponents<PlayMakerFSM>()) if (f.FsmName == "DistanceKinematic") _distKinematic = f;

            // nobody else drives while the driver lives: no enter trigger, no Drive FSM (Activate events are ignored), no far-away kinematic
            if (_enterTrigger != null) _enterTrigger.enabled = false;
            if (_drive != null) _drive.enabled = false;
            if (_distKinematic != null) _distKinematic.enabled = false;

            _ctl = new InputControl(car);
            _seated = 0f; _driving = _dead = _stuck = _done = false;
            Plugin.Log.LogInfo("Crew: driver " + driver.name + " seated, drives in " + Plugin.DriveDelaySeconds.Value + " s"
                + (_enterTrigger != null ? "" : " (no enter trigger found!)") + (_drive != null ? "" : " (no Drive FSM found!)"));
        }

        // Switch off the listed FSMs; called right after Instantiate (before their Start) and again every frame as a guard,
        // because Detection/Damage carry EnableFSM actions that could switch the movers back on.
        internal void MuteAi()
        {
            if (_driver == null) return;
            var names = (Plugin.DriverDisabledFsms.Value ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            foreach (var f in _driver.GetComponents<PlayMakerFSM>())
                if (f.enabled && Array.IndexOf(names, f.FsmName) >= 0) { f.enabled = false; if (!_muted.Contains(f)) _muted.Add(f); }
        }

        private void Update()
        {
            if (_done || _car == null) { if (_car == null) Destroy(this); return; }
            if (Time.timeScale <= 0f) return;

            if (!_dead && !DriverAliveNow()) { OnDriverDied(); }

            if (!_dead)
            {
                MuteAi();
                _seated += Time.deltaTime;
                if (!_driving && _seated >= Plugin.DriveDelaySeconds.Value && Nwh.EngineRunning(_car)) StartDriving();   // waits for the START flow too
                if (_driving) Drive();
                return;
            }

            // dead and not stuck: brake to a stop, then hand the car back
            _braking += Time.deltaTime;
            bool stopped = _rb == null || _rb.velocity.magnitude < 0.3f;
            if (!stopped && _braking < 8f) { Nwh.SetInput(_car, 0f, 0f, 1f); return; }
            Nwh.SetInput(_car, 0f, 0f, 0f);
            Patrol.Handbrake(_car, true);
            _ctl.Release();
            Plugin.Log.LogInfo("Crew: car stopped (" + (_rb != null ? (_rb.velocity.magnitude * 3.6f).ToString("0.0") : "?") + " km/h), handbrake on");
            _done = true;
        }

        private bool DriverAliveNow()
        {
            if (_driver == null) return false;                       // Health FSM destroyed it (carcass spawned)
            if (_driver.transform.parent == null) return false;      // thrown out of the seat somehow
            if (_health != null && _health.Fsm.Initialized)
            {
                var h = _health.FsmVariables.GetFsmFloat("Health");
                if (h != null && h.Value <= 0f) return false;
            }
            return true;
        }

        private void StartDriving()
        {
            _driving = true;
            _ctl.Take();
            Plugin.Log.LogInfo("Crew: driver drives off (throttle " + Plugin.DriveThrottle.Value + ")");
        }

        private void Drive()
        {
            if (Nwh.GearIndex(_car) <= 0) Nwh.ShiftInto(_car, 1);
            Nwh.SetInput(_car, Plugin.DriveThrottle.Value, 0f, 0f);
            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Plugin.Verbose("Crew: driving, " + (_rb != null ? (_rb.velocity.magnitude * 3.6f).ToString("0.0") : "?") + " km/h gear " + Nwh.Gear(_car));
            }
        }

        private void OnDriverDied()
        {
            _dead = true;
            _stuck = UnityEngine.Random.Range(0f, 100f) < Plugin.StuckPedalChance.Value;
            // the seat is free: the player may take the car
            if (_enterTrigger != null) _enterTrigger.enabled = true;
            if (_drive != null) _drive.enabled = true;
            if (_distKinematic != null) _distKinematic.enabled = true;
            if (!_driving)
            {
                // never drove: nothing to stop, nothing taken
                Plugin.Log.LogInfo("Crew: driver died before driving off; seat free");
                _done = true;
                return;
            }
            if (_stuck)
            {
                // leg on the gas: leave throttle where it is; INPUT stays off until the player gets in (the game's own enter flow activates it)
                Nwh.SetInput(_car, Plugin.DriveThrottle.Value, 0f, 0f);
                _ctl.Release();   // INPUT object stays inactive until the player enters, so the throttle value stays put
                _done = true;
                Plugin.Log.LogInfo("Crew: driver died - leg stuck on the gas (" + Plugin.StuckPedalChance.Value + " % roll), seat free");
            }
            else Plugin.Log.LogInfo("Crew: driver died - braking, seat free");
        }
    }
}

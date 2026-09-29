using System;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // Ranged passengers retain vanilla detection and firing, but never receive movement or animation FSMs. Their
    // visible body always uses the exact same fixed Pose component as the driver. Sound2 preserves weapon audio.
    internal class PassengerGuard : MonoBehaviour
    {
        internal static readonly string[] CombatFsms = { "Detection", "Attack", "RangedAttackWait", "Damage Ranged", "WeaponType", "Sound2" };
        private static readonly string[] TargetFsms = { "Detection", "Attack", "RangedAttackWait", "Damage Ranged", "WeaponType" };
        internal static readonly string[] RangedHumans = { "Boltjaw", "Flexa", "Lugnut", "Scrud", "Sprokka" };

        private GameObject _passenger, _car;
        private Transform _anchor;
        private PlayMakerFSM _attack;
        private PlayMakerFSM[] _combat;
        private GameObject[] _meleeWeapons;
        private Pose _pose;
        private bool _rangedPrefab, _ranged, _targetDiagnosticLogged, _targetAcquired;
        // driver mode: the same combat, but only in random bursts; the driver pose in between
        private bool _driverMode, _shooting;
        private float _nextBurst, _burstUntil;

        internal static bool IsRangedHuman(GameObject who) { return IsRanged(who); }

        internal static void Prepare(GameObject passenger, bool combatAllowed)
        {
            bool ranged = IsRanged(passenger);
            if (ranged)
            {
                ForceRangedOnly(passenger);
                NeutralizeMeleeDamage(passenger);
            }
            Crew.MuteAi(passenger, ranged && combatAllowed ? CombatFsms : null);
        }

        private static void MuteNonCombatAi(GameObject passenger, bool combatAllowed)
        {
            bool ranged = IsRanged(passenger);
            if (ranged)
                foreach (var fsm in passenger.GetComponents<PlayMakerFSM>())
                    if (fsm.FsmName == "Damage" && fsm.enabled) fsm.enabled = false;
            Crew.MuteAi(passenger, ranged && combatAllowed ? CombatFsms : null);
        }

        private bool CombatAllowed { get { return _driverMode ? Plugin.DriverRangedCombat.Value : Plugin.PassengerRangedCombat.Value; } }

        // A shooting mob at the wheel: like a passenger, but it only shoots in bursts at random intervals and sits in the
        // generic driver pose (hands on the wheel) in between.
        internal static PassengerGuard AttachDriver(GameObject driver, GameObject car, Transform sit)
        {
            Attach(driver, car, sit);
            var guard = driver.GetComponent<PassengerGuard>();
            guard.SetDriverMode(sit);
            return guard;
        }

        internal void SetDriverMode(Transform sit)
        {
            _anchor = sit;
            _driverMode = true;
            _shooting = false;
            _ranged = _rangedPrefab && Plugin.DriverRangedCombat.Value;
            if (_ranged) foreach (var fsm in _combat) if (fsm != null) fsm.enabled = true;
            if (_attack != null) _attack.enabled = false;
            if (_pose != null) { _pose.SetShooting(false); _pose.SetAim(Vector3.zero, false); }
            ScheduleBurst();
            Plugin.Log.LogInfo("Driver: " + _passenger.name + (_ranged ? " shoots in bursts every " + Plugin.DriverFireIntervalMin.Value + "-" + Plugin.DriverFireIntervalMax.Value + " s" : " does not shoot"));
        }

        private void ScheduleBurst()
        {
            _nextBurst = Time.time + UnityEngine.Random.Range(Plugin.DriverFireIntervalMin.Value, Mathf.Max(Plugin.DriverFireIntervalMin.Value, Plugin.DriverFireIntervalMax.Value));
        }

        // Ranged enemies normally switch to their separate melee Damage FSM when Attack's distance FloatCompare emits
        // d_melee. Redirect every melee event output to the same ranged event already used by that FSM, then keep the
        // melee damage FSM disabled. This preserves native targeting, cadence, projectile damage and Sound2 at all ranges.
        private static void ForceRangedOnly(GameObject passenger)
        {
            var attack = Array.Find(passenger.GetComponents<PlayMakerFSM>(), f => f.FsmName == "Attack");
            if (attack == null) return;
            FsmEvent rangedEvent = null;
            foreach (var state in attack.FsmStates)
                foreach (var action in state.Actions)
                    if (action != null)
                        foreach (var field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (field.FieldType != typeof(FsmEvent)) continue;
                            var ev = field.GetValue(action) as FsmEvent;
                            if (ev != null && ev.Name.IndexOf("ranged", StringComparison.OrdinalIgnoreCase) >= 0)
                                rangedEvent = ev;
                        }
            int redirected = 0;
            if (rangedEvent != null)
                foreach (var state in attack.FsmStates)
                    foreach (var action in state.Actions)
                        if (action != null)
                            foreach (var field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (field.FieldType != typeof(FsmEvent)) continue;
                                var ev = field.GetValue(action) as FsmEvent;
                                if (ev == null || ev.Name.IndexOf("melee", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                field.SetValue(action, rangedEvent);
                                redirected++;
                            }
            foreach (var fsm in passenger.GetComponents<PlayMakerFSM>())
                if (fsm.FsmName == "Damage") fsm.enabled = false;
            Plugin.Log.LogInfo("Passenger: forced ranged-only Attack on " + passenger.name + "; redirected "
                + redirected + " melee event output(s)" + (rangedEvent == null ? " (ranged event not found)" : " to " + rangedEvent.Name));
        }

        private static void NeutralizeMeleeDamage(GameObject passenger)
        {
            int disabledActions = 0;
            foreach (var fsm in passenger.GetComponents<PlayMakerFSM>())
            {
                if (fsm.FsmName != "Damage" && fsm.FsmName != "FireDamage") continue;
                foreach (var state in fsm.FsmStates)
                    foreach (var action in state.Actions)
                        if (action != null && action.Enabled)
                        {
                            action.Enabled = false;
                            disabledActions++;
                        }
                fsm.enabled = false;
            }

            var hitbox = Patrol.FindChild(passenger.transform, "FireDamageCollider");
            if (hitbox != null)
                foreach (var collider in hitbox.GetComponents<Collider>()) collider.enabled = false;

            Plugin.Log.LogInfo("Passenger: neutralized melee/contact damage on " + passenger.name
                + "; disabled " + disabledActions + " FSM action(s)" + (hitbox == null ? "; hitbox not found" : "; hitbox disabled"));
        }

        internal static void Attach(GameObject passenger, GameObject car, Transform anchor)
        {
            var guard = passenger.GetComponent<PassengerGuard>() ?? passenger.AddComponent<PassengerGuard>();
            guard._passenger = passenger;
            guard._car = car;
            guard._anchor = anchor;
            guard._rangedPrefab = IsRanged(passenger);
            guard._ranged = guard._rangedPrefab && Plugin.PassengerRangedCombat.Value;
            guard._combat = Array.FindAll(passenger.GetComponents<PlayMakerFSM>(), f => Array.IndexOf(CombatFsms, f.FsmName) >= 0);
            guard._attack = Array.Find(guard._combat, f => f.FsmName == "Attack");
            guard._meleeWeapons = guard._rangedPrefab ? FindMeleeWeapons(passenger) : new GameObject[0];
            guard.HideMeleeWeapons();
            guard._pose = passenger.GetComponent<Pose>();
            if (guard._pose != null) guard._pose.ConfigurePassengerAim();
            MuteNonCombatAi(passenger, Plugin.PassengerRangedCombat.Value);
            Plugin.Log.LogInfo("Passenger: " + passenger.name + " fixed in driver pose; "
                + (guard._ranged ? "vanilla ranged fire and Sound2 enabled in +/-" + Plugin.PassengerFireArc.Value + " degree arc" : "passive"));
        }

        private static GameObject[] FindMeleeWeapons(GameObject passenger)
        {
            return Array.FindAll(Array.ConvertAll(passenger.GetComponentsInChildren<Transform>(true), t => t.gameObject), go =>
            {
                string name = go.name;
                return string.Equals(name, "machete", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "old_knife", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "shiv", StringComparison.OrdinalIgnoreCase);
            });
        }

        private void HideMeleeWeapons()
        {
            if (_meleeWeapons == null) return;
            foreach (var weapon in _meleeWeapons)
                if (weapon != null && weapon.activeSelf) weapon.SetActive(false);
        }

        private void LateUpdate()
        {
            // Attack can activate the right-hand blade after Update when its close-range state runs. Apply this after
            // PlayMaker so no melee prop is ever displayed, while leaving left-hand firearms and their effects alone.
            if (_rangedPrefab) HideMeleeWeapons();
        }

        private static bool IsRanged(GameObject passenger)
        {
            if (passenger == null) return false;
            string name = passenger.name;
            int suffix = name.IndexOf('('); if (suffix >= 0) name = name.Substring(0, suffix);
            if (Array.FindIndex(RangedHumans, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) < 0) return false;
            bool wait = Array.Exists(passenger.GetComponents<PlayMakerFSM>(), f => f.FsmName == "RangedAttackWait");
            return wait && Patrol.FindChild(passenger.transform, "AttackRaycast_Ranged") != null;
        }

        private void Update()
        {
            if (_passenger == null || _car == null || _anchor == null) { Destroy(this); return; }
            if (Time.timeScale <= 0f) return;

            bool enabled = _rangedPrefab && CombatAllowed;
            if (enabled != _ranged)
            {
                _ranged = enabled;
                _targetAcquired = false;
                if (_ranged) foreach (var fsm in _combat) if (fsm != null) fsm.enabled = true;
                else if (_attack != null) _attack.enabled = false;
            }
            MuteNonCombatAi(_passenger, CombatAllowed);
            if (!_ranged)
            {
                if (_pose != null) _pose.SetAim(Vector3.zero, false);
                return;
            }

            GameObject target = FindVanillaTarget();
            bool inArc = false;
            Vector3 aimPoint = Vector3.zero;
            if (target != null)
            {
                _targetAcquired = true;
                aimPoint = AimPoint(target);
                var flat = Vector3.ProjectOnPlane(aimPoint - _passenger.transform.position, _car.transform.up);
                if (flat.sqrMagnitude > 0.001f)
                    inArc = Mathf.Abs(Vector3.SignedAngle(_car.transform.forward, flat, _car.transform.up)) <= Plugin.PassengerFireArc.Value;
            }

            if (_driverMode)
            {
                // bursts: open one when due and a target is in the arc, close it after FireBurstSeconds
                if (!_shooting && Time.time >= _nextBurst && target != null && inArc)
                {
                    _shooting = true;
                    _burstUntil = Time.time + Plugin.DriverFireBurstSeconds.Value;
                    if (_pose != null) _pose.SetShooting(true);
                    Plugin.Verbose("Driver: " + _passenger.name + " opens fire for " + Plugin.DriverFireBurstSeconds.Value + " s");
                }
                else if (_shooting && (Time.time >= _burstUntil || target == null))
                {
                    _shooting = false;
                    if (_pose != null) _pose.SetShooting(false);
                    ScheduleBurst();
                    Plugin.Verbose("Driver: " + _passenger.name + " back on the wheel, next burst in " + (_nextBurst - Time.time).ToString("0") + " s");
                }
                if (!_shooting)
                {
                    if (_attack != null) _attack.enabled = false;
                    if (_pose != null) _pose.SetAim(Vector3.zero, false);
                    return;
                }
            }

            if (_attack != null && (_targetAcquired || target != null)) _attack.enabled = target != null && inArc;
            if (_pose != null) _pose.SetAim(aimPoint, target != null && inArc);
        }

        private GameObject FindVanillaTarget()
        {
            GameObject best = null;
            int bestScore = 0;
            foreach (var fsm in _combat)
            {
                if (fsm == null || Array.IndexOf(TargetFsms, fsm.FsmName) < 0) continue;
                foreach (var variable in fsm.FsmVariables.GameObjectVariables)
                    Score(variable.Name, variable.Value, ref best, ref bestScore);
                foreach (var state in fsm.FsmStates)
                    foreach (var action in state.Actions)
                    {
                        if (action == null) continue;
                        foreach (var field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (field.FieldType != typeof(FsmGameObject)) continue;
                            var value = field.GetValue(action) as FsmGameObject;
                            if (value != null) Score(field.Name + " " + value.Name, value.Value, ref best, ref bestScore);
                        }
                    }
            }
            if (best == null && !_targetDiagnosticLogged)
            {
                _targetDiagnosticLogged = true;
                Plugin.Verbose("Passenger ranged combat: waiting for vanilla FSM target");
            }
            return best;
        }

        private void Score(string name, GameObject candidate, ref GameObject best, ref int bestScore)
        {
            if (candidate == null || candidate == _passenger || candidate.transform.IsChildOf(_passenger.transform)) return;
            string n = (name ?? "").ToLowerInvariant();
            int score = n.Contains("target") ? 5 : n.Contains("player") ? 4 : n.Contains("enemy") ? 3 : n.Contains("closest") ? 2 : 0;
            if (score <= bestScore) return;
            best = candidate;
            bestScore = score;
        }

        private static Vector3 AimPoint(GameObject target)
        {
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                int colon = n.LastIndexOf(':');
                if ((colon >= 0 ? n.Substring(colon + 1) : n) == "Head") return t.position;
            }
            var col = target.GetComponentInChildren<Collider>();
            return col != null ? col.bounds.center : target.transform.position;
        }
    }
}

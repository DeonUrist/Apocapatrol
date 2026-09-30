using System;
using System.Collections.Generic;
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
        private PlayMakerFSM[] _combat, _fsms;
        private GameObject[] _meleeWeapons;
        private Renderer[] _meleeRenderers = new Renderer[0];
        private Collider[] _meleeColliders = new Collider[0];
        private Pose _pose;
        private bool _rangedPrefab, _ranged, _targetDiagnosticLogged, _targetAcquired;
        // target sources, resolved once by reflection (was: every frame): FsmGameObject variables and action fields whose
        // name suggests a target, with their score
        private FsmGameObject[] _targetVars; private int[] _targetVarScores;
        private FsmStateAction[] _targetActions; private FieldInfo[] _targetFields; private int[] _targetFieldScores;
        private GameObject _aimTargetGo; private Transform _aimHead;   // AimPoint cache
        private float _nextTargetSearch; private GameObject _lastTarget;
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
            MuteNonCombatAi(passenger.GetComponents<PlayMakerFSM>(), IsRanged(passenger), combatAllowed);
        }

        // the periodic guard: the FSM array and the ranged verdict are resolved once at Attach, not every half second
        private static void MuteNonCombatAi(PlayMakerFSM[] fsms, bool ranged, bool combatAllowed)
        {
            if (ranged)
                foreach (var fsm in fsms)
                    if (fsm != null && fsm.FsmName == "Damage" && fsm.enabled) fsm.enabled = false;
            Crew.MuteAi(fsms, ranged && combatAllowed ? CombatFsms : null);
        }

        private bool CombatAllowed { get { return Plugin.RangedCombat.Value; } }

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
            _ranged = _rangedPrefab && Plugin.RangedCombat.Value;
            if (_ranged) foreach (var fsm in _combat) if (fsm != null) fsm.enabled = true;
            if (_attack != null) _attack.enabled = false;
            if (_pose != null) { _pose.SetShooting(false); _pose.SetAim(Vector3.zero, false); }
            ScheduleBurst();
            Plugin.Verbose("Driver: " + _passenger.name + (_ranged ? " shoots in bursts every " + Plugin.FireIntervalMin.Value + "-" + Plugin.FireIntervalMax.Value + " s" : " does not shoot"));
        }

        private void ScheduleBurst()
        {
            _nextBurst = Time.time + UnityEngine.Random.Range(Plugin.FireIntervalMin.Value, Mathf.Max(Plugin.FireIntervalMin.Value, Plugin.FireIntervalMax.Value));
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
            Plugin.Verbose("Passenger: forced ranged-only Attack on " + passenger.name + "; redirected "
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

            Plugin.Verbose("Passenger: neutralized melee/contact damage on " + passenger.name
                + "; disabled " + disabledActions + " FSM action(s)" + (hitbox == null ? "; hitbox not found" : "; hitbox disabled"));
        }

        internal static void Attach(GameObject passenger, GameObject car, Transform anchor)
        {
            var guard = passenger.GetComponent<PassengerGuard>() ?? passenger.AddComponent<PassengerGuard>();
            guard._passenger = passenger;
            guard._car = car;
            guard._anchor = anchor;
            guard._rangedPrefab = IsRanged(passenger);
            guard._ranged = guard._rangedPrefab && Plugin.RangedCombat.Value;
            guard._combat = Array.FindAll(passenger.GetComponents<PlayMakerFSM>(), f => Array.IndexOf(CombatFsms, f.FsmName) >= 0);
            guard._attack = Array.Find(guard._combat, f => f.FsmName == "Attack");
            guard._fsms = passenger.GetComponents<PlayMakerFSM>();
            guard._meleeWeapons = guard._rangedPrefab ? FindMeleeWeapons(passenger) : new GameObject[0];
            var rs = new List<Renderer>(); var cs = new List<Collider>();
            foreach (var w in guard._meleeWeapons) { rs.AddRange(w.GetComponentsInChildren<Renderer>(true)); cs.AddRange(w.GetComponentsInChildren<Collider>(true)); }
            guard._meleeRenderers = rs.ToArray(); guard._meleeColliders = cs.ToArray();
            guard.IndexTargetSources();
            guard.HideMeleeWeapons();
            guard._pose = passenger.GetComponent<Pose>();
            if (guard._pose != null) guard._pose.ConfigurePassengerAim();
            MuteNonCombatAi(passenger, Plugin.RangedCombat.Value);
            Plugin.Verbose("Passenger: " + passenger.name + " fixed in driver pose; "
                + (guard._ranged ? "vanilla ranged fire and Sound2 enabled in +/-" + Plugin.FireArc.Value + " degree arc" : "passive"));
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

        // The melee prop is neutralised in place (renderers and colliders off) rather than deactivated: the Attack FSM's
        // close-range state re-activates the object every frame, and a SetActive ping-pong made its collider enter the car's
        // CarAttack trigger every frame ("Could not find FSM: ID on collider" spam, a physics pair reset per frame, lag).
        private float _nextWeaponCheck, _nextGuard;
        private void HideMeleeWeapons()
        {
            foreach (var r in _meleeRenderers) if (r != null && r.enabled) r.enabled = false;
            foreach (var c in _meleeColliders) if (c != null && c.enabled) c.enabled = false;
        }

        private void LateUpdate()
        {
            // Attack can activate the right-hand blade after Update when its close-range state runs. Apply this after
            // PlayMaker so no melee prop is ever displayed, while leaving left-hand firearms and their effects alone.
            if (_rangedPrefab && Time.time >= _nextWeaponCheck) { _nextWeaponCheck = Time.time + 0.5f; HideMeleeWeapons(); }
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
            if (Time.time >= _nextGuard) { _nextGuard = Time.time + 0.5f; MuteNonCombatAi(_fsms, _rangedPrefab, CombatAllowed); }
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
                    inArc = Mathf.Abs(Vector3.SignedAngle(_car.transform.forward, flat, _car.transform.up)) <= Plugin.FireArc.Value
                         && (aimPoint - _passenger.transform.position).magnitude <= Plugin.ShootDistance.Value;
            }

            if (_driverMode)
            {
                // bursts: open one when due and a target is in the arc, close it after FireBurstSeconds
                if (!_shooting && Time.time >= _nextBurst && target != null && inArc)
                {
                    _shooting = true;
                    _burstUntil = Time.time + Plugin.FireBurstSeconds.Value;
                    if (_pose != null) _pose.SetShooting(true);
                    Plugin.Verbose("Driver: " + _passenger.name + " opens fire for " + Plugin.FireBurstSeconds.Value + " s");
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

        private static int ScoreOf(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return n.Contains("target") ? 5 : n.Contains("player") ? 4 : n.Contains("enemy") ? 3 : n.Contains("closest") ? 2 : 0;
        }

        // One reflection pass over the combat FSMs: remember every FsmGameObject variable / action field that can hold a target.
        private void IndexTargetSources()
        {
            var vars = new List<FsmGameObject>(); var varScores = new List<int>();
            var actions = new List<FsmStateAction>(); var fields = new List<FieldInfo>(); var fieldScores = new List<int>();
            foreach (var fsm in _combat)
            {
                if (fsm == null || Array.IndexOf(TargetFsms, fsm.FsmName) < 0) continue;
                foreach (var variable in fsm.FsmVariables.GameObjectVariables)
                {
                    int s = ScoreOf(variable.Name);
                    if (s > 0) { vars.Add(variable); varScores.Add(s); }
                }
                foreach (var state in fsm.FsmStates)
                    foreach (var action in state.Actions)
                    {
                        if (action == null) continue;
                        foreach (var field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (field.FieldType != typeof(FsmGameObject)) continue;
                            var value = field.GetValue(action) as FsmGameObject;
                            if (value == null) continue;
                            int s = Mathf.Max(ScoreOf(field.Name), ScoreOf(value.Name));
                            if (s > 0) { actions.Add(action); fields.Add(field); fieldScores.Add(s); }
                        }
                    }
            }
            _targetVars = vars.ToArray(); _targetVarScores = varScores.ToArray();
            _targetActions = actions.ToArray(); _targetFields = fields.ToArray(); _targetFieldScores = fieldScores.ToArray();
            Plugin.Verbose("Passenger: " + _passenger.name + " target sources: " + _targetVars.Length + " variables, " + _targetFields.Length + " action fields");
        }

        private GameObject FindVanillaTarget()
        {
            if (Time.time < _nextTargetSearch) return _lastTarget;     // 10× a second is plenty
            _nextTargetSearch = Time.time + 0.1f;
            GameObject best = null;
            int bestScore = 0;
            if (_targetVars != null)
                for (int i = 0; i < _targetVars.Length; i++)
                    if (_targetVarScores[i] > bestScore) Consider(_targetVars[i].Value, _targetVarScores[i], ref best, ref bestScore);
            if (_targetFields != null)
                for (int i = 0; i < _targetFields.Length; i++)
                {
                    if (_targetFieldScores[i] <= bestScore) continue;
                    var value = _targetFields[i].GetValue(_targetActions[i]) as FsmGameObject;
                    if (value != null) Consider(value.Value, _targetFieldScores[i], ref best, ref bestScore);
                }
            if (best == null && !_targetDiagnosticLogged)
            {
                _targetDiagnosticLogged = true;
                Plugin.Verbose("Passenger ranged combat: waiting for vanilla FSM target");
            }
            _lastTarget = best;
            return best;
        }

        private void Consider(GameObject candidate, int score, ref GameObject best, ref int bestScore)
        {
            if (candidate == null || candidate == _passenger || candidate.transform.IsChildOf(_passenger.transform)) return;
            best = candidate;
            bestScore = score;
        }

        // the Head bone of the target (found once per target, not by walking its whole hierarchy every frame)
        private Vector3 AimPoint(GameObject target)
        {
            if (target != _aimTargetGo || (_aimHead == null && target != null))
            {
                _aimTargetGo = target; _aimHead = null;
                foreach (var t in target.GetComponentsInChildren<Transform>(true))
                {
                    string n = t.name;
                    int colon = n.LastIndexOf(':');
                    if ((colon >= 0 ? n.Substring(colon + 1) : n) == "Head") { _aimHead = t; break; }
                }
                if (_aimHead == null) _aimHead = target.transform;   // no Head: aim at the root (collider centre would need a lookup per frame too)
            }
            return _aimHead.position;
        }
    }
}

using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Apocapatrol
{
    // Melee hits on a car's fitted wheels (1.20.0). A fitted wheel's collider is a trigger (the wheel's CheckTag FSM, state vehPart:
    // ColliderSetIsTrigger true - it would fight the NWH wheel otherwise), and the player's melee weapons find their target with a
    // SphereCast2 that ignores triggers (PlayerCamera, r 0.02, 1.8 m) - so a knife cut a loose wheel but went straight through a fitted one.
    // While a melee weapon's Attack FSM is in its "fire" state (the swing, ~0.35 s) the same cast is repeated including triggers;
    // if the first thing it meets is a fitted wheel (before anything solid), the wheel's own Bodypart FSM gets the weapon's damage
    // (the value its "hit" state would have written) and the Damage event - exactly what a hit on a loose wheel does. One hit per swing.
    // Apocaraider (1.5.1+; the hub fix in 1.5.3) has its own version with its wheel rules (multiplier, hit numbers, pop-off): this one then stays out.
    internal static class MeleeWheels
    {
        private const float Radius = 0.02f, Reach = 1.8f;
        private const int Mask = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 13) | (1 << 14) | (1 << 16);   // the knife's own mask
        private static Transform _parent, _cam;
        private static float _nextFind;
        private static PlayMakerFSM _swing;
        private static bool _done;
        private static readonly Dictionary<int, PlayMakerFSM> _attack = new Dictionary<int, PlayMakerFSM>();
        private static readonly Dictionary<int, float> _damage = new Dictionary<int, float>();     // NaN = not a melee weapon
        private static readonly RaycastHit[] _hits = new RaycastHit[24];
        private static int _raider = -1;

        internal static void Reset() { _parent = null; _cam = null; _swing = null; _done = false; _attack.Clear(); _damage.Clear(); }

        // Apocaraider's own version is there: leave it to that one
        private static bool RaiderHandles()
        {
            if (_raider < 0)
            {
                _raider = 0;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == "Apocaraider" && a.GetType("Apocaraider.MeleeWheels") != null) { _raider = 1; break; }
                }
                catch (Exception) { }
                Plugin.Verbose("Melee wheels: " + (_raider == 1 ? "Apocaraider handles them" : "handled here"));
            }
            return _raider == 1;
        }

        // [Debug] VerboseLog: what a swing sees (1.20.2 diagnostics) - also while Apocaraider handles the damage
        private static bool V { get { return Plugin.VerboseLog != null && Plugin.VerboseLog.Value; } }
        private static string _lastStates = "";
        private static bool _logged;
        private static float _nextNoArm;

        internal static void Tick()
        {
            try
            {
                bool raider = RaiderHandles();
                if (raider && !V) return;
                if (_parent == null)
                {
                    if (Time.unscaledTime < _nextFind) return;
                    _nextFind = Time.unscaledTime + 2f;
                    var holder = GameObject.Find("PlayerCameraHolder");
                    _cam = holder != null ? holder.transform.Find("PlayerCamera") : null;
                    _parent = _cam != null ? _cam.Find("WeaponsArm/Parent") : null;
                    if (_parent == null)
                    {
                        if (V && Time.unscaledTime >= _nextNoArm) { _nextNoArm = Time.unscaledTime + 30f; Plugin.Verbose("Melee wheels: no PlayerCameraHolder/PlayerCamera/WeaponsArm/Parent (holder " + (holder != null) + ", camera " + (_cam != null) + ")"); }
                        return;
                    }
                    Plugin.Verbose("Melee wheels: weapons under " + _parent.name + " (" + _parent.childCount + ")");
                }
                PlayMakerFSM active = null;
                string states = V ? "" : null;
                for (int i = 0; i < _parent.childCount; i++)
                {
                    var w = _parent.GetChild(i);
                    if (!w.gameObject.activeInHierarchy) continue;
                    var f = Attack(w.gameObject);
                    if (f == null || f.Fsm == null) continue;
                    if (states != null) states += (states.Length > 0 ? ", " : "") + w.name + ":" + (f.Fsm.Initialized ? f.ActiveStateName : "(not initialised)") + (f.enabled ? "" : "(off)");
                    if (active == null && f.Fsm.Initialized && f.ActiveStateName == "fire") active = f;
                }
                if (states != null && states != _lastStates) { _lastStates = states; Plugin.Verbose("Melee wheels: weapons in hand: " + (states.Length > 0 ? states : "none with an Attack FSM")); }
                if (active == null)
                {
                    if (_swing != null && V && !_logged) Plugin.Verbose("Melee wheels: swing of " + _swing.gameObject.name + " ended - the cast (triggers included, " + Reach + " m) met nothing");
                    _swing = null; _done = false; return;
                }
                if (active != _swing)
                {
                    _swing = active; _done = false; _logged = false;
                    if (V) Plugin.Verbose("Melee wheels: swing of " + active.gameObject.name + " (damage " + MeleeDamage(active) + ", " + (raider ? "Apocaraider applies it" : "applied here") + ")");
                }
                if (_done) return;
                float dmg = MeleeDamage(active);
                if (float.IsNaN(dmg))
                {
                    if (V && !_logged) { _logged = true; Plugin.Verbose("Melee wheels: " + active.gameObject.name + " is not a melee weapon here (a Reload FSM, or no Bodypart.Damage in its \"hit\" state)"); }
                    return;
                }
                var bodypart = WheelInReach();
                if (bodypart == null) return;
                if (raider) { _done = true; Plugin.Verbose("Melee wheels: would hit " + bodypart.gameObject.name + " - Apocaraider applies the damage"); return; }
                var v = bodypart.FsmVariables.GetFsmFloat("Damage");
                if (v == null) { Plugin.Verbose("Melee wheels: " + bodypart.gameObject.name + " Bodypart has no Damage variable"); return; }
                v.Value = dmg;
                bodypart.SendEvent("Damage");
                _done = true;
                Plugin.Verbose("Melee wheels: " + active.gameObject.name + " hit " + bodypart.gameObject.name + " (" + dmg + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Melee wheels: " + e); _parent = null; }
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (int k = 0; k < 3 && t.parent != null; k++) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static PlayMakerFSM Attack(GameObject w)
        {
            PlayMakerFSM f;
            int id = w.GetInstanceID();
            if (_attack.TryGetValue(id, out f) && f != null) return f;
            f = null;
            foreach (var x in w.GetComponents<PlayMakerFSM>()) if (x != null && x.FsmName == "Attack") { f = x; break; }
            _attack[id] = f;
            return f;
        }

        // the weapon's own hit value: its "hit" state writes Bodypart.Damage (old_knife -12); guns (a Reload FSM) are not melee
        private static float MeleeDamage(PlayMakerFSM attack)
        {
            float d;
            int id = attack.GetInstanceID();
            if (_damage.TryGetValue(id, out d)) return d;
            d = float.NaN;
            bool gun = false;
            foreach (var x in attack.GetComponents<PlayMakerFSM>()) if (x != null && x.FsmName == "Reload") { gun = true; break; }
            if (!gun)
                foreach (var st in attack.Fsm.States)
                {
                    if (st.Name != "hit") continue;
                    var acts = st.Actions;
                    if (acts == null || acts.Length == 0) { st.LoadActions(); acts = st.Actions; }
                    if (acts == null) break;
                    foreach (var a in acts)
                    {
                        var s = a as SetFsmFloat;
                        if (s != null && s.fsmName != null && s.fsmName.Value == "Bodypart" && s.variableName != null && s.variableName.Value == "Damage" && s.setValue != null)
                        { d = s.setValue.Value; break; }
                    }
                    break;
                }
            _damage[id] = d;
            return d;
        }

        // the swing's cast with triggers: the nearest thing that is a fitted wheel (before anything solid) -> its Bodypart FSM
        private static PlayMakerFSM WheelInReach()
        {
            if (_cam == null) return null;
            int n = Physics.SphereCastNonAlloc(_cam.position, Radius, _cam.forward, _hits, Reach, Mask, QueryTriggerInteraction.Collide);
            if (n <= 0) return null;
            Array.Sort(_hits, 0, n, HitOrder.Instance);
            var player = PlayerRef.Player;
            if (V && !_logged)
            {
                _logged = true;
                var sb = new System.Text.StringBuilder("Melee wheels: swing cast met " + n + ":");
                for (int i = 0; i < n && i < 10; i++)
                {
                    var c = _hits[i].collider;
                    if (c == null) continue;
                    var wheel = FittedWheel(c.transform);
                    bool own = (player != null && c.transform.IsChildOf(player)) || c.transform.IsChildOf(_cam);
                    sb.Append("\n    ").Append(_hits[i].distance.ToString("0.00")).Append(" m  ").Append(Path(c.transform)).Append("  [").Append(c.GetType().Name)
                      .Append(c.isTrigger ? ", trigger" : ", solid").Append(", layer ").Append(c.gameObject.layer).Append(c.enabled ? "" : ", disabled").Append("]")
                      .Append(own ? "  (player, skipped)" : wheel != null ? "  -> wheel " + wheel.name : "");
                }
                Plugin.Verbose(sb.ToString());
            }
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i].collider;
                if (col == null) continue;
                if (player != null && col.transform.IsChildOf(player)) continue;
                if (col.transform.IsChildOf(_cam)) continue;
                var part = FittedWheel(col.transform);
                if (part != null)
                {
                    foreach (var f in part.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Bodypart") return f;
                    return null;
                }
                if (!col.isTrigger) return null;   // something solid first (a body panel, a door, the ground): the game's own cast handles it
            }
            return null;
        }

        // The wheel a collider belongs to (1.20.1): the wheel item itself (tag vehPart on a hinge_wheel*), or anything else under a wheel
        // hinge - the solid sphere collider of hinge_wheel*/wheel_hub (the game's AddSphereCollider, about the tyre's size, part of the
        // FRAME) wraps the fitted wheel, so a swing meets it before the tyre's own trigger and 1.20.0 stopped there as "solid first".
        // The hub now counts as the wheel on its hinge. Another part on the way up (a fender, a plate) = not a wheel.
        private static Transform FittedWheel(Transform t)
        {
            for (var a = t; a != null; a = a.parent)
            {
                bool tagged;
                try { tagged = a.CompareTag("vehPart"); } catch (Exception) { tagged = false; }
                if (tagged) return a.parent != null && a.parent.name.StartsWith("hinge_wheel", StringComparison.Ordinal) ? a : null;
                if (a.name.StartsWith("hinge_wheel", StringComparison.Ordinal))
                {
                    for (int i = 0; i < a.childCount; i++)
                    {
                        var c = a.GetChild(i);
                        bool part;
                        try { part = c.CompareTag("vehPart"); } catch (Exception) { part = false; }
                        if (part) return c;
                    }
                    return null;                     // a bare hub: no wheel fitted
                }
            }
            return null;
        }

        private class HitOrder : IComparer<RaycastHit>
        {
            internal static readonly HitOrder Instance = new HitOrder();
            public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); }
        }
    }
}

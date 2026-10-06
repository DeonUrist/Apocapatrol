using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using NWH.WheelController3D;
using UnityEngine;

namespace Apocapatrol
{
    // 2.3.0: a template's suspension adjustment (the Part Adjustment tool's wheel spacing x1..1.5 and assembly height) applied to a spawned
    // car - with or without the Part Adjustment mod. With it, the values go through its public SuspensionApi (reflection), so they live in
    // its per-car save data and the two mods never disagree. Without it, SuspensionTune below does the same geometry: the suspension
    // model is stretched on X and moved up, the lift-kit hinge moves with it, and each hinge_wheel_* mount (what NWH casts its suspension
    // from) follows the end of its axle (width) and the height. Only a car with a lift kit on hinge_suspension takes an adjustment: the
    // game's checkSuspension decides stock / lifted by that child, and the tool itself needs the kit (Denis: "no kit, no adjustment").
    internal static class Suspension
    {
        private static bool _apiLooked; private static MethodInfo _apiSet, _apiGet;

        private static void LookForApi()
        {
            if (_apiLooked) return;
            _apiLooked = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PartAdjustment");
                var t = asm != null ? asm.GetType("PartAdjustment.SuspensionApi") : null;
                if (t != null)
                {
                    _apiSet = t.GetMethod("Set", new[] { typeof(GameObject), typeof(float), typeof(float) });
                    _apiGet = t.GetMethod("Get", new[] { typeof(GameObject), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
                }
                Plugin.Verbose("Suspension: Part Adjustment " + (asm == null ? "not loaded - own geometry" : _apiSet != null ? "API found" : "loaded without SuspensionApi (older than 1.2.0) - own geometry"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Suspension: API lookup: " + e.Message); }
        }

        internal static Transform KitHinge(GameObject car) { return car != null ? car.transform.Find("hinge_suspension_parent/hinge_suspension") : null; }

        internal static bool KitFitted(GameObject car)
        {
            var h = KitHinge(car);
            if (h == null) return false;
            for (int i = 0; i < h.childCount; i++) if (h.GetChild(i).CompareTag("vehPart")) return true;
            return false;
        }

        // applies width / height to a car (standard 1 / 0 = nothing to do unless it was adjusted before). False: no kit / unsupported.
        internal static bool Apply(GameObject car, float width, float height, string why)
        {
            if (car == null) return false;
            width = Mathf.Clamp(width, 1f, 1.5f); height = Mathf.Clamp(height, -1f, 1f);
            bool standard = width == 1f && height == 0f;
            if (!KitFitted(car))
            {
                if (!standard) Plugin.Verbose("Suspension: " + car.name + " has no lift kit - its template's suspension " + width.ToString("0.00") + " / " + height.ToString("0.00") + " is ignored");
                return false;
            }
            LookForApi();
            if (_apiSet != null)
            {
                try
                {
                    bool ok = (bool)_apiSet.Invoke(null, new object[] { car, width, height });
                    Plugin.Verbose("Suspension: " + car.name + " width x" + width.ToString("0.00") + ", height " + height.ToString("+0.00;-0.00") + " m via Part Adjustment (" + why + ")" + (ok ? "" : " - refused"));
                    return ok;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Suspension: Part Adjustment API: " + e.Message); }
            }
            var tune = SuspensionTune.Ensure(car);
            if (tune == null) { if (!standard) Plugin.Verbose("Suspension: " + car.name + " is not an adjustable chassis"); return false; }
            tune.Set(width, height);
            Plugin.Verbose("Suspension: " + car.name + " width x" + width.ToString("0.00") + ", height " + height.ToString("+0.00;-0.00") + " m (" + why + ")");
            return true;
        }

        // the car's current adjustment (1 / 0 when standard or without a kit) - what the template exporter writes
        internal static void Read(GameObject car, out float width, out float height)
        {
            width = 1f; height = 0f;
            if (car == null || !KitFitted(car)) return;
            LookForApi();
            if (_apiGet != null)
            {
                try
                {
                    var args = new object[] { car, 1f, 0f };
                    if ((bool)_apiGet.Invoke(null, args)) { width = (float)args[1]; height = (float)args[2]; }
                    return;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Suspension: Part Adjustment API: " + e.Message); }
            }
            var tune = car.GetComponent<SuspensionTune>();
            if (tune != null) { width = tune.Width; height = tune.Height; }
        }

        // a one-line picture of the running gear for the log (VerboseLog): what NWH really uses after the template's wheels went on
        internal static string Describe(GameObject car)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var wc in car.GetComponentsInChildren<WheelController>(true))
            {
                if (wc.transform.parent != car.transform) continue;
                sb.Append(sb.Length > 0 ? ", " : "").Append(wc.name.Replace("hinge_wheel_", "")).Append(" r ").Append(wc.Radius.ToString("0.00")).Append(" w ").Append(wc.Width.ToString("0.00"))
                  .Append(" at ").Append(wc.transform.localPosition.ToString("F2")).Append(" spring ").Append(wc.SpringMaxLength.ToString("0.00"));
            }
            return sb.ToString();
        }

        // Harmony: the vanilla Suspension FSM moves a mount (stock / lifted SetPosition) when the kit goes on or comes off and after a
        // load - our own tune (no Part Adjustment) re-applies on top of the new vanilla baseline, like Part Adjustment does for itself
        internal static void Install()
        {
            var target = AccessTools.Method(typeof(SetPosition), "DoSetPosition");
            if (target == null) { Plugin.Log.LogWarning("Suspension: SetPosition.DoSetPosition not found; template suspension needs Part Adjustment"); return; }
            new Harmony(Plugin.GUID + ".suspension").Patch(target, postfix: new HarmonyMethod(typeof(Suspension), nameof(AfterSetPosition)));
        }

        private static void AfterSetPosition(SetPosition __instance)
        {
            try
            {
                if (__instance.Fsm == null || __instance.Fsm.Name != "Suspension") return;
                var owner = __instance.Fsm.Owner;
                if (owner == null || !owner.name.StartsWith("hinge_wheel_", StringComparison.Ordinal) || owner.transform.parent == null) return;
                var tune = owner.transform.parent.GetComponent<SuspensionTune>();
                if (tune != null) tune.VanillaMountMoved(owner.transform, __instance.State != null ? __instance.State.Name : "");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Suspension: after SetPosition: " + e.Message); }
        }
    }

    // The geometry of a suspension adjustment without the Part Adjustment mod (its own SuspensionAdjustment is the reference; this follows
    // the same rules: model scale X / position Y, hinge Y, mounts = vanilla baseline + axle-end width offset + height). Never saved by the
    // game (Apocapatrol's own save data carries width / height per raider car).
    internal sealed class SuspensionTune : MonoBehaviour
    {
        internal float Width = 1f, Height;
        private Transform _model, _hinge;
        private Vector3 _modelPos, _modelScale, _hingePos;
        private readonly List<Mount> _mounts = new List<Mount>();
        private readonly List<Axle> _axles = new List<Axle>();
        private Rigidbody _body;
        private long _comSig; private float _nextCom; private bool _comShifted;

        private sealed class Mount { internal Transform T; internal Vector3 Baseline; internal float Center; internal bool Lifted; }
        private sealed class Axle { internal Transform Branch; internal bool Lifted; internal float Left, Right; internal Vector3 Center; }

        internal static SuspensionTune Ensure(GameObject car)
        {
            var t = car.GetComponent<SuspensionTune>();
            if (t != null) return t;
            var model = car.transform.Find("suspension_model");
            var hinge = Suspension.KitHinge(car);
            if (model == null || hinge == null) return null;
            var wheels = car.GetComponentsInChildren<WheelController>(true).Where(w => w.transform.parent == car.transform && w.name.StartsWith("hinge_wheel_", StringComparison.Ordinal)).ToArray();
            if (wheels.Length < 4) return null;
            foreach (var w in wheels) if (w.GetComponents<PlayMakerFSM>().All(f => f.FsmName != "Suspension")) return null;   // Rustliner / Rustcargo / Rustchief: dormant assemblies
            t = car.AddComponent<SuspensionTune>();
            t.Init(model, hinge, wheels);
            return t;
        }

        private void Init(Transform model, Transform hinge, WheelController[] wheels)
        {
            _model = model; _hinge = hinge; _body = GetComponent<Rigidbody>();
            _modelPos = model.localPosition; _modelScale = model.localScale; _hingePos = hinge.localPosition;
            foreach (var w in wheels)
            {
                var fsm = w.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Suspension" && f.enabled);
                _mounts.Add(new Mount { T = w.transform, Baseline = VanillaPosition(w.transform, fsm), Lifted = fsm != null && fsm.Fsm.Initialized && fsm.ActiveStateName == "lifted" });
            }
            RefreshCenters();
            // the axle meshes' ends in the model's own coordinates (bounds work on the game's non-readable meshes)
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                string n = mf.name.ToLowerInvariant();
                if (mf.sharedMesh == null || !n.Contains("suspension") || (!n.Contains("front") && !n.Contains("rear"))) continue;
                var branch = mf.transform;
                while (branch.parent != null && branch.parent != model) branch = branch.parent;
                bool lifted = branch.name == "lifted" || branch.name == "buggy";
                if (branch.parent != model || (!lifted && branch.name != "stock")) continue;
                var b = mf.sharedMesh.bounds; float left = float.PositiveInfinity, right = float.NegativeInfinity;
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                {
                    float v = model.InverseTransformPoint(mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3(x, y, z)))).x;
                    left = Mathf.Min(left, v); right = Mathf.Max(right, v);
                }
                _axles.Add(new Axle { Branch = branch, Lifted = lifted, Left = left, Right = right, Center = model.InverseTransformPoint(mf.transform.TransformPoint(b.center)) });
            }
        }

        // the mount position the vanilla FSM's active state writes (stock / lifted SetPosition) - authoritative even after a load
        private static Vector3 VanillaPosition(Transform wheel, PlayMakerFSM fsm)
        {
            if (fsm == null) return wheel.localPosition;
            try
            {
                string want = fsm.Fsm.Initialized ? fsm.ActiveStateName : "stock";
                var state = fsm.FsmStates.FirstOrDefault(s => s.Name == want) ?? fsm.FsmStates.FirstOrDefault(s => s.Name == "stock");
                if (state == null) return wheel.localPosition;
                var acts = state.Actions; if ((acts == null || acts.Length == 0)) { state.LoadActions(); acts = state.Actions; }
                var sp = acts != null ? acts.OfType<SetPosition>().FirstOrDefault(a => a.Enabled && a.space == Space.Self) : null;
                if (sp == null) return wheel.localPosition;
                var p = sp.vector != null && !sp.vector.IsNone ? sp.vector.Value : wheel.localPosition;
                if (sp.x != null && !sp.x.IsNone) p.x = sp.x.Value;
                if (sp.y != null && !sp.y.IsNone) p.y = sp.y.Value;
                if (sp.z != null && !sp.z.IsNone) p.z = sp.z.Value;
                return p;
            }
            catch (Exception) { return wheel.localPosition; }
        }

        private void RefreshCenters()
        {
            foreach (var m in _mounts)
            {
                Mount opp = null; float nearest = float.PositiveInfinity;
                foreach (var c in _mounts)
                {
                    if (c == m || Mathf.Sign(c.Baseline.x) == Mathf.Sign(m.Baseline.x)) continue;
                    float d = Mathf.Abs(c.Baseline.z - m.Baseline.z);
                    if (d < nearest) { opp = c; nearest = d; }
                }
                m.Center = opp == null ? 0f : (m.Baseline.x + opp.Baseline.x) * 0.5f;
            }
        }

        internal void Set(float width, float height) { Width = Mathf.Clamp(width, 1f, 1.5f); Height = Mathf.Clamp(height, -1f, 1f); Apply(); StandstillDamper.Attach(gameObject); }

        private void Apply()
        {
            if (_model == null || _hinge == null) return;
            if (!Suspension.KitFitted(gameObject) && (Width != 1f || Height != 0f)) { Width = 1f; Height = 0f; }
            _model.localScale = new Vector3(_modelScale.x * Width, _modelScale.y, _modelScale.z);
            _model.localPosition = _modelPos + Vector3.up * Height;
            _hinge.localPosition = _hingePos + _hinge.parent.InverseTransformVector(transform.TransformVector(Vector3.up * Height));
            foreach (var m in _mounts) ApplyMount(m);
            ApplyCenterOfMass(true);
            if (_body != null) _body.WakeUp();
        }

        // a lifted body (Height < 0 = mounts down, body up) keeps its centre of mass where it was: the Rigidbody's automatic centre (nothing
        // in the game or NWH sets one) lowered by SuspensionLiftCenterOfMass x the lift - same rule and default as Part Adjustment 1.2.1
        private void ApplyCenterOfMass(bool force)
        {
            if (_body == null) return;
            long sig = 17;
            foreach (var c in GetComponentsInChildren<Collider>(false)) if (c != null && c.enabled && !c.isTrigger) sig = sig * 31 + c.GetInstanceID();
            if (!force && sig == _comSig) return;
            _comSig = sig;
            float lift = Plugin.SuspensionLiftCenterOfMass.Value ? Mathf.Max(0f, -Height) : 0f;
            if (lift <= 0f) { if (_comShifted) { _body.ResetCenterOfMass(); _comShifted = false; } return; }
            _body.ResetCenterOfMass();
            _body.centerOfMass = _body.centerOfMass - Vector3.up * lift;
            _comShifted = true;
        }

        private void FixedUpdate()
        {
            if (!_comShifted || Time.time < _nextCom) return;
            _nextCom = Time.time + 1f;
            ApplyCenterOfMass(false);
        }

        private void ApplyMount(Mount m)
        {
            if (m.T == null) return;
            var p = m.Baseline;
            Vector3 off;
            if (AxleOffset(m, out off)) p += off;
            else p.x = m.Center + (p.x - m.Center) * Width;
            p.y += Height;
            if (!m.T.localPosition.Equals(p)) m.T.localPosition = p;
        }

        // the mount follows the end of the matching (stock / lifted) axle mesh as the model stretches: factory hub clearance kept
        private bool AxleOffset(Mount m, out Vector3 offset)
        {
            offset = Vector3.zero;
            Axle best = null; float dist = float.PositiveInfinity;
            foreach (var a in _axles)
            {
                if (a.Branch == null || a.Lifted != m.Lifted) continue;
                float d = Mathf.Abs(m.T.parent.InverseTransformPoint(_model.TransformPoint(a.Center)).z - m.Baseline.z);
                if (d < dist) { dist = d; best = a; }
            }
            if (best == null) return false;
            bool left = m.Baseline.x < m.T.parent.InverseTransformPoint(_model.position).x;
            float end = (left ? best.Left : best.Right) * _modelScale.x;
            var local = new Vector3(end * (Width - 1f), 0f, 0f);
            offset = m.T.parent.InverseTransformVector(_model.parent.TransformVector(_model.localRotation * local));
            return true;
        }

        // the vanilla FSM just wrote this mount (kit on / off, a load): new baseline, our offsets on top
        internal void VanillaMountMoved(Transform wheel, string state)
        {
            var m = _mounts.FirstOrDefault(x => x.T == wheel);
            if (m == null) return;
            m.Baseline = wheel.localPosition; m.Lifted = state == "lifted";
            RefreshCenters();
            if (!Suspension.KitFitted(gameObject) && (Width != 1f || Height != 0f)) { Apply(); return; }
            foreach (var x in _mounts) ApplyMount(x);
        }
    }

    // A parked car is held still (2.3.x, final form in 2.4.0). Why: the vanilla tyre model (NWH) applies its full sideways grip against any
    // sideways velocity, computed one physics step late. On stock cars the impulse is small and dies out; with 0.52 m truck wheels carrying
    // 5000 N on a lifted chassis it reverses the body's sideways velocity every step - the car "shakes" in place, wheel loads swapping sides
    // 50 times a second (measured). Nothing in the tyre or suspension parameters stops it safely, so the body is simply not allowed to move
    // while parked: below 0.3 m/s with no engine torque on the wheels, the Rigidbody's horizontal position and its roll / pitch are frozen
    // (the suspension still works vertically, the car still sits on slopes). Released when the engine drives a wheel, when something hits
    // the car, or when it moves anyway (above 0.5 m/s). Part Adjustment has the same component; whichever mod attached one first owns a car.
    internal sealed class StandstillDamper : MonoBehaviour
    {
        internal const float HoldSpeed = 0.3f, UnholdSpeed = 0.5f, DriveTorque = 30f, BumpImpulse = 50f;
        private Rigidbody _rb; private bool _held, _bumped; private RigidbodyConstraints _baseConstraints;
        private WheelController[] _wheels = new WheelController[0]; private float _nextWheels;

        internal static void Attach(GameObject car)
        {
            if (car == null) return;
            foreach (var c in car.GetComponents<Component>()) if (c != null && c.GetType().Name == "StandstillDamper") return;
            car.AddComponent<StandstillDamper>();
            Plugin.Verbose("Parking hold on " + car.name);
        }

        // the car the player drives (checked once a second from Patrol.Update)
        private static float _next;
        internal static void Tick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (Plugin.StandstillDamping.Value) Attach(PlayerRef.PlayerCar);
        }

        private void FixedUpdate()
        {
            if (_rb == null) { _rb = GetComponent<Rigidbody>(); if (_rb == null) { Destroy(this); return; } }
            if (!Plugin.StandstillDamping.Value || _rb.isKinematic) { Unhold(); return; }
            if (Time.time >= _nextWheels) Collect();
            float speed = _rb.velocity.magnitude;
            bool driving = false;
            for (int i = 0; i < _wheels.Length; i++) if (_wheels[i] != null && Mathf.Abs(_wheels[i].MotorTorque) > DriveTorque) { driving = true; break; }
            if (!_held && speed < HoldSpeed && !driving)
            {
                _baseConstraints = _rb.constraints;
                _rb.constraints = _baseConstraints | RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                _rb.angularVelocity = Vector3.zero;
                _rb.velocity = new Vector3(0f, _rb.velocity.y, 0f);
                _held = true;
            }
            else if (_held && (speed > UnholdSpeed || driving || _bumped)) Unhold();
            _bumped = false;
        }

        // the wheels (direct hinge_wheel_* children), re-read every 2 s: a wheel put on or taken off changes the set
        private void Collect()
        {
            _nextWheels = Time.time + 2f;
            var list = new List<WheelController>();
            foreach (var w in GetComponentsInChildren<WheelController>(true)) if (w != null && w.transform.parent == transform) list.Add(w);
            _wheels = list.ToArray();
        }
        private void OnCollisionEnter(Collision col) { if (col.impulse.magnitude > BumpImpulse) _bumped = true; }   // rammed, shot, blown...
        private void Unhold() { if (_held) { _rb.constraints = _baseConstraints; _held = false; } }
        private void OnDisable() { if (_rb != null) Unhold(); }
    }

    // The wheels of the car the player drives never touch the player (2.3.0). The game isolates the driver from the hull by layer (Drive 7
    // vs Car 8), but the wheel colliders - NWH's own wheel mesh colliders and the game's wheel_hub sphere (0.7 x the tyre radius) - are on
    // layer 2, which collides with the driver. Stock wheels never reach the seats; a raised suspension or big truck wheels push them into
    // the cabin, and PhysX shoving the driver and the car apart every step is the vibration. Twice a second: every layer-2 collider of the
    // player's car is ignored for the player's colliders (NWH rebuilds its colliders when the wheel size changes; a changed set re-applies).
    internal static class WheelContacts
    {
        private static float _next; private static long _sig; private static GameObject _car;
        private static readonly List<Collider> _p = new List<Collider>(), _c = new List<Collider>();

        internal static void Tick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            var car = PlayerRef.PlayerCar; var player = PlayerRef.Player;
            if (car == null || player == null) { _car = null; _sig = 0; return; }
            _p.Clear(); _c.Clear();
            player.GetComponentsInChildren(true, _p);
            car.GetComponentsInChildren(true, _c);
            long sig = car.GetInstanceID();
            foreach (var c in _p) if (c != null) sig = sig * 31 + c.GetInstanceID();
            foreach (var c in _c) if (c != null && c.gameObject.layer == 2) sig = sig * 31 + c.GetInstanceID();
            if (car == _car && sig == _sig) return;
            _car = car; _sig = sig;
            int n = 0;
            foreach (var c in _c)
            {
                if (c == null || c.gameObject.layer != 2 || c.transform.IsChildOf(player)) continue;
                foreach (var p in _p) if (p != null) { Physics.IgnoreCollision(p, c, true); n++; }
            }
            if (n > 0) Plugin.Verbose("Wheel contacts: " + n + " wheel collider pairs of " + car.name + " ignored for the player");
        }
    }
}

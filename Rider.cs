using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Apocapatrol
{
    // The blast-lance rider (2.1.0, prototype): a crew human standing on the roof of a raider car (template "rider" + "riderPos"), crouched,
    // who throws the game's blast lance (the blastlance_1_explode projectile the player's lance creates) at the player every few seconds.
    // Animation: Apocaplayer's humanoid clips when that mod is loaded (CrouchIdle looping, Throw as a one-shot) through a PlayableGraph on the
    // mob's own Animator; without Apocaplayer the mob keeps its idle and the lance is thrown without a swing.
    internal static class Rider
    {
        internal const string Projectile = "blastlance_1_explode";   // what the player's blast lance [Attack] FSM creates and pushes at 30 m/s
        internal const string LanceItem = "blastlance_1";             // the item, used as the visual in the rider's hand

        internal static GameObject Seat(GameObject car, PatrolMarker marker, string prefabName, float[] localPos)
        {
            var prefab = Prefabs.FindAny(prefabName);
            if (prefab == null) { Plugin.Log.LogWarning("Rider prefab not found: " + prefabName); return null; }
            var anchor = new GameObject("Apocapatrol.RiderPos").transform;
            anchor.SetParent(car.transform, false);
            anchor.localPosition = localPos != null && localPos.Length == 3 ? new Vector3(localPos[0], localPos[1], localPos[2]) : AutoSpot(car);
            anchor.localRotation = Quaternion.identity;

            var go = UnityEngine.Object.Instantiate(prefab, anchor.position, anchor.rotation);
            go.SetActive(true);
            Crew.MuteAi(go);                       // no mover / detection / attack FSMs: the guard does the throwing
            PassengerGuard.NeutralizeContact(go);  // melee / fire-contact damage FSMs and hitbox off (a passive body that can be shot)
            go.name = prefab.name + "(Rider)";
            var carCols = car.GetComponentsInChildren<Collider>(true);
            foreach (var a in go.GetComponentsInChildren<Collider>(true))
                foreach (var b in carCols)
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, true);
            var root = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            root.isKinematic = true;
            root.interpolation = RigidbodyInterpolation.None;
            go.transform.SetParent(anchor, true);
            marker.Rider = go; marker.RiderAnchor = anchor; marker.RiderPrefab = prefab.name;
            go.AddComponent<RiderGuard>().Init(car, marker, anchor, prefab.name);
            Plugin.Verbose("Rider: " + go.name + " on " + car.name + " at local " + anchor.localPosition.ToString("F2"));
            return go;
        }

        // the top of the car: the highest point of its renderers (parts included - plates on the roof count), centred, over the frame's middle
        internal static Vector3 AutoSpot(GameObject car)
        {
            var ct = car.transform;
            bool any = false; var b = new Bounds();
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                bool occupant = false;
                for (var a = r.transform; a != null && a != ct; a = a.parent)
                    if (a.name.IndexOf("(Driver)", StringComparison.Ordinal) >= 0 || a.name.IndexOf("(Passenger)", StringComparison.Ordinal) >= 0 || a.name.IndexOf("(Rider)", StringComparison.Ordinal) >= 0 || a.name == "PhysicsLock") { occupant = true; break; }
                if (occupant) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return new Vector3(0f, 1.2f, 0f);
            var top = ct.InverseTransformPoint(new Vector3(b.center.x, b.max.y, b.center.z));
            return new Vector3(0f, top.y + 0.02f, top.z);
        }

        // Apocaplayer's humanoid clips (Apocaplayer.Anims.Get(name)) - null without the mod or the clip
        private static bool _animsLooked; private static MethodInfo _animsGet;
        internal static AnimationClip Clip(string name)
        {
            try
            {
                if (!_animsLooked)
                {
                    _animsLooked = true;
                    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Apocaplayer");
                    var t = asm != null ? asm.GetType("Apocaplayer.Anims") : null;
                    _animsGet = t != null ? t.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    Plugin.Verbose("Rider: Apocaplayer animations " + (_animsGet != null ? "available" : "not found (no crouch / throw clips)"));
                }
                return _animsGet != null ? _animsGet.Invoke(null, new object[] { name }) as AnimationClip : null;
            }
            catch (Exception e) { Plugin.Verbose("Rider: clip " + name + ": " + e.Message); return null; }
        }

        internal static Transform Bone(Transform root, string bone)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var n = t.name; int i = n.LastIndexOf(':'); if (i < 0) i = n.LastIndexOf('_');
                if ((i >= 0 ? n.Substring(i + 1) : n) == bone) return t;
            }
            return null;
        }

        internal static Vector3 ParseV3(string s, Vector3 def)
        {
            try
            {
                var p = (s ?? "").Split(',');
                if (p.Length != 3) return def;
                return new Vector3(float.Parse(p[0].Trim(), System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture), float.Parse(p[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception) { return def; }
        }
        internal static string FormatV3(Vector3 v) { return v.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " + v.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ", " + v.z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
    }

    internal sealed class RiderGuard : MonoBehaviour
    {
        private GameObject _car; private PatrolMarker _marker; private Transform _anchor; private string _prefab;
        private PlayMakerFSM _health; private FsmFloat _healthVar;
        private Rigidbody _carRb;
        private Animator _anim;
        private PlayableGraph _graph; private bool _graphOk;
        private AnimationMixerPlayable _mixer; private AnimationClipPlayable _idle, _throw;
        private float _throwLen = 1.2f, _blend;
        private bool _throwing, _released, _dead;
        private float _throwStart, _nextThrow, _nextHide, _lanceBackAt;
        private Transform _hand; internal Transform Lance;   // the visual lance in the right hand (RiderApi moves it)
        private readonly List<Renderer> _ownWeapons = new List<Renderer>();
        private const float ReleaseFraction = 0.42f;   // where in the Throw clip the lance leaves the hand

        internal void Init(GameObject car, PatrolMarker marker, Transform anchor, string prefab)
        {
            _car = car; _marker = marker; _anchor = anchor; _prefab = prefab;
            _carRb = car.GetComponent<Rigidbody>();
            _health = GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Health");
            _nextThrow = Time.time + UnityEngine.Random.Range(2f, 4f);
            _anim = GetComponentInChildren<Animator>();
            var lHand = Rider.Bone(transform, "LeftHand"); _hand = Rider.Bone(transform, "RightHand") ?? lHand;
            foreach (var h in new[] { lHand, _hand }) if (h != null) foreach (var r in h.GetComponentsInChildren<Renderer>(true)) _ownWeapons.Add(r);
            HideOwnWeapons();
            BuildGraph();
            BuildLance();
        }

        private void BuildGraph()
        {
            try
            {
                var idle = Rider.Clip("CrouchIdle") ?? Rider.Clip("RifleCrouchIdle") ?? Rider.Clip("Idle");
                var thr = Rider.Clip("Throw");
                if (_anim == null || idle == null) { if (_anim == null) Plugin.Verbose("Rider: no Animator on " + name); return; }
                if (!_anim.isHuman) { Plugin.Verbose("Rider: " + name + "'s Animator is not humanoid - Apocaplayer clips cannot drive it"); return; }
                _anim.applyRootMotion = false;
                _graph = PlayableGraph.Create("Apocapatrol.Rider");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                var output = AnimationPlayableOutput.Create(_graph, "rider", _anim);
                _mixer = AnimationMixerPlayable.Create(_graph, 2);
                _idle = AnimationClipPlayable.Create(_graph, idle);
                _throw = AnimationClipPlayable.Create(_graph, thr ?? idle);
                _throwLen = thr != null ? Mathf.Max(0.4f, thr.length) : 1.2f;
                _graph.Connect(_idle, 0, _mixer, 0); _graph.Connect(_throw, 0, _mixer, 1);
                _mixer.SetInputWeight(0, 1f); _mixer.SetInputWeight(1, 0f);
                _throw.Pause();
                output.SetSourcePlayable(_mixer);
                _graph.Play();
                _graphOk = true;
                Plugin.Verbose("Rider: " + name + " animated with " + idle.name + (thr != null ? " + " + thr.name + " (" + _throwLen.ToString("0.00") + " s)" : " (no Throw clip)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: animation graph: " + e.Message); _graphOk = false; }
        }

        // the blast lance item as a prop in the throwing hand: no FSMs, no physics, no colliders
        private void BuildLance()
        {
            if (_hand == null) return;
            var prefab = Prefabs.FindAny(Rider.LanceItem);
            if (prefab == null) { Plugin.Verbose("Rider: no " + Rider.LanceItem + " prefab for the hand prop"); return; }
            var go = UnityEngine.Object.Instantiate(prefab, _hand.position, _hand.rotation);
            foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
            foreach (var j in go.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(j);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true)) if (b != null) UnityEngine.Object.DestroyImmediate(b);
            go.name = "RiderLance";
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;
            go.SetActive(true);
            go.transform.SetParent(_hand, false);
            Lance = go.transform;
            ApplyLanceOffset();
        }

        internal void ApplyLanceOffset()
        {
            if (Lance == null) return;
            Lance.localPosition = Rider.ParseV3(Plugin.RiderLanceOffset.Value, Vector3.zero);
            Lance.localRotation = Quaternion.Euler(Rider.ParseV3(Plugin.RiderLanceRotation.Value, Vector3.zero));
        }

        private void HideOwnWeapons()
        {
            foreach (var r in _ownWeapons) if (r != null && r.enabled && (Lance == null || !r.transform.IsChildOf(Lance))) r.enabled = false;
        }

        private bool Alive()
        {
            if (transform.parent == null) return false;
            if (_health != null && _health.Fsm.Initialized)
            {
                if (_healthVar == null) _healthVar = _health.FsmVariables.GetFsmFloat("Health");
                if (_healthVar != null && _healthVar.Value <= 0f) return false;
            }
            return true;
        }

        private void Update()
        {
            if (_car == null || _anchor == null) { Destroy(this); return; }
            if (Time.timeScale <= 0f) return;
            if (!_dead && !Alive())
            {
                _dead = true;
                if (_graphOk) { _graph.Destroy(); _graphOk = false; }   // the ragdoll / death takes over
                if (Lance != null) Destroy(Lance.gameObject);
                if (_marker != null && _marker.Rider == gameObject) _marker.Rider = null;
                return;
            }
            if (_dead) return;
            if (Time.time >= _nextHide) { _nextHide = Time.time + 0.5f; HideOwnWeapons(); }
            if (!Plugin.RiderLances.Value) { Face(_car.transform.forward); Blend(0f); return; }

            Vector3 tpos, tvel; GameObject tcar;
            bool has = PlayerRef.Target(out tpos, out tvel, out tcar);
            var to = tpos - transform.position; to.y = 0f;
            float dist = has ? to.magnitude : 9999f;
            Face(has && dist < Plugin.RiderRange.Value * 1.5f && to.sqrMagnitude > 0.01f ? to : _car.transform.forward);

            if (!_throwing && has && dist <= Plugin.RiderRange.Value && dist > 3f && Time.time >= _nextThrow && (_marker == null || !_marker.Exploded))
            {
                _throwing = true; _released = false; _throwStart = Time.time;
                if (_graphOk) { _throw.SetTime(0.0); _throw.Play(); }
                if (Lance != null) Lance.gameObject.SetActive(true);
            }
            if (_throwing)
            {
                float t = Time.time - _throwStart;
                float len = _graphOk ? _throwLen : 0.9f;
                if (!_released && t >= len * ReleaseFraction) { _released = true; Release(tpos, tvel); }
                if (t >= len)
                {
                    _throwing = false;
                    if (_graphOk) _throw.Pause();
                    _nextThrow = Time.time + UnityEngine.Random.Range(Plugin.RiderIntervalMin.Value, Mathf.Max(Plugin.RiderIntervalMin.Value, Plugin.RiderIntervalMax.Value));
                    _lanceBackAt = Time.time + 1.2f;   // "picks the next lance up"
                }
            }
            else if (Lance != null && !Lance.gameObject.activeSelf && Time.time >= _lanceBackAt) Lance.gameObject.SetActive(true);
            Blend(_throwing ? 1f : 0f);
        }

        private void Face(Vector3 dir)
        {
            var up = _car.transform.up;
            dir = Vector3.ProjectOnPlane(dir, up);
            if (dir.sqrMagnitude < 1e-4f) return;
            var want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, 240f * Time.deltaTime);
        }

        private void Blend(float target)
        {
            if (!_graphOk) return;
            _blend = Mathf.MoveTowards(_blend, target, Time.deltaTime / 0.15f);
            _mixer.SetInputWeight(0, 1f - _blend); _mixer.SetInputWeight(1, _blend);
        }

        // the lance leaves the hand: the player's projectile prefab, thrown at the lead point with a ballistic lift, carrying the car's speed
        private void Release(Vector3 tpos, Vector3 tvel)
        {
            try
            {
                if (Lance != null) Lance.gameObject.SetActive(false);
                var prefab = Prefabs.FindAny(Rider.Projectile);
                if (prefab == null) { Plugin.Log.LogWarning("Rider: projectile prefab not found: " + Rider.Projectile); return; }
                float speed = Mathf.Max(5f, Plugin.RiderLanceSpeed.Value);
                var from = (_hand != null ? _hand.position : transform.position + Vector3.up * 1.2f);
                var carVel = _carRb != null ? _carRb.velocity : Vector3.zero;
                float flight = Mathf.Clamp((tpos - from).magnitude / speed, 0.1f, 3f);
                var aim = tpos + Vector3.up * 0.9f + (tvel - carVel) * flight * 0.8f;   // lead on the relative movement
                var flat = aim - from; float dy = flat.y; flat.y = 0f; float d = flat.magnitude;
                float g = Mathf.Abs(Physics.gravity.y);
                // low-arc launch angle for a flat throw, then the height difference on top
                float s = Mathf.Clamp(g * d / (speed * speed), -1f, 1f);
                float angle = 0.5f * Mathf.Asin(s) + Mathf.Atan2(dy, Mathf.Max(d, 0.1f)) * 0.5f;
                var dir = (flat.normalized * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)).normalized;
                var start = from + dir * 0.6f;
                var go = UnityEngine.Object.Instantiate(prefab, start, Quaternion.LookRotation(dir));
                go.SetActive(true);
                var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.velocity = dir * speed + carVel;
                var mine = go.GetComponentsInChildren<Collider>(true);
                foreach (var c in _car.GetComponentsInChildren<Collider>(true)) foreach (var p in mine) if (c != null && p != null) Physics.IgnoreCollision(p, c, true);
                Plugin.Verbose("Rider: " + name + " threw a blast lance at " + d.ToString("0") + " m (" + (angle * Mathf.Rad2Deg).ToString("0") + "° up)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: throw: " + e.Message); }
        }

        private void OnDestroy()
        {
            if (_graphOk) { try { _graph.Destroy(); } catch (Exception) { } _graphOk = false; }
            if (Lance != null) Destroy(Lance.gameObject);
        }
    }

    // What the rider-spot tweaker (Apocaspotter, a separate plugin) uses. Public on purpose.
    public static class RiderApi
    {
        public static bool InGame() { var p = UnityEngine.Object.FindObjectOfType<Patrol>(); return p != null && p.InGame(); }

        // every live raider car that has a rider spot
        public static List<GameObject> Cars()
        {
            var list = new List<GameObject>();
            foreach (var m in PatrolMarker.All) if (m != null && m.RiderAnchor != null) list.Add(m.gameObject);
            return list;
        }
        public static Transform Anchor(GameObject car) { var m = car != null ? car.GetComponent<PatrolMarker>() : null; return m != null ? m.RiderAnchor : null; }
        public static GameObject Rider(GameObject car) { var m = car != null ? car.GetComponent<PatrolMarker>() : null; return m != null ? m.Rider : null; }
        public static Transform Lance(GameObject car) { var r = Rider(car); var g = r != null ? r.GetComponent<RiderGuard>() : null; return g != null ? g.Lance : null; }
        public static string TemplateName(GameObject car) { var m = car != null ? car.GetComponent<PatrolMarker>() : null; return m != null ? m.TemplateName : ""; }

        public static void MoveAnchor(GameObject car, Vector3 localDelta) { var a = Anchor(car); if (a != null) a.localPosition += localDelta; }

        // writes the anchor's frame-local position into the car's template file as "riderPos"
        public static string SaveAnchor(GameObject car)
        {
            var a = Anchor(car); string name = TemplateName(car);
            if (a == null || string.IsNullOrEmpty(name)) return "no rider spot / template on this car";
            var t = CarTemplate.Find(name);
            if (t == null || string.IsNullOrEmpty(t.SourcePath) || !System.IO.File.Exists(t.SourcePath)) return "template file of " + name + " not found";
            var file = TemplateFile.FromJson(System.IO.File.ReadAllText(t.SourcePath));
            var p = a.localPosition;
            file.riderPos = new[] { Round(p.x), Round(p.y), Round(p.z) };
            EditorStore.AtomicWrite(t.SourcePath, file.ToJson());
            CarTemplates.Refresh(true);
            return "riderPos " + Apocapatrol.Rider.FormatV3(p) + " saved into " + System.IO.Path.GetFileName(t.SourcePath);
        }

        // the lance prop's offset in the throwing hand (every live rider), saved into the Apocapatrol config
        public static Vector3 LanceOffset
        {
            get { return Apocapatrol.Rider.ParseV3(Plugin.RiderLanceOffset.Value, Vector3.zero); }
            set { Plugin.RiderLanceOffset.Value = Apocapatrol.Rider.FormatV3(value); ApplyLances(); }
        }
        public static Vector3 LanceEuler
        {
            get { return Apocapatrol.Rider.ParseV3(Plugin.RiderLanceRotation.Value, Vector3.zero); }
            set { Plugin.RiderLanceRotation.Value = Apocapatrol.Rider.FormatV3(value); ApplyLances(); }
        }
        private static void ApplyLances() { foreach (var g in UnityEngine.Object.FindObjectsOfType<RiderGuard>()) g.ApplyLanceOffset(); }
        public static string SaveLance() { Plugin.SaveConfig(); return "lance offset " + Plugin.RiderLanceOffset.Value + " / rotation " + Plugin.RiderLanceRotation.Value + " saved into the Apocapatrol config"; }

        private static float Round(float v) { return Mathf.Round(v * 1000f) / 1000f; }
    }
}

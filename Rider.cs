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

        // Intercept with gravity: the launch velocity (relative to the thrower's own velocity baseVel) of the given speed that meets a target
        // moving at targetVel. The flattest (earliest) solution. False when the speed cannot reach it - launch is then the best try.
        internal static bool Solve(Vector3 from, Vector3 baseVel, Vector3 target, Vector3 targetVel, float speed, out Vector3 launch, out float time)
        {
            var g = Physics.gravity;
            float prevT = 0f, bestMag = float.MaxValue, bestT = 1f;
            for (float t = 0.04f; t <= 4f; t += 0.04f)
            {
                float mag = U(from, baseVel, target, targetVel, g, t).magnitude;
                if (mag < bestMag) { bestMag = mag; bestT = t; }
                if (mag <= speed)
                {
                    float a = prevT > 0f ? prevT : t * 0.5f, b = t;
                    for (int i = 0; i < 14; i++) { float m = (a + b) * 0.5f; if (U(from, baseVel, target, targetVel, g, m).magnitude <= speed) b = m; else a = m; }
                    launch = U(from, baseVel, target, targetVel, g, b); time = b;
                    return true;
                }
                prevT = t;
            }
            var u = U(from, baseVel, target, targetVel, g, bestT);
            launch = u.normalized * speed; time = bestT;
            return false;
        }
        private static Vector3 U(Vector3 from, Vector3 baseVel, Vector3 target, Vector3 targetVel, Vector3 g, float t)
        {
            return (target + targetVel * t - from - baseVel * t - 0.5f * g * t * t) / t;
        }
        // The blast lance's own explosion: what the projectile's [Explosion] FSM creates in its "explode" state (CreateObject), read once from the
        // prefab's FSM. Spawned directly, it always goes off - no trigger contact or FSM event needed.
        private static GameObject _explosion; private static bool _explosionLooked;
        internal static GameObject ExplosionPrefab()
        {
            if (_explosionLooked) return _explosion;
            _explosionLooked = true;
            try
            {
                var lance = Prefabs.FindAny(Projectile);
                var fsm = lance != null ? lance.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Explosion") : null;
                if (fsm != null && fsm.Fsm != null)
                    foreach (var st in fsm.Fsm.States)
                    {
                        if (st.Name != "explode") continue;
                        var acts = st.Actions;
                        if (acts == null || acts.Length == 0) { st.LoadActions(); acts = st.Actions; }
                        if (acts != null) foreach (var a in acts) { var c = a as HutongGames.PlayMaker.Actions.CreateObject; if (c != null && c.gameObject != null && c.gameObject.Value != null) { _explosion = c.gameObject.Value; break; } }
                    }
                Plugin.Verbose("Rider: blast lance explosion = " + (_explosion != null ? _explosion.name : "not found (falls back to setting off a lance)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: explosion lookup: " + e.Message); }
            return _explosion;
        }

        // a blast-lance explosion at the point, every time: the explosion object itself, or (not found) a lance set off by its FSM
        internal static void Boom(Vector3 at)
        {
            var ex = ExplosionPrefab();
            if (ex != null) { var go = UnityEngine.Object.Instantiate(ex, at, Quaternion.identity); go.SetActive(true); return; }
            var prefab = Prefabs.FindAny(Projectile);
            if (prefab == null) return;
            var bomb = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            bomb.SetActive(true);
            var rb = bomb.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true;
            bomb.AddComponent<Detonator>();
        }

        // 2.1.4 "Witness me!" leap: a long, flat jump - the ground speed relative to the thrower's car is fixed (hSpeed), the flight time is
        // when that ground track meets the target's predicted ground track, and the vertical part just has to land on it (a low arc: no lob).
        // false when the target runs away faster than that, the jump would take over 1.6 s, or it would need more than 35 degrees up.
        internal static bool FlatLeap(Vector3 from, Vector3 baseVel, Vector3 target, Vector3 targetVel, float hSpeed, out Vector3 launch, out float time)
        {
            var r = target - from; r.y = 0f;
            var w = targetVel - baseVel; w.y = 0f;
            float a = Vector3.Dot(w, w) - hSpeed * hSpeed, b = 2f * Vector3.Dot(r, w), c = Vector3.Dot(r, r);
            time = -1f;
            if (Mathf.Abs(a) < 1e-4f) { if (b < 0f) time = -c / b; }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc >= 0f)
                {
                    float sq = Mathf.Sqrt(disc), t1 = (-b - sq) / (2f * a), t2 = (-b + sq) / (2f * a);
                    float lo = Mathf.Min(t1, t2), hi = Mathf.Max(t1, t2);
                    time = lo > 0.05f ? lo : hi > 0.05f ? hi : -1f;
                }
            }
            if (time <= 0f) { launch = Vector3.zero; return false; }
            time = Mathf.Max(time, 0.25f);
            launch = U(from, baseVel, target, targetVel, Physics.gravity, time);
            return time <= 1.6f && Elevation(launch) <= 35f;
        }

        // 2.1.4: the rider's own blast - no lance, no FSM event, nothing that can fail to go off: the exploder zombie's blast effect (sound,
        // fire, physics shove of loose things) at the spot, our damage to the player within RiderBlastRadius (full at the centre, a third at
        // the edge; in the car too) and a shove of the player's car away from it
        internal static void Kaboom(Vector3 at, string who)
        {
            try { Explode.BlastAt(at); } catch (Exception e) { Plugin.Log.LogWarning("Rider: blast effect: " + e.Message); }
            try
            {
                float radius = Plugin.RiderBlastRadius.Value;
                var player = PlayerRef.Player;
                if (player == null) return;
                float d = Vector3.Distance(player.position + Vector3.up * 0.9f, at);
                var car = PlayerRef.PlayerCar;
                var crb = car != null ? car.GetComponent<Rigidbody>() : null;
                if (crb != null) d = Mathf.Min(d, Mathf.Max(0f, Vector3.Distance(crb.worldCenterOfMass, at) - 2f));   // the car's hull is ~2 m around its centre
                if (d <= radius) Ram.HurtPlayer(Plugin.RiderBlastDamage.Value * Mathf.Lerp(1f, 0.33f, d / radius), "Rider " + who + " blast");
                if (crb != null && !crb.isKinematic && d <= radius) crb.AddExplosionForce(crb.mass * 5f, at, radius * 1.5f, 0.5f, ForceMode.Impulse);
                Plugin.Verbose("Rider: " + who + " blast at " + at + ", the player " + d.ToString("0.0") + " m away");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: blast damage: " + e.Message); }
        }

        internal static float Elevation(Vector3 v) { return Mathf.Asin(Mathf.Clamp(v.normalized.y, -1f, 1f)) * Mathf.Rad2Deg; }

        // "Witness me!" - Sounds/witness-me.wav (16-bit PCM, normalised to the game's human_hurt voice: -23.8 LUFS, peak -5.6 dBTP) as a
        // 3D voice on the rider
        private static AudioClip _scream; private static bool _screamLooked;
        // plays the scream; returns its length (1.5 s when the wav is missing: the rider still pauses before leaping)
        internal static float Scream(Transform at)
        {
            try
            {
                if (!_screamLooked)
                {
                    _screamLooked = true;
                    var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Rider).Assembly.Location) ?? ".", "Sounds", "witness-me.wav");
                    _scream = Wav.Load(path, "witness-me", 1.8f);   // +5 dB over the normalised file (peak about -0.5 dBFS)
                    if (_scream == null) Plugin.Log.LogWarning("Rider: " + path + " not found or not a PCM wav - the rider leaps silently");
                }
                if (_scream == null || at == null) return 1.5f;
                var go = new GameObject("Apocapatrol.WitnessMe");
                go.transform.SetParent(at, false); go.transform.localPosition = Vector3.up * 1.6f;
                var src = go.AddComponent<AudioSource>();
                // loud and far: full volume within 35 m, still clearly heard at 200+ m (mostly 3D, a little 2D so distance doesn't swallow it)
                src.clip = _scream; src.spatialBlend = 0.8f; src.rolloffMode = AudioRolloffMode.Logarithmic; src.minDistance = 35f; src.maxDistance = 400f;
                src.volume = 1f; src.priority = 64; src.dopplerLevel = 0f;
                src.Play();
                UnityEngine.Object.Destroy(go, _scream.length + 0.2f);
                return _scream.length;
            }
            catch (Exception e) { Plugin.Verbose("Rider: scream: " + e.Message); return 1.5f; }
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
        private enum Mode { Riding, Throwing, Witness, Flying, Dismount, Done }
        private GameObject _car; private PatrolMarker _marker; private Transform _anchor; private string _prefab;
        private PlayMakerFSM _health; private FsmFloat _healthVar; private float _maxHealth = -1f;
        private Rigidbody _carRb;
        private Animator _anim;
        private PlayableGraph _graph; private bool _graphOk;
        private AnimationMixerPlayable _mixer; private AnimationClipPlayable _idle, _throw, _jump;
        private float _throwLen = 1.2f, _jumpLen = 1f;
        private readonly float[] _w = new float[3];
        private Mode _mode = Mode.Riding;
        private bool _released, _inClose, _kamikazeArmed;
        private float _nextWitness;
        private Pilot _pilot;
        private float _modeStart, _nextThrow, _nextHide, _lanceBackAt;
        private Vector3 _vel;                       // flight velocity (witness jump / dismount hop)
        private float _side = 1f;
        private Transform _hand; internal Transform Lance;   // the visual lance in the right hand (RiderApi moves it)
        private readonly List<Renderer> _ownWeapons = new List<Renderer>();
        private Collider[] _carCols = new Collider[0], _myCols = new Collider[0];
        private static readonly RaycastHit[] _hits = new RaycastHit[16];
        private const float ReleaseFraction = 0.42f;   // where in the Throw clip the lance leaves the hand
        private const float AfterScream = 0.35f;       // pause between the end of "Witness me!" and the leap, s - the player's chance to get away
        private float _windUp = 1.85f;

        internal void Init(GameObject car, PatrolMarker marker, Transform anchor, string prefab)
        {
            _car = car; _marker = marker; _anchor = anchor; _prefab = prefab;
            _carRb = car.GetComponent<Rigidbody>();
            _health = GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Health");
            _nextThrow = Time.time + UnityEngine.Random.Range(2f, 4f);
            _anim = GetComponentInChildren<Animator>();
            var lHand = Rider.Bone(transform, "LeftHand"); _hand = Rider.Bone(transform, "RightHand") ?? lHand;
            foreach (var h in new[] { lHand, _hand }) if (h != null) foreach (var r in h.GetComponentsInChildren<Renderer>(true)) _ownWeapons.Add(r);
            _carCols = car.GetComponentsInChildren<Collider>(true);
            _myCols = GetComponentsInChildren<Collider>(true);
            _side = Vector3.Dot(anchor.position - car.transform.position, car.transform.right) >= 0f ? 1f : (UnityEngine.Random.value < 0.5f ? -1f : 1f);
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
                var jump = Rider.Clip("Jump");
                if (_anim == null || idle == null) { if (_anim == null) Plugin.Verbose("Rider: no Animator on " + name); return; }
                if (!_anim.isHuman) { Plugin.Verbose("Rider: " + name + "'s Animator is not humanoid - Apocaplayer clips cannot drive it"); return; }
                _anim.applyRootMotion = false;
                _graph = PlayableGraph.Create("Apocapatrol.Rider");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                var output = AnimationPlayableOutput.Create(_graph, "rider", _anim);
                _mixer = AnimationMixerPlayable.Create(_graph, 3);
                _idle = AnimationClipPlayable.Create(_graph, idle);
                _throw = AnimationClipPlayable.Create(_graph, thr ?? idle);
                _jump = AnimationClipPlayable.Create(_graph, jump ?? idle);
                _throwLen = thr != null ? Mathf.Max(0.4f, thr.length) : 1.2f;
                _jumpLen = jump != null ? Mathf.Max(0.3f, jump.length) : 1f;
                _graph.Connect(_idle, 0, _mixer, 0); _graph.Connect(_throw, 0, _mixer, 1); _graph.Connect(_jump, 0, _mixer, 2);
                _w[0] = 1f; _mixer.SetInputWeight(0, 1f); _mixer.SetInputWeight(1, 0f); _mixer.SetInputWeight(2, 0f);
                _throw.Pause(); _jump.Pause();
                output.SetSourcePlayable(_mixer);
                _graph.Play();
                _graphOk = true;
                Plugin.Verbose("Rider: " + name + " animated with " + idle.name + (thr != null ? " + " + thr.name : " (no Throw)") + (jump != null ? " + " + jump.name : " (no Jump)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: animation graph: " + e.Message); _graphOk = false; }
        }

        // 0 = crouch idle, 1 = throw, 2 = jump (one-shots restart from 0)
        private void Play(int clip)
        {
            if (!_graphOk) return;
            if (clip == 1) { _throw.SetTime(0.0); _throw.Play(); }
            if (clip == 2) { _jump.SetTime(0.0); _jump.Play(); }
        }

        private void Blend(int target, float seconds)
        {
            if (!_graphOk) return;
            float step = Time.deltaTime / Mathf.Max(0.01f, seconds);
            for (int i = 0; i < 3; i++) { _w[i] = Mathf.MoveTowards(_w[i], i == target ? 1f : 0f, step); _mixer.SetInputWeight(i, _w[i]); }
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

        private float Health()
        {
            if (_health == null || !_health.Fsm.Initialized) return -1f;
            if (_healthVar == null) _healthVar = _health.FsmVariables.GetFsmFloat("Health");
            if (_healthVar == null) return -1f;
            if (_maxHealth < 0f || _healthVar.Value > _maxHealth) _maxHealth = _healthVar.Value;
            return _healthVar.Value;
        }
        private bool Alive() { float h = Health(); return h < 0f || h > 0f; }
        private bool Wounded() { float h = Health(); return h > 0f && _maxHealth > 0f && h < _maxHealth - 0.5f; }

        private void Update()
        {
            if (_mode == Mode.Done) return;
            if (Time.timeScale <= 0f) return;
            if (_mode == Mode.Flying) { Fly(); return; }
            if (_mode == Mode.Dismount) { Hop(); return; }
            if (_car == null || _anchor == null) { Destroy(this); return; }
            if (transform.parent == null || !Alive()) { Die(); return; }
            if (Time.time >= _nextHide) { _nextHide = Time.time + 0.5f; HideOwnWeapons(); }

            // the player took this car, or it blew up: off the roof
            if (PlayerRef.PlayerCar == _car) { JumpOff("the player took the car"); return; }

            Vector3 tpos, tvel; GameObject tcar;
            bool has = PlayerRef.Target(out tpos, out tvel, out tcar);
            var to = tpos - transform.position; to.y = 0f;
            float dist = has ? to.magnitude : 9999f;

            if (_mode == Mode.Witness)
            {
                Face(to.sqrMagnitude > 0.01f ? to : _car.transform.forward);
                Blend(0, 0.15f);
                Escort();
                if (Time.time - _modeStart >= _windUp)
                {
                    // aimed at where the player is NOW and will be; only a jump that lands goes - else he waits (the driver closes in)
                    Vector3 launch; float flight;
                    if (CanLeap(tvel, tcar, out launch, out flight)) Leap(launch, flight);
                    else if (Time.time - _modeStart > _windUp + 5f)
                    {
                        _mode = Mode.Riding; _nextWitness = Time.time + 3f;
                        Plugin.Verbose("Rider: " + name + " - no jump that can hit, back to throwing");
                    }
                }
                return;
            }

            Face(has && dist < Plugin.RiderRange.Value * 1.5f && to.sqrMagnitude > 0.01f ? to : _car.transform.forward);

            // "Witness me!": the player's car comes within throwing range - always when this rider is wounded, else WitnessMeChance % once per
            // approach. Armed, the driver pulls up alongside, matching the player's heading and speed (Pilot escort); the scream starts once the
            // player is within RiderJumpRange, both cars go the same way (not head-on / apart) and a flat jump lands on the player's car.
            if (Plugin.RiderLances.Value && has && tcar != null && _mode == Mode.Riding)
            {
                float near = (LeapAim(tcar) - transform.position).magnitude;
                if (near <= Plugin.RiderRange.Value)
                {
                    if (!_inClose) { _inClose = true; _kamikazeArmed = UnityEngine.Random.Range(0f, 100f) < Plugin.WitnessMeChance.Value; }
                    if (_kamikazeArmed || Wounded())
                    {
                        Escort();
                        Vector3 launch; float flight;
                        if (near <= Plugin.RiderJumpRange.Value && Time.time >= _nextWitness && CanLeap(tvel, tcar, out launch, out flight)) { StartWitness(); return; }
                    }
                }
                else if (near > Plugin.RiderRange.Value + 8f) _inClose = false;
            }
            else _inClose = false;

            if (!Plugin.RiderLances.Value) { Blend(0, 0.15f); return; }

            if (_mode == Mode.Riding && has && dist > 3f && dist <= Plugin.RiderRange.Value && Time.time >= _nextThrow && (_marker == null || !_marker.Exploded))
            {
                // only within the lance's real reach: a flat throw (<= RiderMaxThrowAngle up) at the lance's speed must hit the predicted spot
                Vector3 launch; float flight;
                var from = HandPos();
                if (Rider.Solve(from, CarVel(), tpos + Vector3.up * 0.9f, tvel, Plugin.RiderLanceSpeed.Value, out launch, out flight)
                    && Rider.Elevation(launch) <= Plugin.RiderMaxThrowAngle.Value)
                {
                    _mode = Mode.Throwing; _released = false; _modeStart = Time.time;
                    Play(1);
                    if (Lance != null) Lance.gameObject.SetActive(true);
                }
                else _nextThrow = Time.time + 0.5f;   // out of reach: look again shortly
            }
            if (_mode == Mode.Throwing)
            {
                float t = Time.time - _modeStart;
                float len = _graphOk ? _throwLen : 0.9f;
                if (!_released && t >= len * ReleaseFraction) { _released = true; Release(tpos, tvel); }
                if (t >= len)
                {
                    _mode = Mode.Riding;
                    if (_graphOk) _throw.Pause();
                    _nextThrow = Time.time + UnityEngine.Random.Range(Plugin.RiderIntervalMin.Value, Mathf.Max(Plugin.RiderIntervalMin.Value, Plugin.RiderIntervalMax.Value));
                    _lanceBackAt = Time.time + 1.2f;   // "picks the next lance up"
                }
            }
            else if (Lance != null && !Lance.gameObject.activeSelf && Time.time >= _lanceBackAt) Lance.gameObject.SetActive(true);
            Blend(_mode == Mode.Throwing ? 1 : 0, 0.15f);
        }

        private Vector3 HandPos() { return _hand != null ? _hand.position : transform.position + Vector3.up * 1.2f; }
        private Vector3 CarVel() { return _carRb != null ? _carRb.velocity : Vector3.zero; }

        private void Face(Vector3 dir)
        {
            var up = _car != null ? _car.transform.up : Vector3.up;
            dir = Vector3.ProjectOnPlane(dir, up);
            if (dir.sqrMagnitude < 1e-4f) return;
            var want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, 240f * Time.deltaTime);
        }

        // the lance leaves the hand: the player's projectile prefab on the intercept course (predicted target, gravity, the car's speed)
        private void Release(Vector3 tpos, Vector3 tvel)
        {
            try
            {
                if (Lance != null) Lance.gameObject.SetActive(false);
                var prefab = Prefabs.FindAny(Rider.Projectile);
                if (prefab == null) { Plugin.Log.LogWarning("Rider: projectile prefab not found: " + Rider.Projectile); return; }
                float speed = Plugin.RiderLanceSpeed.Value;
                var from = HandPos(); var carVel = CarVel();
                Vector3 launch; float flight;
                if (!Rider.Solve(from, carVel, tpos + Vector3.up * 0.9f, tvel, speed, out launch, out flight)) Plugin.Verbose("Rider: target moved out of reach during the swing - thrown at the best angle");
                var dir = launch.normalized;
                var go = UnityEngine.Object.Instantiate(prefab, from + dir * 0.6f, Quaternion.LookRotation(dir));
                go.SetActive(true);
                var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.velocity = launch + carVel;
                IgnoreOwn(go);
                go.AddComponent<LanceFuse>();   // explodes on any hit, whatever angle it lands at
                Plugin.Verbose("Rider: " + name + " threw a blast lance, " + flight.ToString("0.00") + " s flight, " + Rider.Elevation(launch).ToString("0") + "° up");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: throw: " + e.Message); }
        }

        private void IgnoreOwn(GameObject go)
        {
            var mine = go.GetComponentsInChildren<Collider>(true);
            foreach (var c in _carCols) foreach (var p in mine) if (c != null && p != null) Physics.IgnoreCollision(p, c, true);
        }

        // ------------------------------------------------------------ "Witness me!"

        private void StartWitness()
        {
            _mode = Mode.Witness; _modeStart = Time.time;
            if (Lance != null) Lance.gameObject.SetActive(true);
            _windUp = Rider.Scream(transform) + AfterScream;   // the whole scream first, then the leap
            Plugin.Verbose("Rider: " + name + " - WITNESS ME! (" + (Wounded() ? "wounded" : "chance roll") + ")");
        }

        private Vector3 LeapFrom() { return transform.position + Vector3.up * 0.9f; }
        private static Vector3 LeapAim(GameObject tcar)
        {
            var tc = tcar.GetComponent<Rigidbody>();
            return (tc != null ? tc.worldCenterOfMass : tcar.transform.position) + Vector3.up * 0.4f;
        }

        // the driver helps: Pilot drives alongside the player while this is refreshed
        private void Escort()
        {
            if (_pilot == null && _car != null) _pilot = _car.GetComponent<Pilot>();
            if (_pilot != null) _pilot.EscortUntil = Time.time + 0.6f;
        }

        // a jump that lands: both cars not going opposite ways or apart (> 60 degrees between their headings, both moving), and a flat jump
        // at RiderLeapSpeed meets the player's car on its predicted course
        private bool CanLeap(Vector3 tvel, GameObject tcar, out Vector3 launch, out float flight)
        {
            launch = Vector3.zero; flight = 0f;
            if (tcar == null) return false;
            var cv = CarVel(); cv.y = 0f; var pv = tvel; pv.y = 0f;
            if (cv.magnitude > 3f && pv.magnitude > 3f && Vector3.Angle(cv, pv) > 60f) return false;
            return Rider.FlatLeap(LeapFrom(), CarVel(), LeapAim(tcar), tvel, Plugin.RiderLeapSpeed.Value, out launch, out flight);
        }

        // off the roof in a long flat jump on the intercept course to the player's car, as a live bomb. His FSMs are off for the flight: the
        // game cannot kill and swap him for a corpse mid-air (2.1.3's "the boy died, nothing blew up")
        private void Leap(Vector3 launch, float flight)
        {
            _vel = launch + CarVel();
            Unseat();
            foreach (var f in GetComponentsInChildren<PlayMakerFSM>(true)) if (f != null) { f.Fsm.RestartOnEnable = false; f.enabled = false; }
            if (Lance != null) Lance.gameObject.SetActive(true);
            Play(2);
            _mode = Mode.Flying; _modeStart = Time.time;
            Plugin.Verbose("Rider: " + name + " leaps at the player's car, " + flight.ToString("0.00") + " s, " + Rider.Elevation(launch).ToString("0") + " deg up");
        }

        private void Fly()
        {
            float dt = Time.deltaTime;
            Blend(2, 0.1f);
            var p = transform.position + Vector3.up * 0.9f;
            var step = _vel * dt + 0.5f * Physics.gravity * dt * dt;
            _vel += Physics.gravity * dt;
            float len = step.magnitude;
            if (len > 1e-4f)
            {
                int n = Physics.SphereCastNonAlloc(p, 0.4f, step / len, _hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue; Vector3 at = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    var c = _hits[i].collider;
                    if (c == null || Own(c)) continue;
                    if (_hits[i].distance < best) { best = _hits[i].distance; at = _hits[i].distance > 0f ? _hits[i].point : p; }
                }
                if (best < float.MaxValue) { Detonate(at); return; }
            }
            transform.position += step;
            var flat = new Vector3(_vel.x, 0f, _vel.z);
            if (flat.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
            // touching the player's car even if no collider was swept (fast, thin parts): blows up there
            var pc = PlayerRef.PlayerCar;
            if (pc != null && (LeapAim(pc) - (transform.position + Vector3.up * 0.9f)).sqrMagnitude < 2.2f * 2.2f) { Detonate(transform.position + Vector3.up * 0.9f); return; }
            if (Time.time - _modeStart > 4f) Detonate(transform.position + Vector3.up * 0.9f);
        }

        private bool Own(Collider c)
        {
            if (c.transform.IsChildOf(transform)) return true;
            if (_car != null && c.transform.IsChildOf(_car.transform)) return true;
            return Lance != null && c.transform.IsChildOf(Lance);
        }

        // the blast (Rider.Kaboom: no lance physics), the rider's carcass thrown
        private void Detonate(Vector3 at)
        {
            _mode = Mode.Done;
            Rider.Kaboom(at, name);
            try
            {
                var dead = Prefabs.FindAny(_prefab + "_Dead");
                if (dead != null)
                {
                    var body = UnityEngine.Object.Instantiate(dead, transform.position, transform.rotation);
                    body.SetActive(true);
                    foreach (var r in body.GetComponentsInChildren<Rigidbody>(true)) r.velocity = _vel * 0.3f + Vector3.up * 4f + UnityEngine.Random.insideUnitSphere * 3f;
                    UnityEngine.Object.Destroy(body, 120f);
                }
                Plugin.Verbose("Rider: " + name + " blew up at " + at);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider: detonate: " + e.Message); }
            if (_marker != null && _marker.Rider == gameObject) _marker.Rider = null;
            Destroy(gameObject);
        }

        // ------------------------------------------------------------ dismount (the crew bails, the car is vacated or taken)

        internal void JumpOff(string why)
        {
            if (_mode == Mode.Flying || _mode == Mode.Dismount || _mode == Mode.Done || _car == null) return;
            if (!Alive()) { Die(); return; }
            var cv = CarVel();
            _vel = _car.transform.right * _side * 3.2f + Vector3.up * 4.2f + cv * 0.7f;
            Unseat();
            Play(2);
            _mode = Mode.Dismount; _modeStart = Time.time;
            Plugin.Verbose("Rider: " + name + " jumps off " + _car.name + " (" + why + ")");
        }

        private void Hop()
        {
            float dt = Time.deltaTime;
            Blend(2, 0.1f);
            var step = _vel * dt + 0.5f * Physics.gravity * dt * dt;
            _vel += Physics.gravity * dt;
            var p = transform.position + Vector3.up * 0.5f;
            float len = step.magnitude;
            bool landed = false; Vector3 ground = transform.position + step;
            if (len > 1e-4f && _vel.y < 0f)
            {
                int n = Physics.RaycastNonAlloc(p, Vector3.down, _hits, 0.5f - step.y + 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++) { var c = _hits[i].collider; if (c == null || Own(c) || _hits[i].normal.y < 0.5f) continue; landed = true; ground = _hits[i].point; break; }
            }
            if (!landed) transform.position += step;
            if (landed || Time.time - _modeStart > 2.5f) Land(landed ? ground : transform.position);
        }

        // on the ground: a free mob of the same kind with the same health takes over (AI on), like the game's bail-out
        private void Land(Vector3 at)
        {
            _mode = Mode.Done;
            float health = Health();
            var prefab = Prefabs.FindAny(_prefab);
            if (prefab != null)
            {
                var mob = UnityEngine.Object.Instantiate(prefab, at + Vector3.up * 0.5f, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
                mob.SetActive(true);
                var col = mob.GetComponent<Collider>();
                if (col != null) mob.transform.position += Vector3.up * (at.y + 0.15f - col.bounds.min.y);
                Register.Name(mob, prefab.name); Register.Add(mob, false);
                if (health > 0f) { Patrol.SetHealth(mob, health); mob.AddComponent<Patrol.LateHealth>().Value = health; }
                Plugin.Verbose("Rider: " + mob.name + " landed and fights on foot");
            }
            if (_marker != null && _marker.Rider == gameObject) _marker.Rider = null;
            Destroy(gameObject);
        }

        // leaves the roof anchor: free kinematic body, no more collisions to ignore, the lance prop stays in the hand
        private void Unseat()
        {
            if (_marker != null && _marker.Rider == gameObject) _marker.Rider = null;   // the car no longer carries it (Explode won't bail it again)
            transform.SetParent(null, true);
            var up = Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.ProjectOnPlane(transform.forward, Vector3.up) : Vector3.forward, Vector3.up);
            transform.rotation = up;
        }

        private void Die()
        {
            _mode = Mode.Done;
            if (_graphOk) { _graph.Destroy(); _graphOk = false; }   // the ragdoll / death takes over
            if (Lance != null) Destroy(Lance.gameObject);
            if (_marker != null && _marker.Rider == gameObject) _marker.Rider = null;
        }

        private void OnDestroy()
        {
            if (_mode == Mode.Flying && gameObject.scene.isLoaded && Time.timeScale > 0f) { _mode = Mode.Done; Rider.Kaboom(transform.position + Vector3.up * 0.9f, name); }   // removed mid-air by something else: still goes off
            if (_graphOk) { try { _graph.Destroy(); } catch (Exception) { } _graphOk = false; }
            if (Lance != null) Destroy(Lance.gameObject);
        }
    }

    // sets a freshly spawned blast-lance projectile off once its FSMs have started (an event sent before Start is lost)
    internal sealed class Detonator : MonoBehaviour
    {
        private int _frames;
        private void Update()
        {
            if (++_frames < 2) return;
            foreach (var f in GetComponents<PlayMakerFSM>()) if (f.FsmName == "Explosion" && f.Fsm.Initialized && f.ActiveStateName != "explode" && f.ActiveStateName != "delete") f.SendEvent("DamageFlammable");
            if (_frames > 10) Destroy(this);
        }
    }

    // A rider's thrown blast lance: the game's lance only goes off when its head's trigger touches something, so one that lands flat or at an
    // angle used to lie there (and blow up later). Any solid hit - or 8 s - now sets it off, unless its own FSM is already exploding.
    internal sealed class LanceFuse : MonoBehaviour
    {
        private float _born; private bool _done;
        private PlayMakerFSM _fsm;
        private void Start() { _born = Time.time; _fsm = GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Explosion"); }
        private bool Exploding() { return _fsm != null && _fsm.Fsm != null && _fsm.Fsm.Initialized && (_fsm.ActiveStateName == "explode" || _fsm.ActiveStateName == "delete"); }
        private void OnCollisionEnter(Collision c) { if (!_done && Time.time - _born > 0.05f) Fire(c.contactCount > 0 ? c.GetContact(0).point : transform.position); }
        private void Update() { if (!_done && Time.time - _born > 8f) Fire(transform.position); }
        private void Fire(Vector3 at)
        {
            if (Exploding()) { _done = true; return; }
            _done = true;
            Rider.Boom(at);
            Destroy(gameObject);
        }
    }

    // a minimal RIFF/WAVE reader (8/16-bit PCM, any channel count) -> AudioClip
    internal static class Wav
    {
        internal static AudioClip Load(string path, string name, float gain = 1f)
        {
            if (!System.IO.File.Exists(path)) return null;
            var b = System.IO.File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A') return null;
            int pos = 12, channels = 0, rate = 0, bits = 0, fmt = 0, dataAt = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4); int len = BitConverter.ToInt32(b, pos + 4); int body = pos + 8;
                if (id == "fmt ") { fmt = BitConverter.ToInt16(b, body); channels = BitConverter.ToInt16(b, body + 2); rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToInt16(b, body + 14); }
                else if (id == "data") { dataAt = body; dataLen = Math.Min(len, b.Length - body); break; }
                pos = body + len + (len & 1);
            }
            if (fmt != 1 || channels < 1 || rate <= 0 || dataAt < 0 || (bits != 16 && bits != 8)) return null;
            int count = dataLen / (bits / 8);
            var data = new float[count];
            if (bits == 16) for (int i = 0; i < count; i++) data[i] = BitConverter.ToInt16(b, dataAt + i * 2) / 32768f;
            else for (int i = 0; i < count; i++) data[i] = (b[dataAt + i] - 128) / 128f;
            if (gain != 1f) for (int i = 0; i < count; i++) data[i] = Mathf.Clamp(data[i] * gain, -1f, 1f);
            var clip = AudioClip.Create(name, count / channels, channels, rate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return clip;
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

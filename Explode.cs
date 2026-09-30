using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // Self-destructing cars ([Self-destruct] SelfDestructingCars): once a raider car is fully vacated - the crew is dead, bailed out,
    // or the last passenger got out beside a dead driver - it "explodes": the frame goes almost black, the exploder zombie's harmless blast (fireball + bang)
    // goes off at the car, and every part pops off its hinge with condition 0 (CarPartsLootFromExplodedCars % of them keep theirs).
    // What is left is a dead chassis: nothing to enter, attach, adjust or fuel, and the Cleanup removes it beyond 1000 m.
    // Trucks (Rust* bodies) are a lootable wreck instead: no charring, the wheels stay on (same condition roll) and it sits on them
    // with the handbrake on, the rear doors still open and close, and the cargo stays locked in the bed where it spawned.
    //
    // Popping a part is what the wrench does: re-tag it vehPartRemoved on layer 9 (the part's own de_Attach state); the part's
    // CheckTag FSM then unparents it and adds a Rigidbody on its next frame, and we give that body a shove the frame after.
    internal static class Explode
    {
        internal const float RemoveDistance = 1000f;          // a dead chassis is removed beyond this (the game only deletes cars at 5 km)
        private const float Delay = 1f;                        // after the vacate, so the last carcass has left the seat
        private const string BlastPrefab = "Explosion_BlastZombie";   // what the exploder zombie's Health FSM spawns when it dies
        private static readonly Color Charred = new Color(0.06f, 0.06f, 0.06f, 1f);
        private static GameObject _blast; private static bool _blastLooked;
        // the blast prefab's harmful half: ExplosionRadius grows its trigger sphere, ExplosionDamage sends distance-scaled Damage to every
        // Bodypart its Range/LOS sensors detect. Switched off right after Instantiate, before they Start.
        private static readonly string[] HarmfulFsms = { "ExplosionRadius", "ExplosionDamage" };

        // Called by the Crew when the car is vacated. Re-checks after a short delay: a car the player took or sits in is never blown up.
        internal static void Schedule(Crew crew, GameObject car, PatrolMarker marker, string why)
        {
            if (!Plugin.SelfDestruct.Value || car == null || marker == null || marker.Exploded) return;
            crew.StartCoroutine(Routine(car, marker, why));
        }

        private static IEnumerator Routine(GameObject car, PatrolMarker marker, string why)
        {
            yield return new WaitForSeconds(Delay);
            if (car == null || marker == null || marker.Exploded || marker.PlayerEntered) yield break;
            if (PlayerRef.PlayerCar == car) yield break;
            marker.Exploded = true;
            Plugin.Verbose("Explode: " + car.name + " (" + why + ")");

            bool truck = IsTruck(marker.BodyPrefab);
            var crew = car.GetComponent<Crew>();
            if (crew != null) crew.OnExploded();
            Nwh.SetInput(car, 0f, 0f, truck ? 1f : 0f);
            if (truck) { Nwh.StopEngine(car); Patrol.Handbrake(car, true); Nwh.SetHandbrake(car, 1f); }

            if (!truck) Darken(car);
            Blast(car);

            var parts = new List<Transform>();
            int kept = 0, wheels = 0;
            foreach (var p in Parts(car))
            {
                bool keep = KeepsCondition();
                if (!keep) SetCondition(p, 0f);
                else kept++;
                if (truck && OnWheelHinge(car, p)) { wheels++; continue; }   // a truck keeps its wheels (rolled like the rest)
                parts.Add(p);
                // the wrench's de_Attach: layer Item + tag vehPartRemoved -> the part's CheckTag FSM unparents it and adds a Rigidbody
                p.gameObject.layer = 9;
                try { p.tag = "vehPartRemoved"; } catch (Exception e) { Plugin.Log.LogWarning("Explode: tag: " + e.Message); }
            }
            var cargo = truck ? new List<Transform>() : Cargo(car);   // a truck's cargo stays locked in the bed
            foreach (var c in cargo)
            {
                // a locked item: the vanilla pickup path (LockPhysics_OFF) gives it its Rigidbody back
                foreach (var f in c.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "LockPhysics") { f.enabled = true; f.SendEvent("LockPhysics_OFF"); }
                c.SetParent(null, true);
            }
            Plugin.Verbose("Explode: " + parts.Count + " part(s) popped" + (truck ? ", " + wheels + " wheel(s) stay on" : "") + " (" + kept + " keep their condition), "
                + (truck ? "cargo stays in the bed" : cargo.Count + " cargo item(s) spilled"));

            yield return null;
            yield return null;   // CheckTag / LockPhysics have run: the Rigidbodies exist
            if (car == null) yield break;
            var centre = car.transform.position;
            foreach (var t in parts) Shove(t, centre, 3f, 6f);
            foreach (var t in cargo) Shove(t, centre, 2f, 4f);
            // a part the CheckTag FSM did not free (none expected): free it ourselves
            foreach (var t in parts)
                if (t != null && t.IsChildOf(car.transform))
                {
                    t.SetParent(null, true);
                    if (t.GetComponent<Rigidbody>() == null) t.gameObject.AddComponent<Rigidbody>();
                    Shove(t, centre, 3f, 6f);
                }

            Deaden(car, truck);
        }

        // ------------------------------------------------------------ pieces

        // the cargo trucks (Rustcargo...): same test as the convoy / template code
        internal static bool IsTruck(string body) { return (body ?? "").StartsWith("Rust", StringComparison.OrdinalIgnoreCase); }

        private static bool OnWheelHinge(GameObject car, Transform t)
        {
            for (var a = t.parent; a != null && a != car.transform; a = a.parent)
                if (a.name.StartsWith("hinge_wheel", StringComparison.Ordinal)) return true;
            return false;
        }

        // what a dead truck keeps working: the rear doors (parts/door_hinge_*: DoorOpen/DoorClose), the wheel hinges (Suspension,
        // checkWheel, attach - the NWH wheels read them) and the cargo in parts/PhysicsLock (its own LockPhysics/pickup FSMs)
        private static bool KeptOnTruck(GameObject car, Transform t)
        {
            for (var a = t; a != null && a != car.transform; a = a.parent)
                if (a.name.StartsWith("door_hinge", StringComparison.Ordinal) || a.name.StartsWith("hinge_wheel", StringComparison.Ordinal)) return true;
            return false;
        }

        // Every attached part: a vehPart-tagged object under the car (not one nested in another part, not an occupant)
        private static List<Transform> Parts(GameObject car)
        {
            var list = new List<Transform>();
            foreach (var t in car.GetComponentsInChildren<Transform>(true))
            {
                if (t == car.transform || !t.CompareTag("vehPart")) continue;
                bool nested = false;
                for (var a = t.parent; a != null && a != car.transform; a = a.parent) if (a.CompareTag("vehPart")) { nested = true; break; }
                if (!nested) list.Add(t);
            }
            return list;
        }

        // Cargo locked in the bed: children of parts/PhysicsLock that carry a LockPhysics FSM
        private static List<Transform> Cargo(GameObject car)
        {
            var list = new List<Transform>();
            var lockGo = Patrol.FindChild(car.transform, "PhysicsLock");
            if (lockGo == null) return list;
            for (int i = 0; i < lockGo.childCount; i++)
            {
                var c = lockGo.GetChild(i);
                foreach (var f in c.GetComponents<PlayMakerFSM>()) if (f.FsmName == "LockPhysics") { list.Add(c); break; }
            }
            return list;
        }

        // CarPartsLootFromExplodedCars %: a roll 0..100 weighted toward the low numbers ((u + u^2) / 2), kept when it lands at or below
        // the setting - at 12 about a fifth of the parts keep their condition, at 50 about 70 %, at 100 all of them
        private static bool KeepsCondition()
        {
            float pct = Mathf.Clamp(Plugin.ExplodedLootPercent.Value, 0f, 100f);
            if (pct <= 0f) return false;
            if (pct >= 100f) return true;
            float u = UnityEngine.Random.value;
            float roll = 100f * (u + u * u) * 0.5f;
            return roll <= pct;
        }

        internal static void SetCondition(Transform part, float value)
        {
            foreach (var f in part.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName != "Condition" && f.FsmName != "Repair") continue;
                if (!f.Fsm.Initialized) continue;
                var v = f.FsmVariables.GetFsmFloat("Condition");
                if (v != null) v.Value = value;
            }
        }

        private static void Shove(Transform t, Vector3 centre, float min, float max)
        {
            if (t == null) return;
            var rb = t.GetComponent<Rigidbody>();
            if (rb == null || rb.isKinematic) return;
            var dir = t.position - centre; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = UnityEngine.Random.insideUnitSphere;
            dir.y = 0f; dir.Normalize();
            float speed = UnityEngine.Random.Range(min, max);
            rb.WakeUp();
            rb.velocity = dir * speed + Vector3.up * (speed * 0.8f);
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 4f;
        }

        // The frame's renderers (not the parts', not an occupant's) go almost black: instanced materials, _Color and emission
        internal static void Darken(GameObject car)
        {
            int n = 0;
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.GetType().Name == "ParticleSystemRenderer") continue;   // by name: no ParticleSystemModule reference
                bool skip = false;
                for (var a = r.transform; a != null && a != car.transform; a = a.parent)
                    if (a.CompareTag("vehPart") || a.GetComponent<Crew>() != null || a.name.IndexOf("(Driver)", StringComparison.Ordinal) >= 0
                        || a.name.IndexOf("(Passenger)", StringComparison.Ordinal) >= 0 || a.name.IndexOf("_Dead", StringComparison.Ordinal) >= 0
                        || a.name == "PhysicsLock") { skip = true; break; }
                if (skip) continue;
                try
                {
                    foreach (var m in r.materials)
                    {
                        if (m == null) continue;
                        if (m.HasProperty("_Color")) { var c = m.color; m.color = new Color(Charred.r, Charred.g, Charred.b, c.a); }
                        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
                        n++;
                    }
                }
                catch (Exception) { }
            }
            Plugin.Verbose("Explode: " + n + " material(s) charred");
        }

        // The exploder zombie's blast without its damage: the game's own Explosion_BlastZombie prefab (AudioSource explosion_03, 0.7, 100 m,
        // play-on-awake; `Explosion` FSM: ParticleSystemPlay BigExplosion with children (fireball, smoke, debris, embers, light, shockwave) +
        // AudioPlay + a physics Explosion (force 3000, radius 5, which also helps throw the parts) + Wait 2 s + DestroySelf). Its damage FSMs,
        // sensors and trigger collider are disabled before they Start; it is not registered, so the game never saves it.
        private static void Blast(GameObject car)
        {
            if (!_blastLooked)
            {
                _blastLooked = true;
                _blast = Prefabs.FindAny(BlastPrefab);
                if (_blast == null) Plugin.Log.LogWarning("Explode: " + BlastPrefab + " prefab not found; no blast effect");
            }
            if (_blast == null) return;
            var rb = car.GetComponent<Rigidbody>();
            var at = (rb != null ? rb.worldCenterOfMass : car.transform.position) + Vector3.up * 0.3f;
            var fx = UnityEngine.Object.Instantiate(_blast, at, Quaternion.identity);
            int off = 0;
            foreach (var f in fx.GetComponentsInChildren<PlayMakerFSM>(true))
                if (Array.IndexOf(HarmfulFsms, f.FsmName) >= 0) { f.enabled = false; off++; }
            foreach (var b in fx.GetComponentsInChildren<Behaviour>(true))
            {
                string n = b.GetType().Name;
                if (n == "RangeSensor" || n == "LOSSensor" || n.EndsWith("Sensor", StringComparison.Ordinal)) { b.enabled = false; off++; }
            }
            foreach (var c in fx.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            fx.SetActive(true);
            UnityEngine.Object.Destroy(fx, 8f);   // its own FSM destroys it after 2 s; this is only the safety net
            Plugin.Verbose("Explode: blast effect at " + at + " (" + off + " harmful component(s) off)");
        }

        // A dead chassis: every FSM on it off (no F prompt, no attach/adjust/fuel/handbrake, no CarAttack), the vehicle controller off.
        // Also used after a load for a chassis that exploded before the save (its FSMs restarted with the scene).
        internal static void Deaden(GameObject car, bool truck)
        {
            if (car == null) return;
            foreach (var f in car.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f == null) continue;
                var t = f.transform;
                bool occupant = false;
                for (var a = t; a != null && a != car.transform; a = a.parent)
                    if (a.CompareTag("vehPart") || a.name.IndexOf("_Dead", StringComparison.Ordinal) >= 0 || a.name == "PhysicsLock") { occupant = true; break; }
                if (occupant || (truck && KeptOnTruck(car, t))) continue;
                f.Fsm.RestartOnEnable = false;
                f.enabled = false;
            }
            // the vehicle controller and the wheel controllers (NWH suspension raycasts would keep the frame floating on invisible wheels);
            // a truck keeps both - it stands on its real wheels, engine off, handbrake and brakes on
            if (truck) { Nwh.StopEngine(car); Nwh.SetInput(car, 0f, 0f, 1f); Nwh.SetHandbrake(car, 1f); }
            else foreach (var c in car.GetComponentsInChildren<Component>(true))
            {
                if (!(c is Behaviour)) continue;
                string n = c.GetType().Name;
                if (n == "VehicleController" || n == "WheelController") ((Behaviour)c).enabled = false;
            }
            // engine sound scripts (SkrilStudio RealisticEngineSound, gearbox whine, muffler crackle) and every looping sound on the frame -
            // the START FSM's cranking loop kept playing when the car blew up mid-start
            foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                if (b != null && b.GetType().Namespace == "SkrilStudio") b.enabled = false;
            StopLoops(car);
            var dt = Patrol.FindChild(car.transform, "DriveTrigger");
            if (dt != null) foreach (var col in dt.GetComponents<Collider>()) col.enabled = false;
            var start = Patrol.FindChild(car.transform, "START");     // the ignition's click collider (its FSM is already off)
            if (start != null) foreach (var col in start.GetComponents<Collider>()) col.enabled = false;
            var pilot = car.GetComponent<Pilot>();
            if (pilot != null) pilot.Detach();
            var crew = car.GetComponent<Crew>();
            if (crew != null) crew.enabled = false;
            // the wreck keeps itself dead from now on (engine, crew, sounds; a truck is frozen once it has settled on its wheels)
            var w = car.GetComponent<Wreck>();
            if (w == null) w = car.AddComponent<Wreck>();
            w.Truck = truck;
        }

        private static void StopLoops(GameObject car)
        {
            foreach (var a in car.GetComponentsInChildren<AudioSource>(true))
                if (a != null && a.loop && a.isPlaying) a.Stop();
        }

        // true once the car blew up: every start-up / revive path checks it after each wait
        internal static bool IsWreck(GameObject car)
        {
            if (car == null) return true;
            var m = car.GetComponent<PatrolMarker>();
            return m != null && m.Exploded;
        }

        // Sits on an exploded car. Twice a second: no crew, no pilot, engine off, no looping sound. A truck stands on its wheels with the
        // vehicle controller on until it has settled (still for 1.5 s, at most 10 s), then it is frozen: engine stopped, VehicleController and
        // WheelControllers off, Rigidbody kinematic - nothing of the NWH simulation (starter, auto-start, sounds) runs on a wreck any more.
        // Removal beyond Explode.RemoveDistance is the Cleanup's job (PatrolMarker.Exploded).
        internal sealed class Wreck : MonoBehaviour
        {
            internal bool Truck;
            private float _next, _still, _settleUntil;
            private bool _frozen, _started;

            private void Start()
            {
                _settleUntil = Time.time + 10f;
                var crew = GetComponent<Crew>();
                if (crew != null) { crew.StopAllCoroutines(); crew.enabled = false; }   // a revive/start-up still waiting
                _started = true;
            }

            private void Update()
            {
                if (!_started || Time.time < _next) return;
                _next = Time.time + 0.5f;
                try
                {
                    var car = gameObject;
                    var pilot = GetComponent<Pilot>();
                    if (pilot != null) pilot.Detach();
                    var crew = GetComponent<Crew>();
                    if (crew != null && crew.enabled) crew.enabled = false;
                    if (!_frozen && Nwh.EngineRunning(car)) { Nwh.StopEngine(car); Plugin.Verbose("Explode: " + car.name + " engine stopped (wreck)"); }
                    StopLoops(car);
                    if (_frozen) return;
                    if (!Truck) { _frozen = true; return; }   // a car's controller is already off
                    Nwh.SetInput(car, 0f, 0f, 1f);
                    Nwh.SetHandbrake(car, 1f);
                    var rb = GetComponent<Rigidbody>();
                    float v = rb != null && !rb.isKinematic ? rb.velocity.magnitude : 0f;
                    _still = v < 0.3f ? _still + 0.5f : 0f;
                    if (_still >= 1.5f || Time.time >= _settleUntil) Freeze(car, rb);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Explode: wreck: " + e.Message); _frozen = true; }
            }

            private void Freeze(GameObject car, Rigidbody rb)
            {
                _frozen = true;
                Nwh.StopEngine(car);
                foreach (var c in car.GetComponentsInChildren<Component>(true))
                {
                    if (!(c is Behaviour)) continue;
                    string n = c.GetType().Name;
                    if (n == "VehicleController" || n == "WheelController") ((Behaviour)c).enabled = false;
                }
                if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }
                StopLoops(car);
                Plugin.Verbose("Explode: " + car.name + " wreck settled and frozen");
            }
        }

        // after a load: a chassis that exploded before the save is dead again
        internal static void RestoreDead(GameObject car, string body)
        {
            bool truck = IsTruck(body);
            if (!truck) Darken(car);
            Deaden(car, truck);
        }
    }
}

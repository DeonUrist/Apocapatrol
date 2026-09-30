using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // Self-destructing cars ([Self-destruct] SelfDestructingCars): once a raider car is fully vacated - the crew is dead, bailed out,
    // or the last passenger got out beside a dead driver - it "explodes": the frame goes almost black, the exploder zombie's blast
    // sound plays at the car, and every part pops off its hinge with condition 0 (CarPartsLootFromExplodedCars % of them keep theirs).
    // What is left is a dead chassis: nothing to enter, attach, adjust or fuel, and the Cleanup removes it beyond 1000 m.
    //
    // Popping a part is what the wrench does: re-tag it vehPartRemoved on layer 9 (the part's own de_Attach state); the part's
    // CheckTag FSM then unparents it and adds a Rigidbody on its next frame, and we give that body a shove the frame after.
    internal static class Explode
    {
        internal const float RemoveDistance = 1000f;          // a dead chassis is removed beyond this (the game only deletes cars at 5 km)
        private const float Delay = 1f;                        // after the vacate, so the last carcass has left the seat
        private const string SfxPrefab = "Explosion_BlastZombie";
        private static readonly Color Charred = new Color(0.06f, 0.06f, 0.06f, 1f);
        private static AudioClip _sfx; private static bool _sfxLooked;

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

            var crew = car.GetComponent<Crew>();
            if (crew != null) crew.OnExploded();
            Nwh.SetInput(car, 0f, 0f, 0f);

            Darken(car);
            Sfx(car);

            var parts = Parts(car);
            int kept = 0;
            foreach (var p in parts)
            {
                bool keep = KeepsCondition();
                if (!keep) SetCondition(p, 0f);
                else kept++;
                // the wrench's de_Attach: layer Item + tag vehPartRemoved -> the part's CheckTag FSM unparents it and adds a Rigidbody
                p.gameObject.layer = 9;
                try { p.tag = "vehPartRemoved"; } catch (Exception e) { Plugin.Log.LogWarning("Explode: tag: " + e.Message); }
            }
            var cargo = Cargo(car);
            foreach (var c in cargo)
            {
                // a locked item: the vanilla pickup path (LockPhysics_OFF) gives it its Rigidbody back
                foreach (var f in c.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "LockPhysics") { f.enabled = true; f.SendEvent("LockPhysics_OFF"); }
                c.SetParent(null, true);
            }
            Plugin.Verbose("Explode: " + parts.Count + " part(s) popped (" + kept + " keep their condition), " + cargo.Count + " cargo item(s) spilled");

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

            Deaden(car);
        }

        // ------------------------------------------------------------ pieces

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

        // The exploder zombie's blast, sound only (its prefab also carries an ExplosionDamage FSM; we never instantiate it)
        private static void Sfx(GameObject car)
        {
            if (!_sfxLooked)
            {
                _sfxLooked = true;
                var prefab = Prefabs.FindAny(SfxPrefab);
                var src = prefab != null ? prefab.GetComponent<AudioSource>() : null;
                _sfx = src != null ? src.clip : null;
                if (_sfx == null) Plugin.Log.LogWarning("Explode: no blast sound on " + SfxPrefab);
            }
            if (_sfx == null) return;
            var rb = car.GetComponent<Rigidbody>();
            AudioSource.PlayClipAtPoint(_sfx, rb != null ? rb.worldCenterOfMass : car.transform.position + Vector3.up, 1f);
        }

        // A dead chassis: every FSM on it off (no F prompt, no attach/adjust/fuel/handbrake, no CarAttack), the vehicle controller off.
        // Also used after a load for a chassis that exploded before the save (its FSMs restarted with the scene).
        internal static void Deaden(GameObject car)
        {
            if (car == null) return;
            foreach (var f in car.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f == null) continue;
                var t = f.transform;
                bool occupant = false;
                for (var a = t; a != null && a != car.transform; a = a.parent)
                    if (a.CompareTag("vehPart") || a.name.IndexOf("_Dead", StringComparison.Ordinal) >= 0) { occupant = true; break; }
                if (occupant) continue;
                f.Fsm.RestartOnEnable = false;
                f.enabled = false;
            }
            // the vehicle controller and the wheel controllers (NWH suspension raycasts would keep the frame floating on invisible wheels)
            foreach (var c in car.GetComponentsInChildren<Component>(true))
            {
                if (!(c is Behaviour)) continue;
                string n = c.GetType().Name;
                if (n == "VehicleController" || n == "WheelController") ((Behaviour)c).enabled = false;
            }
            var dt = Patrol.FindChild(car.transform, "DriveTrigger");
            if (dt != null) foreach (var col in dt.GetComponents<Collider>()) col.enabled = false;
            var pilot = car.GetComponent<Pilot>();
            if (pilot != null) pilot.Detach();
        }

        // after a load: a chassis that exploded before the save is dead again
        internal static void RestoreDead(GameObject car)
        {
            Darken(car);
            Deaden(car);
        }
    }
}

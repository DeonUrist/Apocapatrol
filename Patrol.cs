using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // Runner: waits for the spawn key in game and assembles one car.
    internal class Patrol : MonoBehaviour
    {
        private PlayMakerFSM _menu, _saveLoad;
        private float _nextRefScan;
        private bool _busy;

        internal static void ResetForScene() { Prefabs.Invalidate(); }

        private void Update()
        {
            PatrolPersistence.Tick(this);
            if (!InGame()) return;

        }

        // builds a template (from the F8 menu); one at a time
        internal void Spawn(CarTemplate t)
        {
            if (_busy) { Plugin.Log.LogInfo("Spawn: a build is still running"); return; }
            if (!InGame()) return;
            StartCoroutine(Build(t));
        }

        internal bool InGame()
        {
            if (Time.unscaledTime >= _nextRefScan && (!Alive(_menu) || !Alive(_saveLoad)))
            {
                _nextRefScan = Time.unscaledTime + 2f;
                var gm = GameObject.Find("__GameManager__");
                if (gm != null) foreach (var f in gm.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Menu") _menu = f;
                var sl = GameObject.Find("SaveLoadGame");
                if (sl != null) foreach (var f in sl.GetComponents<PlayMakerFSM>()) if (f.FsmName == "SaveLoadGame") _saveLoad = f;
            }
            if (Time.timeScale <= 0f) return false;
            if (!Alive(_menu) || !_menu.Fsm.Initialized || _menu.ActiveStateName != "play") return false;
            if (Alive(_saveLoad) && _saveLoad.Fsm.Initialized && _saveLoad.ActiveStateName != "isPlay") return false;
            return true;
        }

        // =============================================================== build

        private IEnumerator Build(CarTemplate tpl)
        {
            _busy = true;
            try
            {
                Vector3 p, fwd;
                if (!PlayerPose(out p, out fwd)) { Plugin.Log.LogWarning("No player found"); yield break; }
                Plugin.Log.LogInfo("Building template " + tpl.Name + ": " + tpl.Describe());

                var body = Prefabs.Find(tpl.Body, "vehicle");
                if (body == null) { Plugin.Log.LogWarning("Body prefab not found: " + tpl.Body); yield break; }

                var pos = p + fwd * Plugin.SpawnDistance + Vector3.up * 1.0f;
                var car = UnityEngine.Object.Instantiate(body, pos, Quaternion.LookRotation(fwd));
                car.SetActive(true);
                Register.Name(car, body.name); Register.Add(car, true);
                Plugin.Log.LogInfo("Car frame " + car.name + " at " + pos);

                yield return null;   // let the frame's FSMs start (hinges, getEngine, START ...)

                int parts = 0;
                parts += AttachAll(car, new[] { "hinge_wheel_FL", "hinge_wheel_FR", "hinge_wheel_RL", "hinge_wheel_RR" }, tpl.Wheel, "wheel");
                parts += AttachAll(car, new[] { "hinge_engine" }, tpl.Engine, "engine");
                parts += AttachAll(car, new[] { "hinge_radiator" }, tpl.Radiator, "radiator");
                parts += AttachAll(car, new[] { "hinge_steeringwheel" }, tpl.SteeringWheel, "steeringwheel");
                parts += AttachAll(car, new[] { "hinge_exhaust" }, tpl.Exhaust, "exhaust");
                parts += AttachAll(car, new[] { "hinge_seat_driver" }, tpl.Seat, "seat");
                parts += AttachAll(car, new[] { "hinge_seat_passenger" }, tpl.PassengerSeat, "seat");
                Plugin.Log.LogInfo(parts + " parts attached");

                if (tpl.FillFuel) Fuel(car);
                if (tpl.ReleaseHandbrake) Handbrake(car, false);
                List<GameObject> cargo = null;
                if (!string.IsNullOrEmpty(tpl.Cargo))
                {
                    string lootKey = tpl.Cargo.Equals("Random", StringComparison.OrdinalIgnoreCase) ? Cargo.RollLootType() : tpl.Cargo;
                    Plugin.Log.LogInfo("Loot: " + lootKey + " (" + Plugin.CargoSpec(lootKey) + ")");
                    cargo = Cargo.Load(car, Plugin.CargoSpec(lootKey));
                }

                yield return null;
                GameObject driver = null, passenger = null;
                if (!string.IsNullOrEmpty(tpl.Driver))
                {
                    if (tpl.Driver.Trim().EndsWith("_Dead", StringComparison.OrdinalIgnoreCase)) driver = SeatDriver(car, tpl.Driver);
                    else driver = SeatLiveDriver(car, tpl.Driver);
                }
                if (!string.IsNullOrEmpty(tpl.Passenger)) passenger = SeatPassenger(car, tpl.Passenger);
                PatrolMarker.Attach(car, body.name, tpl.Driver, driver, tpl.Passenger, passenger, tpl.Rams);

                yield return new WaitForSeconds(1.5f);
                if (cargo != null) Cargo.SettleFsms(cargo);
                SetPartConditions(car);
                if (Plugin.VerboseLog.Value) LogHingeStates(car);

                {
                    var start = FindFsm(car, "START", "Start");
                    if (start == null) Plugin.Log.LogWarning("No START/Start FSM on the frame");
                    else
                    {
                        Plugin.Verbose("START state before: " + start.ActiveStateName);
                        start.Fsm.SetState("Ignition");
                        yield return null;
                        yield return null;
                        start.Fsm.SetState("Start");
                        yield return new WaitForSeconds(3f);
                        bool running = Nwh.EngineRunning(car);
                        Plugin.Log.LogInfo("START state: " + start.ActiveStateName + "  engine running: " + running);
                        if (!running)
                        {
                            Nwh.StartEngine(car);
                            yield return new WaitForSeconds(1f);
                            Plugin.Log.LogInfo("After NWH StartEngine(): running=" + Nwh.EngineRunning(car) + "  START state: " + start.ActiveStateName);
                        }
                    }
                }

                if (driver != null && !tpl.Driver.Trim().EndsWith("_Dead", StringComparison.OrdinalIgnoreCase))
                    Plugin.Log.LogInfo("Driver in place; the Crew component takes it from here");
            }
            finally { _busy = false; }
        }

        private static int AttachAll(GameObject car, string[] hingeNames, string partQuery, string kind)
        {
            if (string.IsNullOrEmpty(partQuery)) return 0;
            var prefab = Prefabs.Find(partQuery, kind);
            if (prefab == null) { Plugin.Log.LogWarning(kind + " prefab not found: " + partQuery); return 0; }
            int n = 0;
            foreach (var hn in hingeNames)
            {
                var hinge = FindChild(car.transform, hn);
                if (hinge == null) { Plugin.Log.LogWarning("Hinge " + hn + " not found on " + car.name); continue; }
                Attach(prefab, hinge);
                n++;
            }
            return n;
        }

        // The game's vehPart_Attach "Attach" state: destroy the item's Rigidbody, tag vehPart, layer 8,
        // parent to the hinge with local position/rotation reset. Done before the item's own FSMs Start,
        // so CheckTag/LockPhysics see an already-attached part (same as a part loaded from a save).
        private static void Attach(GameObject prefab, Transform hinge)
        {
            var part = UnityEngine.Object.Instantiate(prefab, hinge.position, hinge.rotation);
            part.SetActive(true);
            var rb = part.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
            try { part.tag = "vehPart"; } catch (Exception e) { Plugin.Log.LogWarning("tag vehPart: " + e.Message); }
            part.layer = 8;
            part.transform.SetParent(hinge, true);
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = Quaternion.identity;
            Register.Name(part, prefab.name); Register.Add(part, false);
            Plugin.Verbose("  " + part.name + " -> " + hinge.name);
        }

        private static void Fuel(GameObject car)
        {
            var fuel = FindChild(car.transform, "Fuel");
            if (fuel == null) { Plugin.Log.LogWarning("No Fuel child"); return; }
            foreach (var f in fuel.GetComponents<PlayMakerFSM>())
            {
                if (f.FsmName != "LiquidAmount") continue;
                var liquid = f.FsmVariables.GetFsmFloat("Liquid");
                var cap = f.FsmVariables.GetFsmFloat("LiquidCapacity");
                if (liquid == null || cap == null) { Plugin.Log.LogWarning("Fuel/LiquidAmount has no Liquid/LiquidCapacity"); return; }
                float amount = cap.Value > 0f ? cap.Value : 20f;
                liquid.Value = amount;
                Plugin.Log.LogInfo("Fuel: " + amount + " / " + cap.Value);
                return;
            }
            Plugin.Log.LogWarning("Fuel/LiquidAmount FSM not found");
        }

        // The handbrake lever FSM: HandbrakeOn -> (click) over -> Sound -> HandbrakeOff (SetProperty input.Handbrake + lever rotation).
        // Entering "Sound" plays the click and flows into HandbrakeOff by itself; "Sound 2" goes back to HandbrakeOn.
        internal static void Handbrake(GameObject car, bool on)
        {
            var f = FindFsm(car, "handbrake", "Handbrake");
            if (f == null) { Plugin.Log.LogWarning("No handbrake/Handbrake FSM; setting NWH input.Handbrake only"); Nwh.SetHandbrake(car, on ? 1f : 0f); return; }
            string cur = f.Fsm.Initialized ? f.ActiveStateName : "";
            bool isOn = cur == "HandbrakeOn" || cur == "over" || cur == "Sound 2" || cur == "";
            if (on == isOn) { Plugin.Verbose("Handbrake already " + (on ? "on" : "off") + " (" + cur + ")"); return; }
            f.Fsm.SetState(on ? "Sound 2" : "Sound");
            Plugin.Log.LogInfo("Handbrake " + (on ? "on" : "released") + " (was " + cur + ")");
        }

        // A ragdoll on the driver seat: the *_Dead prefab's root Rigidbody is made kinematic and parented to the car's sitPos
        // (where the player sits), so it rides along exactly; the bones hang off it through their CharacterJoints and keep
        // flopping. Collisions between the ragdoll and the car are ignored so it never gets thrown out by the car's colliders.
        internal static GameObject SeatDriver(GameObject car, string prefabName)
        {
            var prefab = Prefabs.FindAny(prefabName);
            if (prefab == null) { Plugin.Log.LogWarning("Driver prefab not found: " + prefabName); return null; }
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null) { Plugin.Log.LogWarning("No sitPos on " + car.name); return null; }

            var off = new Vector3(Plugin.DriverOffsetX.Value, Plugin.DriverOffsetY.Value, Plugin.DriverOffsetZ.Value);
            var pos = sit.position + car.transform.right * off.x + car.transform.up * off.y + car.transform.forward * off.z;
            var rot = Quaternion.LookRotation(car.transform.forward, car.transform.up);
            var drv = UnityEngine.Object.Instantiate(prefab, pos, rot);
            drv.SetActive(true);
            drv.name = prefab.name + "(Driver)";

            var carCols = car.GetComponentsInChildren<Collider>(true);
            var drvCols = drv.GetComponentsInChildren<Collider>(true);
            foreach (var a in drvCols) foreach (var b in carCols)
                if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

            var root = drv.GetComponent<Rigidbody>();
            if (root == null) root = drv.AddComponent<Rigidbody>();
            root.isKinematic = true;
            root.interpolation = RigidbodyInterpolation.None;
            drv.transform.SetParent(sit, true);

            int bones = 0;
            foreach (var rb in drv.GetComponentsInChildren<Rigidbody>(true))
                if (rb != root) { bones++; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            Plugin.Log.LogInfo("Driver " + drv.name + " on " + sit.name + " at " + pos + " (" + bones + " ragdoll bones, " + drvCols.Length + " colliders)");
            return drv;
        }

        // A live enemy at the wheel with its AI off: instantiated at sitPos, the mover/AI FSMs disabled before they Start,
        // root Rigidbody kinematic and parented to the seat, collisions with the car ignored. Health/Damage/Bodypart stay
        // vanilla so it can be shot; the Crew component drives the car and reacts to its death.
        internal static GameObject SeatLiveDriver(GameObject car, string prefabName, float health = -1f, CrewPhase phase = CrewPhase.Waiting, float seated = 0f)
        {
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null) { Plugin.Log.LogWarning("No sitPos on " + car.name); return null; }
            var drv = SeatOccupant(car, prefabName, sit, "Driver", false, health);
            if (drv == null) return null;
            if (Plugin.RangedCombat.Value && PassengerGuard.IsRangedHuman(drv)) PassengerGuard.AttachDriver(drv, car, sit);
            var crew = phase == CrewPhase.Waiting && seated <= 0f ? Crew.Attach(car, drv) : Crew.Restore(car, drv, phase, seated);
            crew.MuteAi();
            return drv;
        }

        // The passenger sits where the driver would if the driver seat hinge were the passenger seat hinge: sitPos shifted
        // by the offset between the two seat hinges (the game has no passenger sit point of its own).
        internal static GameObject SeatPassenger(GameObject car, string prefabName, float health = -1f)
        {
            var sit = FindChild(car.transform, "sitPos");
            var hd = FindChild(car.transform, "hinge_seat_driver");
            var hp = FindChild(car.transform, "hinge_seat_passenger");
            if (sit == null || hd == null || hp == null) { Plugin.Log.LogWarning("No sitPos / seat hinges for a passenger on " + car.name); return null; }
            var anchor = new GameObject("Apocapatrol.PassengerPos").transform;
            anchor.SetParent(car.transform, false);
            anchor.position = sit.position + (hp.position - hd.position);
            anchor.rotation = sit.rotation;
            var pax = SeatOccupant(car, prefabName, anchor, "Passenger", true, health);
            if (pax == null) { UnityEngine.Object.Destroy(anchor.gameObject); return null; }
            PassengerGuard.Attach(pax, car, anchor);
            return pax;
        }

        // A live enemy in a seat with its AI off: instantiated at the anchor, the mover/AI FSMs disabled by the Crew before
        // they Start, root Rigidbody kinematic and parented to the anchor, collisions with the car ignored, seated pose.
        // Health/Damage/Bodypart stay vanilla so it can be shot.
        private static GameObject SeatOccupant(GameObject car, string prefabName, Transform anchor, string role, bool passenger, float health = -1f)
        {
            var prefab = Prefabs.FindAny(prefabName);
            if (prefab == null) { Plugin.Log.LogWarning(role + " prefab not found: " + prefabName); return null; }

            var off = new Vector3(Plugin.DriverOffsetX.Value, Plugin.DriverOffsetY.Value, Plugin.DriverOffsetZ.Value);
            var pos = anchor.position + car.transform.right * off.x + car.transform.up * off.y + car.transform.forward * off.z;
            var rot = Quaternion.LookRotation(car.transform.forward, car.transform.up);
            var go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            go.SetActive(true);
            if (passenger) PassengerGuard.Prepare(go, Plugin.RangedCombat.Value);
            else if (Plugin.RangedCombat.Value) PassengerGuard.Prepare(go, true);   // ranged-only rewiring + mute, before the FSMs' Start
            else Crew.MuteAi(go);
            go.name = prefab.name + "(" + role + ")";

            foreach (var a in go.GetComponentsInChildren<Collider>(true))
                foreach (var b in car.GetComponentsInChildren<Collider>(true))
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

            var root = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            root.isKinematic = true;
            root.interpolation = RigidbodyInterpolation.None;
            go.transform.SetParent(anchor, true);
            if (Plugin.PoseEnabled.Value) Pose.Apply(go, anchor, prefab.name);
            if (health >= 0f) SetHealth(go, health);
            Plugin.Log.LogInfo(role + " " + go.name + " on " + anchor.name + " at " + pos);
            return go;
        }

        // Every collider of an occupant vs every collider of the car. Unity drops an ignore pair when either collider is
        // disabled or its object deactivated (weapon props toggled by WeaponType/Attack, the FireDamage hitbox...), and an
        // occupant's root is kinematic, so a single live pair shoves the car around unopposed. Cheap; re-applied periodically.
        private static readonly Dictionary<GameObject, long> _ignoreSignature = new Dictionary<GameObject, long>();

        // Cheap signature of the occupant's + car's enabled colliders; the pairs are re-applied only when it changes
        // (a toggled prop, a part fallen off), not every second - each IgnoreCollision call makes PhysX rebuild pair filters.
        internal static int IgnoreCollisionsIfChanged(GameObject who, GameObject car)
        {
            if (who == null || car == null) return 0;
            long sig = 17;
            foreach (var a in who.GetComponentsInChildren<Collider>(true))
                if (a != null && a.enabled && a.gameObject.activeInHierarchy) sig = sig * 31 + a.GetInstanceID();
            foreach (var b in car.GetComponentsInChildren<Collider>(true))
                if (b != null && b.enabled && b.gameObject.activeInHierarchy) sig = sig * 31 + b.GetInstanceID();
            long old;
            if (_ignoreSignature.TryGetValue(who, out old) && old == sig) return 0;
            _ignoreSignature[who] = sig;
            int n = IgnoreCollisions(who, car);
            Plugin.Verbose("Collision ignores re-applied for " + who.name + ": " + n + " pairs");
            return n;
        }

        internal static int IgnoreCollisions(GameObject who, GameObject car)
        {
            if (who == null || car == null) return 0;
            int n = 0;
            var carCols = car.GetComponentsInChildren<Collider>(true);
            foreach (var a in who.GetComponentsInChildren<Collider>(true))
            {
                if (a == null || !a.enabled || !a.gameObject.activeInHierarchy) continue;
                foreach (var b in carCols)
                    if (b != null && b.enabled && b.gameObject.activeInHierarchy && !b.transform.IsChildOf(who.transform)) { Physics.IgnoreCollision(a, b, true); n++; }
            }
            return n;
        }

        // Before the passenger climbs over, the dead driver's carcass (the <X>_Dead ragdoll its Health FSM dropped on the seat)
        // is thrown out of the car by physics: its colliders are told to ignore the car and the passenger first (so it cannot
        // shove the car), joints to the car are cut, and every bone gets a sideways+up velocity. The returned CorpseEject is
        // Done once the carcass is EjectDistance from the seat; if it has not got there after 3 s it is put down there once.
        // Returns null when there is nothing to eject.
        internal static CorpseEject EjectCorpses(GameObject car, GameObject pax)
        {
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null) return null;
            var roots = new List<Transform>();
            foreach (var c in Physics.OverlapSphere(sit.position, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                if (c == null) continue;
                var root = c.transform.root;
                if (root == car.transform || (pax != null && root == pax.transform.root)) continue;
                if (root.name.IndexOf("_Dead", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!roots.Contains(root)) roots.Add(root);
            }
            if (roots.Count == 0) { Plugin.Verbose("Eject: no carcass near the driver seat"); return null; }
            var carCols = car.GetComponentsInChildren<Collider>(true);
            var paxCols = pax != null ? pax.GetComponentsInChildren<Collider>(true) : new Collider[0];
            var left = -car.transform.right;
            foreach (var root in roots)
            {
                int joints = 0, jointsToCar = 0;
                foreach (var j in root.GetComponentsInChildren<Joint>(true))
                {
                    joints++;
                    if (j.connectedBody != null && j.connectedBody.transform.IsChildOf(car.transform)) { jointsToCar++; j.connectedBody = null; }
                }
                foreach (var a in root.GetComponentsInChildren<Collider>(true))
                {
                    if (a == null) continue;
                    foreach (var b in carCols) if (b != null) Physics.IgnoreCollision(a, b, true);
                    foreach (var b in paxCols) if (b != null) Physics.IgnoreCollision(a, b, true);
                }
                var velocity = left * Plugin.EjectSpeed + Vector3.up * (Plugin.EjectSpeed * 0.5f) - car.transform.forward * 0.5f;
                Launch(root, velocity);
                Plugin.Log.LogInfo("Eject: " + root.name + " thrown out of " + car.name + " at " + velocity.magnitude.ToString("0.0") + " m/s ("
                    + joints + " joints, " + jointsToCar + " were attached to the car)");
            }
            var eject = car.AddComponent<CorpseEject>();
            eject.Init(sit, roots, left);
            return eject;
        }

        // Gives every bone of a ragdoll the same velocity (a shove), waking it up.
        internal static void Launch(Transform root, Vector3 velocity)
        {
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb.isKinematic) continue;
                rb.WakeUp();
                rb.velocity = velocity;
                rb.angularVelocity = Vector3.zero;
            }
        }

        // Moves a ragdoll as a whole (every rigidbody by the same offset) and gives all of it one velocity (zero = put down asleep).
        internal static void Throw(Transform root, Vector3 to, Vector3 velocity)
        {
            var rbs = root.GetComponentsInChildren<Rigidbody>(true);
            Vector3 from = root.position;
            if (rbs.Length > 0)
            {
                from = Vector3.zero;
                foreach (var rb in rbs) from += rb.position;
                from /= rbs.Length;
            }
            var offset = to - from;
            root.position += offset;
            foreach (var rb in rbs)
            {
                if (rb.transform == root) continue;                  // already moved with the transform
                rb.position = rb.transform.position;                 // sync the physics position to the moved transform
            }
            foreach (var rb in rbs)
            {
                if (rb.isKinematic) continue;
                rb.velocity = velocity; rb.angularVelocity = Vector3.zero;
                if (velocity == Vector3.zero) rb.Sleep();
            }
        }

        // The surviving passenger climbs over to the driver seat: same placement as SeatOccupant, AI fully muted, pose re-anchored.
        internal static GameObject MoveToDriverSeat(GameObject car, GameObject pax)
        {
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null || pax == null) return null;
            var guard = pax.GetComponent<PassengerGuard>();
            var oldAnchor = pax.transform.parent;
            if (guard != null && Plugin.RangedCombat.Value) Crew.MuteAi(pax, PassengerGuard.CombatFsms);
            else { if (guard != null) UnityEngine.Object.Destroy(guard); guard = null; Crew.MuteAi(pax); }
            var off = new Vector3(Plugin.DriverOffsetX.Value, Plugin.DriverOffsetY.Value, Plugin.DriverOffsetZ.Value);
            pax.transform.SetParent(sit, true);
            pax.transform.position = sit.position + car.transform.right * off.x + car.transform.up * off.y + car.transform.forward * off.z;
            pax.transform.rotation = Quaternion.LookRotation(car.transform.forward, car.transform.up);
            var pose = pax.GetComponent<Pose>();
            if (pose != null) pose.Reseat(sit);
            pax.name = pax.name.Replace("(Passenger)", "(Driver)");
            if (guard != null) guard.SetDriverMode(sit);
            int pairs = IgnoreCollisions(pax, car);
            Plugin.Log.LogInfo("Takeover: " + pax.name + " on " + sit.name + ", " + pairs + " collider pairs vs the car ignored");
            if (oldAnchor != null && oldAnchor.name == "Apocapatrol.PassengerPos") UnityEngine.Object.Destroy(oldAnchor.gameObject);
            return pax;
        }

        // The passenger gets out: a fresh instance of its prefab (fully vanilla AI, registered like a spawned enemy so it is
        // saved by the game) appears beside the car with the passenger's remaining health; the seated copy is destroyed.
        // A fresh instance rather than the seated one because the seat setup rewires its FSM actions (ranged-only, melee off).
        internal static void BailOut(GameObject car, GameObject pax, PatrolMarker marker)
        {
            marker.PassengerLeft();
            BailOut(car, pax, marker.PassengerPrefab, 0f);
        }

        // Any occupant gets out on the given side (+1 right, -1 left, 0 = the side its seat is on relative to sitPos).
        internal static GameObject BailOut(GameObject car, GameObject pax, string prefabName, float side)
        {
            float health = GetHealth(pax);
            var prefab = Prefabs.FindAny(prefabName);
            var anchor = pax.transform.parent;
            var sit = FindChild(car.transform, "sitPos");
            if (side == 0f)
            {
                side = 1f;
                if (anchor != null && sit != null) side = Vector3.Dot(anchor.position - sit.position, car.transform.right) >= 0f ? 1f : -1f;
            }
            var from = (anchor != null ? anchor.position : car.transform.position) + car.transform.right * side * Plugin.BailDistance + Vector3.up * 1.5f;
            var pos = from + Vector3.down * 1.2f;
            var hits = Physics.RaycastAll(from, Vector3.down, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
                if (h.collider != null && !h.collider.transform.IsChildOf(car.transform) && !h.collider.transform.IsChildOf(pax.transform) && h.distance < best)
                { best = h.distance; pos = h.point; }
            float groundY = pos.y;
            var rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(car.transform.right * side, Vector3.up).normalized, Vector3.up);

            if (prefab == null)
            {
                Plugin.Log.LogWarning("Bail-out: prefab not found: " + prefabName + "; " + pax.name + " just disappears");
                UnityEngine.Object.Destroy(pax);
                if (anchor != null && anchor.name == "Apocapatrol.PassengerPos") UnityEngine.Object.Destroy(anchor.gameObject);
                return null;
            }
            var mob = UnityEngine.Object.Instantiate(prefab, pos + Vector3.up * 0.5f, rot);
            mob.SetActive(true);
            // stand it on the ground: the prefab pivot may sit mid-body, so lift it by its collider's lowest point
            var rootCol = mob.GetComponent<Collider>();
            if (rootCol != null)
            {
                float bottom = rootCol.bounds.min.y;
                mob.transform.position += Vector3.up * (groundY + 0.15f - bottom);
            }
            pos = mob.transform.position;
            Register.Name(mob, prefab.name); Register.Add(mob, false);
            if (health > 0f) { SetHealth(mob, health); mob.AddComponent<LateHealth>().Value = health; }
            UnityEngine.Object.Destroy(pax);
            if (anchor != null && anchor.name == "Apocapatrol.PassengerPos") UnityEngine.Object.Destroy(anchor.gameObject);
            Plugin.Log.LogInfo("Bail-out: " + mob.name + " got out of " + car.name + " with " + (health > 0f ? health.ToString("0") : "full") + " health at " + pos);
            return mob;
        }

        internal static float GetHealth(GameObject who)
        {
            if (who == null) return -1f;
            var f = who.GetComponents<PlayMakerFSM>().FirstOrDefault(x => x.FsmName == "Health");
            var h = f != null ? f.FsmVariables.GetFsmFloat("Health") : null;
            return h != null ? h.Value : -1f;
        }

        internal static void SetHealth(GameObject who, float value)
        {
            var f = who.GetComponents<PlayMakerFSM>().FirstOrDefault(x => x.FsmName == "Health");
            var h = f != null ? f.FsmVariables.GetFsmFloat("Health") : null;
            if (h != null) h.Value = value;
        }

        // Parts with a Condition FSM (engine, radiator, wheels) get a rolled condition; the FSM's tiers react to the variable.
        private static void SetPartConditions(GameObject car)
        {
            int n = 0;
            foreach (var f in car.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f.FsmName != "Condition" || !f.Fsm.Initialized) continue;
                var v = f.FsmVariables.GetFsmFloat("Condition");
                if (v == null) continue;
                float c = Plugin.RollPartHealth();
                v.Value = c;
                var repair = f.gameObject.GetComponents<PlayMakerFSM>();
                foreach (var r in repair)
                    if (r.FsmName == "Repair" && r.Fsm.Initialized) { var rv = r.FsmVariables.GetFsmFloat("Condition"); if (rv != null) rv.Value = c; }
                Plugin.Verbose("  " + f.gameObject.name + " condition " + c.ToString("0"));
                n++;
            }
            Plugin.Log.LogInfo("Part conditions set on " + n + " part(s) (" + Plugin.MinPartHealth.Value + ".." + Plugin.MaxPartHealth.Value + " %)");
        }

        private static void LogHingeStates(GameObject car)
        {
            foreach (var f in car.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                string n = f.gameObject.name;
                if (!(n.StartsWith("hinge_") || n == "START" || n == "Fuel")) continue;
                if (f.FsmName != "vehPart_Attach" && f.FsmName != "checkWheel" && f.FsmName != "getEngine" && f.FsmName != "getRadiator"
                    && f.FsmName != "Start" && f.FsmName != "LiquidAmount") continue;
                Plugin.Log.LogInfo("  " + n + " [" + f.FsmName + "] " + (f.Fsm.Initialized ? f.ActiveStateName : "(not init)") + " children=" + f.transform.childCount);
            }
        }

    // Follows a thrown carcass: Done once it is EjectDistance from the driver seat (the passenger only climbs over then);
    // if it has not got there after 3 s (caught on something) it is put down at that distance once. Removes itself when done.
    internal class CorpseEject : MonoBehaviour
    {
        private Transform _sit;
        private List<Transform> _roots;
        private Vector3 _left;
        private float _t, _nextCheck;
        internal bool Done { get; private set; }

        internal void Init(Transform sit, List<Transform> roots, Vector3 left) { _sit = sit; _roots = roots; _left = left; }

        private float Distance(Transform root)
        {
            var rb = root.GetComponentInChildren<Rigidbody>();
            var p = rb != null ? rb.position : root.position;
            var d = p - _sit.position; d.y = 0f;
            return d.magnitude;
        }

        private void FixedUpdate()
        {
            if (Done) { Destroy(this); return; }
            if (_sit == null || _roots == null) { Done = true; return; }
            _t += Time.fixedDeltaTime;
            if (_t < _nextCheck) return;
            _nextCheck = _t + 0.1f;
            bool clear = true;
            foreach (var root in _roots)
                if (root != null && Distance(root) < Plugin.EjectDistance) clear = false;
            if (clear) { Done = true; Plugin.Verbose("Eject: carcass clear of the seat after " + _t.ToString("0.0") + " s"); return; }
            if (_t < 3f) return;
            foreach (var root in _roots)
            {
                if (root == null || Distance(root) >= Plugin.EjectDistance) continue;
                var target = _sit.position + _left * Plugin.EjectDistance + Vector3.up * 2f;
                var ground = target + Vector3.down * 1.5f;
                foreach (var h in Physics.RaycastAll(target, Vector3.down, 8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    if (h.collider != null && !h.collider.transform.IsChildOf(transform) && !h.collider.transform.IsChildOf(root) && h.point.y > ground.y) ground = h.point;
                Patrol.Throw(root, ground + Vector3.up * 0.4f, Vector3.zero);
                Plugin.Log.LogInfo("Eject: " + root.name + " did not get clear in 3 s, put down " + Plugin.EjectDistance + " m left of the seat");
            }
            Done = true;
        }
    }

    // Re-applies a health value for a few frames after Instantiate, in case the Health FSM's start state resets it.
    internal class LateHealth : MonoBehaviour
    {
        internal float Value;
        private int _frames;
        private void Update()
        {
            Patrol.SetHealth(gameObject, Value);
            if (++_frames >= 3) Destroy(this);
        }
    }

    // =============================================================== helpers

        // F9 survey: hinge_exhaust transforms of every car in the scene and of every frame prefab, to find out why exhausts
        // on freshly instantiated frames sit 90 degrees off compared with the game's own cars.
        internal static void SurveyExhausts()
        {
            Plugin.Log.LogInfo("=== Exhaust survey: scene cars ===");
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t.parent != null || !PlayerRef.HasVehicleController(t)) continue;
                LogHinge(t.name, t);
            }
            Plugin.Log.LogInfo("=== Exhaust survey: frame prefabs (assets) ===");
            foreach (var name in new[] { "PipeRat", "Poloska", "TinyTyrant", "Junker", "Rustcargo", "Duke", "Vulture", "Rustallion" })
            {
                var p = Prefabs.Find(name, "vehicle");
                if (p != null) LogHinge(p.name + " [asset]", p.transform);
            }
        }

        private static void LogHinge(string label, Transform car)
        {
            var h = FindChild(car, "hinge_exhaust");
            if (h == null) { Plugin.Log.LogInfo(label + ": no hinge_exhaust"); return; }
            string s = label + ": hinge_exhaust localPos " + h.localPosition.ToString("0.000") + " localEuler " + h.localEulerAngles.ToString("0.0")
                + " parent=" + (h.parent != null ? h.parent.name : "-") + " worldEuler " + h.eulerAngles.ToString("0.0") + " carEuler " + car.eulerAngles.ToString("0.0");
            foreach (Transform c in h)
                s += "\n      child " + c.name + " tag=" + c.tag + " localPos " + c.localPosition.ToString("0.000") + " localEuler " + c.localEulerAngles.ToString("0.0") + " scale " + c.localScale.ToString("0.00");
            var vis = FindChild(car, "exhaustHingeVisible");
            if (vis != null) s += "\n      exhaustHingeVisible localPos " + vis.localPosition.ToString("0.000") + " localEuler " + vis.localEulerAngles.ToString("0.0") + " worldEuler " + vis.eulerAngles.ToString("0.0");
            Plugin.Log.LogInfo(s);
        }

        private static bool PlayerPose(out Vector3 p, out Vector3 fwd)
        {
            var pl = PlayerRef.Player;
            var cam = Camera.main;
            if (pl == null && cam == null) { p = Vector3.zero; fwd = Vector3.forward; return false; }
            p = pl != null ? pl.transform.position : cam.transform.position;
            fwd = cam != null ? cam.transform.forward : pl.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            return true;
        }

        internal static Transform FindChild(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++) { var c = root.GetChild(i); if (c.name == name) return c; }
            for (int i = 0; i < root.childCount; i++) { var r = FindChild(root.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        internal static PlayMakerFSM FindFsm(GameObject car, string objectName, string fsmName)
        {
            foreach (var f in car.GetComponentsInChildren<PlayMakerFSM>(true))
                if (f.gameObject.name == objectName && f.FsmName == fsmName) return f;
            return null;
        }

        private static bool Alive(PlayMakerFSM f) { return f != null && f.gameObject != null; }
    }

    // =============================================================== prefab lookup
    // Asset prefabs = GameObjects without a scene. Matched by prefab name first, then by the in-game name
    // (ItemName FSM: variable ItemName, or the text of its UiTextSetText actions), exact then contains.
    internal static class Prefabs
    {
        private class Entry { public GameObject Go; public string Name; public string Display; public string Kind; }
        private static List<Entry> _all;

        internal static void Invalidate() { _all = null; }

        internal static GameObject Find(string query, string kind)
        {
            if (string.IsNullOrEmpty(query)) return null;
            if (_all == null) Scan();
            string q = query.Trim();
            Entry e = _all.FirstOrDefault(x => x.Kind == kind && string.Equals(x.Name, q, StringComparison.OrdinalIgnoreCase))
                   ?? _all.FirstOrDefault(x => string.Equals(x.Name, q, StringComparison.OrdinalIgnoreCase))
                   ?? _all.FirstOrDefault(x => x.Kind == kind && string.Equals(x.Display, q, StringComparison.OrdinalIgnoreCase))
                   ?? _all.FirstOrDefault(x => string.Equals(x.Display, q, StringComparison.OrdinalIgnoreCase))
                   ?? _all.FirstOrDefault(x => x.Kind == kind && x.Display != null && x.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? _all.FirstOrDefault(x => x.Kind == kind && x.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            if (e == null)
            {
                var near = _all.Where(x => x.Kind == kind).Select(x => x.Name + (x.Display != null ? " \"" + x.Display + "\"" : "")).Take(40);
                Plugin.Log.LogWarning("No " + kind + " prefab matches \"" + q + "\". Known: " + string.Join(", ", near.ToArray()));
                return null;
            }
            Plugin.Verbose("Prefab \"" + q + "\" -> " + e.Name + (e.Display != null ? " (\"" + e.Display + "\")" : "") + " [" + e.Kind + "]");
            return e.Go;
        }

        // Any asset root prefab by name (carcasses, enemies ... anything with a PlayMaker FSM), exact then contains.
        internal static GameObject FindAny(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string q = query.Trim();
            GameObject partial = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null) continue;
                var go = f.gameObject;
                if (go.scene.IsValid() || go.transform.parent != null) continue;
                if (string.Equals(go.name, q, StringComparison.OrdinalIgnoreCase)) return go;
                if (partial == null && go.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) partial = go;
            }
            if (partial != null) Plugin.Verbose("Prefab \"" + q + "\" -> " + partial.name + " (partial match)");
            return partial;
        }

        private static void Scan()
        {
            _all = new List<Entry>();
            var byGo = new Dictionary<GameObject, List<PlayMakerFSM>>();
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null) continue;
                var go = f.gameObject;
                if (go.scene.IsValid() || go.transform.parent != null) continue;   // assets only, roots only
                List<PlayMakerFSM> l;
                if (!byGo.TryGetValue(go, out l)) byGo[go] = l = new List<PlayMakerFSM>();
                l.Add(f);
            }
            foreach (var kv in byGo)
            {
                var names = new HashSet<string>(kv.Value.Select(f => f.FsmName));
                string kind;
                if (names.Contains("RpmGear") || names.Contains("getFuel") || names.Contains("CrashDamage")) kind = "vehicle";
                else if (names.Contains("ID")) kind = IdOf(kv.Value) ?? "item";
                else continue;
                _all.Add(new Entry { Go = kv.Key, Name = kv.Key.name, Display = DisplayOf(kv.Value), Kind = kind });
            }
            Plugin.Verbose("Prefab scan: " + _all.Count + " vehicles/items");
        }

        private static string IdOf(List<PlayMakerFSM> fsms)
        {
            var id = fsms.FirstOrDefault(f => f.FsmName == "ID");
            var v = id != null ? id.FsmVariables.GetFsmString("ID") : null;
            return v != null && !string.IsNullOrEmpty(v.Value) ? v.Value : null;
        }

        private static string DisplayOf(List<PlayMakerFSM> fsms)
        {
            var f = fsms.FirstOrDefault(x => x.FsmName == "ItemName");
            if (f == null) return null;
            var v = f.FsmVariables.GetFsmString("ItemName");
            if (v != null && !string.IsNullOrEmpty(v.Value)) return v.Value;
            try
            {
                // wheels etc. carry the name only as the text of a UiTextSetText action
                foreach (var st in f.FsmStates)
                    foreach (var a in st.Actions)
                    {
                        if (a == null || a.GetType().Name != "UiTextSetText") continue;
                        var fld = a.GetType().GetField("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var fs = fld != null ? fld.GetValue(a) as FsmString : null;
                        if (fs != null && !fs.UseVariable && !string.IsNullOrEmpty(fs.Value)) return fs.Value;
                    }
            }
            catch (Exception) { }
            return null;
        }
    }

    // =============================================================== who writes VehicleController.input
    // The car's DriveTrigger/INPUT FSMs (INPUT_AxisInput, INPUT_AxisSteering, ...) copy the legacy axes into input.Throttle/Brakes/
    // Clutch/Steering every frame, player inside or not. While the mod drives, they are disabled; Release() restores them
    // (single-state FSMs, so PlayMaker's restart-on-enable is harmless).
    internal class InputControl
    {
        private static readonly string[] Names = { "INPUT_AxisInput", "INPUT_AxisSteering", "INPUT_NormalizedAxisInput", "INPUT_MouseSteering" };
        private readonly List<PlayMakerFSM> _taken = new List<PlayMakerFSM>();
        private readonly GameObject _car;
        private bool _autoWas;

        internal InputControl(GameObject car) { _car = car; }

        internal bool Taken { get { return _taken.Count > 0; } }

        internal void Take()
        {
            _taken.Clear();
            foreach (var f in _car.GetComponentsInChildren<PlayMakerFSM>(true))
                if (f.gameObject.name == "INPUT" && f.enabled && Array.IndexOf(Names, f.FsmName) >= 0) { f.enabled = false; _taken.Add(f); }
            _autoWas = Nwh.AutoInput(_car, false);
            Plugin.Verbose("Input control taken: " + _taken.Count + " INPUT FSMs paused, autoSetInput was " + _autoWas);
        }

        internal void Release()
        {
            foreach (var f in _taken) if (f != null) f.enabled = true;
            _taken.Clear();
            if (_car != null) Nwh.AutoInput(_car, _autoWas);
            Plugin.Verbose("Input control released");
        }
    }

    // =============================================================== vanilla spawn recipe
    internal static class Register
    {
        internal static void Name(GameObject go, string prefab)
        {
            int id = -1;
            var counterGo = GameObject.Find("itemNameID");
            if (counterGo != null)
                foreach (var f in counterGo.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "itemNameID")
                    {
                        var v = f.FsmVariables.GetFsmInt("intName");
                        if (v != null) { v.Value += 1; id = v.Value; }
                        break;
                    }
            go.name = prefab + "(Clone)" + (id >= 0 ? id.ToString() : "");
        }

        internal static void Add(GameObject go, bool isVehicle)
        {
            var reg = GameObject.Find("NewGO_ArrayList");
            if (reg == null) return;
            var proxies = reg.GetComponents<PlayMakerArrayListProxy>();
            string want = isVehicle ? "car" : "item";
            var list = proxies.FirstOrDefault(p => (p.referenceName ?? "").ToLowerInvariant().Contains(want));
            if (list != null) list.arrayList.Add(go);
            else Plugin.Log.LogWarning("NewGO_ArrayList has no '" + want + "' list");
        }
    }

    // =============================================================== NWH Vehicle Physics 2 by reflection
    internal static class Nwh
    {
        private static Component Vc(GameObject car)
        {
            foreach (var c in car.GetComponents<Component>())
                if (c != null && c.GetType().Name == "VehicleController") return c;
            return null;
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null) return p.GetValue(o, null);
            return null;
        }

        private static bool Set(object o, string name, object value)
        {
            if (o == null) return false;
            var t = o.GetType();
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) { f.SetValue(o, value); return true; }
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite) { p.SetValue(o, value, null); return true; }
            return false;
        }

        private static object Engine(GameObject car) { return Get(Get(Vc(car), "powertrain"), "engine"); }

        internal static bool EngineRunning(GameObject car)
        {
            try { var r = Get(Engine(car), "IsRunning"); return r is bool && (bool)r; }
            catch (Exception e) { Plugin.Log.LogWarning("EngineRunning: " + e.Message); return false; }
        }

        internal static void StartEngine(GameObject car)
        {
            try
            {
                var eng = Engine(car);
                if (eng == null) { Plugin.Log.LogWarning("No powertrain.engine"); return; }
                var m = eng.GetType().GetMethod("StartEngine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null) { Plugin.Log.LogWarning("No StartEngine()"); return; }
                m.Invoke(eng, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("StartEngine: " + e.Message); }
        }

        // returns the previous value
        internal static bool AutoInput(GameObject car, bool on)
        {
            try
            {
                var input = Get(Vc(car), "input");
                var was = Get(input, "autoSetInput");
                if (!Set(input, "autoSetInput", on)) Plugin.Verbose("no autoSetInput");
                return was is bool ? (bool)was : false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("AutoInput: " + e.Message); return false; }
        }

        internal static int GearIndex(GameObject car)
        {
            try { var g = Get(Get(Get(Vc(car), "powertrain"), "transmission"), "Gear"); return g is int ? (int)g : 0; }
            catch (Exception) { return 0; }
        }

        private static bool _shiftLogged;
        // input.ShiftInto = gear index (-1 R, 0 N, 1 = 1st/D); the vehicle consumes it on its next update
        internal static void ShiftInto(GameObject car, int gear)
        {
            try
            {
                var input = Get(Vc(car), "input");
                bool ok = Set(input, "ShiftInto", gear);
                if (!ok) ok = Set(input, "shiftInto", gear);
                if (!_shiftLogged) { _shiftLogged = true; Plugin.Verbose("ShiftInto " + gear + (ok ? "" : " FAILED (no ShiftInto on input)")); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("ShiftInto: " + e.Message); }
        }

        // one-line state readback for the drive test log
        internal static string Diag(GameObject car)
        {
            try
            {
                var vc = Vc(car); var input = Get(vc, "input"); var pt = Get(vc, "powertrain");
                var eng = Get(pt, "engine"); var tr = Get(pt, "transmission");
                return "thr=" + Get(input, "Throttle") + " clutch=" + Get(input, "Clutch") + " hb=" + Get(input, "Handbrake")
                     + " rpm=" + F(Get(eng, "RPM") ?? Get(eng, "OutputRPM")) + " trType=" + Get(tr, "transmissionType")
                     + " active=" + (Get(vc, "IsActive") ?? Get(vc, "Active")) + " enabled=" + (vc != null ? ((Behaviour)vc).enabled.ToString() : "?");
            }
            catch (Exception e) { return "diag: " + e.Message; }
        }

        private static string F(object o) { return o is float ? ((float)o).ToString("0") : (o == null ? "?" : o.ToString()); }

        internal static string Gear(GameObject car)
        {
            try { var tr = Get(Get(Vc(car), "powertrain"), "transmission"); var g = Get(tr, "Gear"); return g != null ? g.ToString() : "?"; }
            catch (Exception) { return "?"; }
        }

        internal static void SetHandbrake(GameObject car, float value)
        {
            try { var input = Get(Vc(car), "input"); if (!Set(input, "Handbrake", value)) Plugin.Verbose("no input.Handbrake"); }
            catch (Exception e) { Plugin.Log.LogWarning("SetHandbrake: " + e.Message); }
        }

        internal static void SetInput(GameObject car, float throttle, float steering, float brakes)
        {
            try
            {
                var input = Get(Vc(car), "input");
                if (input == null) { Plugin.Log.LogWarning("No VehicleController.input"); return; }
                Set(input, "Throttle", throttle);
                Set(input, "Steering", steering);
                Set(input, "Brakes", brakes);
            }
            catch (Exception e) { Plugin.Log.LogWarning("SetInput: " + e.Message); }
        }
    }
}

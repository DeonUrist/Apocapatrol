using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // Runner on the hidden plugin object: the car builder (template spawner + convoy), the in-game check, the persistence tick.
    internal class Patrol : MonoBehaviour
    {
        private PlayMakerFSM _menu, _saveLoad;
        private float _nextRefScan;
        private bool _busy;                 // the template spawner's own build (one at a time)
        private int _inGameFrame = -1;      // InGame() is asked by four runner components every frame: evaluated once per frame
        private bool _inGame;

        internal static void ResetForScene() { Prefabs.Invalidate(); Register.Invalidate(); }

        private void Update()
        {
            PatrolPersistence.Tick(this);
            if (!InGame()) return;
            Ram.Tick();
            ExitSpeed.Tick();
            if (Time.unscaledTime >= _nextPrune) { _nextPrune = Time.unscaledTime + 30f; PruneIgnoreSignatures(); }
        }

        // occupants of destroyed cars (the game deletes cars beyond 5 km, the cleanup removes more) leave their signature behind
        private static float _nextPrune;
        private static readonly List<GameObject> _deadKeys = new List<GameObject>();
        private static void PruneIgnoreSignatures()
        {
            _deadKeys.Clear();
            foreach (var k in _ignoreSignature.Keys) if (k == null) _deadKeys.Add(k);
            foreach (var k in _deadKeys) _ignoreSignature.Remove(k);
        }

        // [Debug] AiOverlay: one OnGUI for every pilot (an OnGUI on each car would be dispatched per IMGUI event whether or not it draws)
        private void OnGUI()
        {
            if (!Plugin.AiOverlay.Value || Time.timeScale <= 0f) return;
            var all = Pilot.All;
            int shown = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) continue;
                GUI.Label(new Rect(10, 10 + 18 * i, 1400, 22), p.OverlayLine());
                shown++;
            }
            // no raider car on the road: the spawn clock instead (only read here - nothing is computed while the overlay is off)
            if (shown == 0) GUI.Label(new Rect(10, 10, 1400, 22), Convoy.OverlayLine());
        }

        // builds a template (from the F8 menu); one at a time
        internal void Spawn(CarTemplate t)
        {
            if (_busy) { Plugin.Verbose("Spawn: a build is still running"); return; }
            if (!InGame()) return;
            _busy = true;
            StartCoroutine(Build(t, null, Quaternion.identity, true, false, null));
        }

        // Builds a template at a given place (convoy spawner). hold = the crew waits for Crew.Release() before driving off;
        // onDone receives the car (or null) when the build has finished.
        internal void SpawnAt(CarTemplate t, Vector3 pos, Quaternion rot, bool hold, Action<GameObject> onDone)
        {
            if (!InGame()) { if (onDone != null) onDone(null); return; }
            StartCoroutine(Build(t, pos, rot, false, hold, onDone));
        }

        internal bool InGame()
        {
            if (_inGameFrame == Time.frameCount) return _inGame;
            _inGameFrame = Time.frameCount;
            return _inGame = InGameNow();
        }

        private bool InGameNow()
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

        // at == null: SpawnDistance in front of the player, facing the player's way; otherwise exactly there.
        // The work is spread over several frames (one Instantiate group per frame) so a build never stalls a single frame for long.
        private IEnumerator Build(CarTemplate tpl, Vector3? at, Quaternion rot, bool menu, bool hold, Action<GameObject> onDone)
        {
            GameObject car = null;
            try
            {
                Vector3 pos;
                if (at.HasValue) pos = at.Value;
                else
                {
                    Vector3 p, fwd;
                    if (!PlayerPose(out p, out fwd)) { Plugin.Log.LogWarning("No player found"); yield break; }
                    pos = p + fwd * Plugin.SpawnDistance + Vector3.up * 1.0f;
                    rot = Quaternion.LookRotation(fwd);
                }
                Plugin.Verbose("Building template " + tpl.Name + ": " + tpl.Describe());

                var body = Prefabs.Find(tpl.Body, "vehicle");
                if (body == null) { Plugin.Log.LogWarning("Body prefab not found: " + tpl.Body); yield break; }

                car = UnityEngine.Object.Instantiate(body, pos, rot);
                car.SetActive(true);
                Paint.Apply(car, body.name);   // Textures/<body>.png or <texture>.png over the frame (e.g. poloska.png, junker.png)
                Register.Name(car, body.name); Register.Add(car, true);
                Plugin.Verbose("Car frame " + car.name + " at " + pos);

                yield return null;   // let the frame's FSMs start (hinges, getEngine, START ...)

                int parts = 0;
                if (tpl.Parts != null)
                {
                    // a JSON template: its exact part list, hinge by hinge (parts on parts too), a few per frame
                    var placed = new GameObject[tpl.Parts.Length];
                    for (int i = 0; i < tpl.Parts.Length; i++)
                    {
                        placed[i] = AttachTemplatePart(car, tpl, i, placed);
                        if (placed[i] != null) parts++;
                        if (i % 4 == 3) { yield return null; if (car == null) yield break; }
                    }
                    var bumperHinge = FindChild(car.transform, "hinge_bumper_front");
                    if (bumperHinge != null && !HasPart(bumperHinge))
                    {
                        string bumper = tpl.RollBumper();
                        if (bumper.Length > 0) { Plugin.Verbose("Front bumper roll: " + bumper); parts += AttachAll(car, new[] { "hinge_bumper_front" }, bumper, "bumper"); }
                    }
                }
                else
                {
                    parts += AttachAll(car, new[] { "hinge_wheel_FL", "hinge_wheel_FR" }, tpl.Wheel, "wheel");
                    yield return null;
                    parts += AttachAll(car, new[] { "hinge_wheel_RL", "hinge_wheel_RR" }, tpl.RearWheel.Length > 0 ? tpl.RearWheel : tpl.Wheel, "wheel");
                    yield return null;
                    string bumper = tpl.RollBumper();
                    if (bumper.Length > 0) { Plugin.Verbose("Front bumper roll: " + bumper); parts += AttachAll(car, new[] { "hinge_bumper_front" }, bumper, "bumper"); }
                    parts += AttachAll(car, new[] { "hinge_engine" }, tpl.Engine, "engine");
                    yield return null;
                    parts += AttachAll(car, new[] { "hinge_radiator" }, tpl.Radiator, "radiator");
                    parts += AttachAll(car, new[] { "hinge_steeringwheel" }, tpl.SteeringWheel, "steeringwheel");
                    parts += AttachAll(car, new[] { "hinge_exhaust" }, tpl.Exhaust, "exhaust");
                    yield return null;
                    parts += AttachAll(car, new[] { "hinge_seat_driver" }, tpl.Seat, "seat");
                    parts += AttachAll(car, new[] { "hinge_seat_passenger" }, tpl.PassengerSeat, "seat");
                }
                Plugin.Verbose(parts + " parts attached");

                if (tpl.FillFuel) Fuel(car, Plugin.RollPartFill());
                if (tpl.ReleaseHandbrake) Handbrake(car, false);
                List<GameObject> cargo = null;
                string lootKey = null;
                if (!string.IsNullOrEmpty(tpl.Cargo))
                {
                    bool itemSpec = CarTemplates.IsItemSpec(tpl.Cargo);   // a JSON template's fixed bed items ("dogfood_can:6;akms:1")
                    lootKey = itemSpec ? "Custom" : tpl.Cargo.Equals("Random", StringComparison.OrdinalIgnoreCase) ? Cargo.RollLootType() : tpl.Cargo;
                    string spec = itemSpec ? tpl.Cargo : Cargo.SpecFor(lootKey);
                    float scale = UnityEngine.Random.Range(Mathf.Min(tpl.LootScaleMin, tpl.LootScaleMax), Mathf.Max(tpl.LootScaleMin, tpl.LootScaleMax));
                    Plugin.Verbose("Loot: " + lootKey + " (" + spec + ") x" + Plugin.LootMultiplier.Value + " x" + scale.ToString("0.00") + " (template)");
                    cargo = new List<GameObject>();
                    yield return Cargo.Load(car, spec, scale, cargo);   // a few items per frame, not the whole bed in one
                    if (car == null) yield break;
                }

                yield return null;
                GameObject driver = null, passenger = null;
                if (!string.IsNullOrEmpty(tpl.Driver))
                {
                    if (tpl.Driver.Trim().EndsWith("_Dead", StringComparison.OrdinalIgnoreCase)) driver = SeatDriver(car, tpl.Driver);
                    else driver = SeatLiveDriver(car, tpl.Driver, -1f, CrewPhase.Waiting, 0f, hold);
                }
                yield return null;
                if (!string.IsNullOrEmpty(tpl.Passenger)) passenger = SeatPassenger(car, tpl.Passenger);
                var marker = PatrolMarker.Attach(car, body.name, tpl.Driver, driver, tpl.Passenger, passenger, tpl.Rams);
                marker.CargoKey = lootKey ?? "";
                Paint.ApplyCargo(car, lootKey);   // the container's texture by load (Textures/cargo_*.png)

                yield return new WaitForSeconds(1f);
                if (car == null) yield break;
                if (cargo != null) Cargo.SettleFsms(cargo);
                if (Explode.IsWreck(car)) yield break;   // crew killed during the build: it already blew up
                SetPartConditions(car);
                if (cargo != null) Cargo.ApplyForcedConditions(cargo);   // "#100" items (the mechanic truck's V8) keep their set condition
                FillParts(car);

                yield return StartUp(car);
            }
            finally
            {
                if (menu) _busy = false;
                if (onDone != null) onDone(car);
            }
        }

        // The game's own ignition: START [Start] FSM Ignition -> Start (engine sound, RPM, IsRunning), NWH StartEngine() as a fallback.
        // Used by the build and when a saved car is revived after a load.
        internal static IEnumerator StartUp(GameObject car)
        {
            if (Explode.IsWreck(car)) yield break;
            var start = FindFsm(car, "START", "Start");
            if (start == null) { Plugin.Log.LogWarning("No START/Start FSM on " + (car != null ? car.name : "?")); yield break; }
            Plugin.Verbose("START state before: " + start.ActiveStateName);
            start.Fsm.SetState("Ignition");
            yield return null;
            yield return null;
            if (Explode.IsWreck(car)) yield break;   // blew up mid-start: no more ignition
            start.Fsm.SetState("Start");
            yield return new WaitForSeconds(1.5f);
            if (Explode.IsWreck(car)) yield break;
            bool running = Nwh.EngineRunning(car);
            Plugin.Verbose("START state: " + start.ActiveStateName + "  engine running: " + running);
            if (!running)
            {
                Nwh.StartEngine(car);
                yield return new WaitForSeconds(1f);
                if (Explode.IsWreck(car)) yield break;
                Plugin.Verbose("After NWH StartEngine(): running=" + Nwh.EngineRunning(car) + "  START state: " + start.ActiveStateName);
            }
        }

        private static int AttachAll(GameObject car, string[] hingeNames, string partQuery, string kind)
        {
            if (string.IsNullOrEmpty(partQuery)) return 0;
            // bumpers carry no ID FSM, so they are not in the item catalog: look them up as plain asset roots
            var prefab = kind == "bumper" ? Prefabs.FindAny(partQuery) : Prefabs.Find(partQuery, kind);
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
        private static GameObject Attach(GameObject prefab, Transform hinge)
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
            return part;
        }

        // One part of a JSON template: the hinge is found by its path under the frame or under the part it sits on (by name
        // anywhere below as a fallback); a hinge that already holds a part (the frame came with one) keeps it, and that part
        // stands in as the parent for the parts listed on it.
        private static GameObject AttachTemplatePart(GameObject car, CarTemplate tpl, int index, GameObject[] placed)
        {
            var p = tpl.Parts[index];
            if (string.IsNullOrEmpty(p.prefab)) return null;
            Transform root = car.transform;
            if (p.parent >= 0)
            {
                var host = placed[p.parent];
                if (host == null) { Plugin.Log.LogWarning(tpl.Name + ": part [" + index + "] " + p.prefab + " skipped, its parent part [" + p.parent + "] is missing"); return null; }
                root = host.transform;
            }
            var hinge = FindPath(root, p.hinge);
            if (hinge == null && p.hinge.Length > 0)
            {
                int slash = p.hinge.LastIndexOf('/');
                hinge = FindChild(root, slash >= 0 ? p.hinge.Substring(slash + 1) : p.hinge);
            }
            if (hinge == null) { Plugin.Log.LogWarning(tpl.Name + ": hinge " + p.hinge + " not found on " + root.name + " for " + p.prefab); return null; }
            var existing = PartOn(hinge);
            if (existing != null) { Plugin.Verbose("  " + hinge.name + " already holds " + existing.name + ", " + p.prefab + " not added"); return existing; }
            var prefab = Prefabs.FindAny(p.prefab) ?? Prefabs.Find(p.prefab, "item");
            if (prefab == null) { Plugin.Log.LogWarning(tpl.Name + ": part prefab not found: " + p.prefab); return null; }
            if (p.hingePos != null && p.hingePos.Length == 3) hinge.localPosition = new Vector3(p.hingePos[0], p.hingePos[1], p.hingePos[2]);
            if (p.hingeRot != null && p.hingeRot.Length == 3) hinge.localEulerAngles = new Vector3(p.hingeRot[0], p.hingeRot[1], p.hingeRot[2]);
            var part = Attach(prefab, hinge);
            if (p.pos != null && p.pos.Length == 3) part.transform.localPosition = new Vector3(p.pos[0], p.pos[1], p.pos[2]);
            if (p.rot != null && p.rot.Length == 3) part.transform.localEulerAngles = new Vector3(p.rot[0], p.rot[1], p.rot[2]);
            return part;
        }

        private static bool HasPart(Transform hinge) { return PartOn(hinge) != null; }

        private static GameObject PartOn(Transform hinge)
        {
            for (int i = 0; i < hinge.childCount; i++)
            {
                var c = hinge.GetChild(i);
                bool tagged;
                try { tagged = c.CompareTag("vehPart"); } catch (Exception) { tagged = false; }
                if (tagged) return c.gameObject;
            }
            return null;
        }

        internal static Transform FindPath(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;
            var t = root;
            foreach (var seg in path.Split('/'))
            {
                Transform next = null;
                for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).name == seg) { next = t.GetChild(i); break; }
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        // The tank: <car>/Fuel [LiquidAmount] Liquid / LiquidCapacity, filled to pct % of the capacity.
        private static void Fuel(GameObject car, float pct)
        {
            var fuel = FindChild(car.transform, "Fuel");
            if (fuel == null) { Plugin.Log.LogWarning("No Fuel child"); return; }
            if (!SetLiquid(fuel.gameObject, pct, "Fuel")) Plugin.Log.LogWarning("Fuel/LiquidAmount FSM not found");
        }

        // Engine oil and radiator water live on the part's "cap" child ([LiquidAmount] Liquid / LiquidCapacity, 2 l oil, 3 l water);
        // filled to a rolled % once the parts sit on their hinges.
        private static void FillParts(GameObject car)
        {
            foreach (var hn in new[] { "hinge_engine", "hinge_radiator" })
            {
                var hinge = FindChild(car.transform, hn);
                if (hinge == null) continue;
                for (int i = 0; i < hinge.childCount; i++)
                {
                    var part = hinge.GetChild(i);
                    var cap = FindChild(part, "cap");
                    if (cap == null) continue;
                    SetLiquid(cap.gameObject, Plugin.RollPartFill(), part.name);
                }
            }
        }

        private static bool SetLiquid(GameObject holder, float pct, string label)
        {
            foreach (var f in holder.GetComponents<PlayMakerFSM>())
            {
                if (f.FsmName != "LiquidAmount") continue;
                var liquid = f.FsmVariables.GetFsmFloat("Liquid");
                var cap = f.FsmVariables.GetFsmFloat("LiquidCapacity");
                if (liquid == null || cap == null) { Plugin.Log.LogWarning(label + "/LiquidAmount has no Liquid/LiquidCapacity"); return true; }
                float capacity = cap.Value > 0f ? cap.Value : 20f;
                liquid.Value = capacity * Mathf.Clamp01(pct / 100f);
                Plugin.Verbose(label + ": " + liquid.Value.ToString("0.0") + " / " + capacity + " (" + pct.ToString("0") + " %)");
                return true;
            }
            return false;
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
            Plugin.Verbose("Handbrake " + (on ? "on" : "released") + " (was " + cur + ")");
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
            Plugin.Verbose("Driver " + drv.name + " on " + sit.name + " at " + pos + " (" + bones + " ragdoll bones, " + drvCols.Length + " colliders)");
            return drv;
        }

        // A live enemy at the wheel with its AI off: instantiated at sitPos, the mover/AI FSMs disabled before they Start,
        // root Rigidbody kinematic and parented to the seat, collisions with the car ignored. Health/Damage/Bodypart stay
        // vanilla so it can be shot; the Crew component drives the car and reacts to its death.
        internal static GameObject SeatLiveDriver(GameObject car, string prefabName, float health = -1f, CrewPhase phase = CrewPhase.Waiting, float seated = 0f, bool hold = false)
        {
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null) { Plugin.Log.LogWarning("No sitPos on " + car.name); return null; }
            var drv = SeatOccupant(car, prefabName, sit, "Driver", false, health);
            if (drv == null) return null;
            if (Plugin.RangedCombat.Value && PassengerGuard.IsRangedHuman(drv)) PassengerGuard.AttachDriver(drv, car, sit);
            var crew = phase == CrewPhase.Waiting && seated <= 0f ? Crew.Attach(car, drv) : Crew.Restore(car, drv, phase, seated);
            crew.Hold = hold;
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

            var carCols = car.GetComponentsInChildren<Collider>(true);   // once, not once per occupant collider
            foreach (var a in go.GetComponentsInChildren<Collider>(true))
                foreach (var b in carCols)
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

            var root = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            root.isKinematic = true;
            root.interpolation = RigidbodyInterpolation.None;
            go.transform.SetParent(anchor, true);
            if (Plugin.PoseEnabled.Value) Pose.Apply(go, anchor, prefab.name);
            if (health >= 0f) SetHealth(go, health);
            Plugin.Verbose(role + " " + go.name + " on " + anchor.name + " at " + pos);
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
            return IgnoreCollisionsIfChanged(who, car, car.GetComponentsInChildren<Collider>(true));
        }

        // carCols = the car's colliders, fetched by the caller once for every occupant it checks
        internal static int IgnoreCollisionsIfChanged(GameObject who, GameObject car, Collider[] carCols)
        {
            if (who == null || car == null) return 0;
            long sig = 17;
            foreach (var a in who.GetComponentsInChildren<Collider>(true))
                if (a != null && a.enabled && a.gameObject.activeInHierarchy) sig = sig * 31 + a.GetInstanceID();
            foreach (var b in carCols)
                if (b != null && b.enabled && b.gameObject.activeInHierarchy) sig = sig * 31 + b.GetInstanceID();
            long old;
            if (_ignoreSignature.TryGetValue(who, out old) && old == sig) return 0;
            _ignoreSignature[who] = sig;
            int n = IgnoreCollisions(who, car, carCols);
            Plugin.Verbose("Collision ignores re-applied for " + who.name + ": " + n + " pairs");
            return n;
        }

        internal static int IgnoreCollisions(GameObject who, GameObject car)
        {
            if (who == null || car == null) return 0;
            return IgnoreCollisions(who, car, car.GetComponentsInChildren<Collider>(true));
        }

        internal static int IgnoreCollisions(GameObject who, GameObject car, Collider[] carCols)
        {
            if (who == null || car == null) return 0;
            int n = 0;
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
                Plugin.Verbose("Eject: " + root.name + " thrown out of " + car.name + " at " + velocity.magnitude.ToString("0.0") + " m/s ("
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
            Plugin.Verbose("Takeover: " + pax.name + " on " + sit.name + ", " + pairs + " collider pairs vs the car ignored");
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
            Plugin.Verbose("Bail-out: " + mob.name + " got out of " + car.name + " with " + (health > 0f ? health.ToString("0") : "full") + " health at " + pos);
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
            Plugin.Verbose("Part conditions set on " + n + " part(s) (" + Plugin.MinPartHealth.Value + ".." + Plugin.MaxPartHealth.Value + " %)");
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
                Plugin.Verbose("Eject: " + root.name + " did not get clear in 3 s, put down " + Plugin.EjectDistance + " m left of the seat");
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
        private static List<GameObject> _roots;                                   // every asset root with a PlayMaker FSM (crew, carcasses, loot...)
        private static Dictionary<string, GameObject> _byName;
        private static float _nextRescan;

        internal static void Invalidate() { _all = null; _roots = null; _byName = null; _nextRescan = 0f; }

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

        // Any asset root prefab by name (carcasses, enemies ... anything with a PlayMaker FSM), exact then contains — from the
        // cached scan. A miss re-scans once (a prefab loaded after the first scan), at most every 30 s.
        internal static GameObject FindAny(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string q = query.Trim();
            if (_all == null) Scan();
            var hit = Lookup(q);
            if (hit == null && Time.unscaledTime >= _nextRescan) { _nextRescan = Time.unscaledTime + 30f; Scan(); hit = Lookup(q); }
            return hit;
        }

        private static GameObject Lookup(string q)
        {
            GameObject go;
            if (_byName.TryGetValue(q, out go)) return go;
            foreach (var r in _roots)
                if (r != null && r.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Plugin.Verbose("Prefab \"" + q + "\" -> " + r.name + " (partial match)");
                    return r;
                }
            return null;
        }

        private static void Scan()
        {
            _all = new List<Entry>();
            _roots = new List<GameObject>();
            _byName = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            var byGo = new Dictionary<GameObject, List<PlayMakerFSM>>();
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null) continue;
                var go = f.gameObject;
                if (go.scene.IsValid() || go.transform.parent != null) continue;   // assets only, roots only
                List<PlayMakerFSM> l;
                if (!byGo.TryGetValue(go, out l))
                {
                    byGo[go] = l = new List<PlayMakerFSM>();
                    _roots.Add(go);
                    if (!_byName.ContainsKey(go.name)) _byName[go.name] = go;
                }
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
            Plugin.Verbose("Prefab scan: " + _all.Count + " vehicles/items, " + _roots.Count + " prefabs with FSMs");
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
            Nwh.ForgetInput(_car);
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

    // =============================================================== exit speed
    // The game lets the player leave a car only below 6 m/s: PlayerCamera [DriveUse] compares the car's speed against a literal 6 in
    // its inCar / over 2 / TooFast states (FloatCompare float2). A truck shoving the player's car keeps it above that for good, so the
    // literal is replaced by [Debug] ExitSpeedKmh (converted to m/s); re-checked every few seconds (live config edits, new Player object).
    internal static class ExitSpeed
    {
        private static PlayMakerFSM _fsm;
        private static float _next, _applied = -1f;
        private static readonly List<FsmFloat> _limits = new List<FsmFloat>();

        internal static void Tick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 5f;
            float want = Mathf.Max(1f, Plugin.ExitSpeedKmh.Value) / 3.6f;
            if (_fsm == null || _fsm.gameObject == null)
            {
                _fsm = null; _limits.Clear(); _applied = -1f;
                var player = PlayerRef.Player;
                var cam = player != null ? Patrol.FindChild(player, "PlayerCamera") : null;
                if (cam == null) return;
                foreach (var f in cam.GetComponents<PlayMakerFSM>()) if (f.FsmName == "DriveUse") { _fsm = f; break; }
                if (_fsm == null) return;
                try
                {
                    foreach (var st in _fsm.FsmStates)
                        foreach (var a in st.Actions)
                        {
                            if (a == null || a.GetType().Name != "FloatCompare") continue;
                            var fld = a.GetType().GetField("float2", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var v = fld != null ? fld.GetValue(a) as FsmFloat : null;
                            if (v != null && !v.UseVariable && Mathf.Abs(v.Value - 6f) < 0.01f) _limits.Add(v);   // the vanilla literal
                        }
                }
                catch (Exception e) { Plugin.Log.LogWarning("ExitSpeed: " + e.Message); }
                if (_limits.Count == 0) { Plugin.Log.LogWarning("ExitSpeed: no 6 m/s compare found in PlayerCamera [DriveUse]"); return; }
            }
            if (Mathf.Abs(_applied - want) < 0.001f) return;
            foreach (var v in _limits) v.Value = want;
            _applied = want;
            Plugin.Verbose("Exit speed limit set to " + Plugin.ExitSpeedKmh.Value.ToString("0") + " km/h (" + _limits.Count + " compare(s) in PlayerCamera [DriveUse])");
        }
    }

    // =============================================================== vanilla spawn recipe
    // The counter FSM and the registry lists are looked up once and kept (GameObject.Find is a scene-wide name search; a loot
    // truck used to run it twice per item, sixty-odd times in one frame). Re-found when the objects are gone (scene change).
    internal static class Register
    {
        private static PlayMakerFSM _counterFsm;
        private static FsmInt _counter;
        private static GameObject _reg;
        private static PlayMakerArrayListProxy _cars, _items;

        internal static void Invalidate() { _counterFsm = null; _counter = null; _reg = null; _cars = null; _items = null; }

        private static FsmInt Counter()
        {
            if (_counterFsm != null && _counterFsm.gameObject != null && _counter != null) return _counter;
            _counterFsm = null; _counter = null;
            var counterGo = GameObject.Find("itemNameID");
            if (counterGo == null) return null;
            foreach (var f in counterGo.GetComponents<PlayMakerFSM>())
                if (f.FsmName == "itemNameID") { _counterFsm = f; _counter = f.FsmVariables.GetFsmInt("intName"); break; }
            return _counter;
        }

        private static GameObject Registry()
        {
            if (_reg != null) return _reg;
            _cars = _items = null;
            _reg = GameObject.Find("NewGO_ArrayList");
            if (_reg == null) return null;
            foreach (var p in _reg.GetComponents<PlayMakerArrayListProxy>())
            {
                string n = (p.referenceName ?? "").ToLowerInvariant();
                if (_cars == null && n.Contains("car")) _cars = p;
                if (_items == null && n.Contains("item")) _items = p;
            }
            return _reg;
        }

        internal static void Name(GameObject go, string prefab)
        {
            int id = -1;
            var v = Counter();
            if (v != null) { v.Value += 1; id = v.Value; }
            go.name = prefab + "(Clone)" + (id >= 0 ? id.ToString() : "");
        }

        // Takes a car and everything under it (parts, cargo) out of the game's NewGO lists before it is destroyed, so the
        // save does not keep references to it.
        internal static void RemoveTree(GameObject root)
        {
            var reg = Registry();
            if (reg == null || root == null) return;
            var set = new HashSet<GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) set.Add(t.gameObject);
            foreach (var p in reg.GetComponents<PlayMakerArrayListProxy>())
            {
                var list = p.arrayList;
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var go = list[i] as GameObject;
                    if (go != null && set.Contains(go)) list.RemoveAt(i);
                }
            }
        }

        internal static void Add(GameObject go, bool isVehicle)
        {
            if (Registry() == null) return;
            var list = isVehicle ? _cars : _items;
            if (list != null && list.arrayList != null) list.arrayList.Add(go);
            else Plugin.Log.LogWarning("NewGO_ArrayList has no '" + (isVehicle ? "car" : "item") + "' list");
        }
    }

    // =============================================================== NWH Vehicle Physics 2 by reflection
    // Everything is reached by reflection (no compile dependency on the NWH assembly). The VehicleController and its input /
    // powertrain / engine / transmission objects are stable for the life of the car and the member lookups never change, so
    // both are cached: a Handle per car and a MemberInfo table per (type, name). The pilots ask several times per physics step.
    internal static class Nwh
    {
        private class Handle
        {
            public Component Vc;
            public object Input, Engine, Transmission;
            public float Throttle = float.NaN, Steering = float.NaN, Brakes = float.NaN, WrittenAt = -1f;   // last SetInput, to skip identical writes
        }

        private const float InputRefresh = 0.5f;   // identical inputs are still re-written this often (a stopped or idle car writes 3 boxed floats per step otherwise)

        private static readonly Dictionary<int, Handle> _handles = new Dictionary<int, Handle>();
        private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> _members = new Dictionary<Type, Dictionary<string, MemberInfo>>();
        private static float _nextSweep;

        private static Handle Of(GameObject car)
        {
            if (car == null) return null;
            if (Time.unscaledTime >= _nextSweep) { _nextSweep = Time.unscaledTime + 30f; Sweep(); }
            Handle h;
            int id = car.GetInstanceID();
            if (_handles.TryGetValue(id, out h) && h.Vc != null) return h;
            h = new Handle();
            foreach (var c in car.GetComponents<Component>())
                if (c != null && c.GetType().Name == "VehicleController") { h.Vc = c; break; }
            if (h.Vc == null) return null;
            h.Input = Get(h.Vc, "input");
            var pt = Get(h.Vc, "powertrain");
            h.Engine = Get(pt, "engine");
            h.Transmission = Get(pt, "transmission");
            _handles[id] = h;
            return h;
        }

        // drop the handles of destroyed cars
        private static void Sweep()
        {
            var dead = new List<int>();
            foreach (var kv in _handles) if (kv.Value.Vc == null) dead.Add(kv.Key);
            foreach (var k in dead) _handles.Remove(k);
        }

        private static MemberInfo Member(Type t, string name)
        {
            Dictionary<string, MemberInfo> d;
            if (!_members.TryGetValue(t, out d)) _members[t] = d = new Dictionary<string, MemberInfo>();
            MemberInfo m;
            if (d.TryGetValue(name, out m)) return m;
            m = (MemberInfo)t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            d[name] = m;
            return m;
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            var m = Member(o.GetType(), name);
            var f = m as FieldInfo;
            if (f != null) return f.GetValue(o);
            var p = m as PropertyInfo;
            return p != null ? p.GetValue(o, null) : null;
        }

        private static bool Set(object o, string name, object value)
        {
            if (o == null) return false;
            var m = Member(o.GetType(), name);
            var f = m as FieldInfo;
            if (f != null) { f.SetValue(o, value); return true; }
            var p = m as PropertyInfo;
            if (p != null && p.CanWrite) { p.SetValue(o, value, null); return true; }
            return false;
        }

        internal static bool EngineRunning(GameObject car)
        {
            try { var h = Of(car); var r = h != null ? Get(h.Engine, "IsRunning") : null; return r is bool && (bool)r; }
            catch (Exception e) { Plugin.Log.LogWarning("EngineRunning: " + e.Message); return false; }
        }

        internal static void StartEngine(GameObject car)
        {
            try
            {
                var h = Of(car);
                var eng = h != null ? h.Engine : null;
                if (eng == null) { Plugin.Log.LogWarning("No powertrain.engine"); return; }
                var m = eng.GetType().GetMethod("StartEngine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null) { Plugin.Log.LogWarning("No StartEngine()"); return; }
                m.Invoke(eng, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("StartEngine: " + e.Message); }
        }

        internal static void StopEngine(GameObject car)
        {
            try
            {
                var h = Of(car);
                var eng = h != null ? h.Engine : null;
                if (eng == null) return;
                var m = eng.GetType().GetMethod("StopEngine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null) { Plugin.Verbose("No StopEngine()"); return; }
                m.Invoke(eng, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("StopEngine: " + e.Message); }
        }

        // returns the previous value
        internal static bool AutoInput(GameObject car, bool on)
        {
            try
            {
                var h = Of(car);
                var input = h != null ? h.Input : null;
                var was = Get(input, "autoSetInput");
                if (!Set(input, "autoSetInput", on)) Plugin.Verbose("no autoSetInput");
                return was is bool ? (bool)was : false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("AutoInput: " + e.Message); return false; }
        }

        internal static int GearIndex(GameObject car)
        {
            try { var h = Of(car); var g = h != null ? Get(h.Transmission, "Gear") : null; return g is int ? (int)g : 0; }
            catch (Exception) { return 0; }
        }

        // input.ShiftInto = gear index (-1 R, 0 N, 1 = 1st/D); the vehicle consumes it on its next update
        internal static void ShiftInto(GameObject car, int gear)
        {
            try
            {
                var h = Of(car);
                if (h == null) return;
                if (!Set(h.Input, "ShiftInto", gear)) Set(h.Input, "shiftInto", gear);
            }
            catch (Exception e) { Plugin.Log.LogWarning("ShiftInto: " + e.Message); }
        }

        // one-line NWH state readback for the log
        internal static string Diag(GameObject car)
        {
            try
            {
                var h = Of(car);
                if (h == null) return "diag: no VehicleController";
                return "thr=" + Get(h.Input, "Throttle") + " clutch=" + Get(h.Input, "Clutch") + " hb=" + Get(h.Input, "Handbrake")
                     + " rpm=" + F(Get(h.Engine, "RPM") ?? Get(h.Engine, "OutputRPM")) + " trType=" + Get(h.Transmission, "transmissionType")
                     + " active=" + (Get(h.Vc, "IsActive") ?? Get(h.Vc, "Active")) + " enabled=" + ((Behaviour)h.Vc).enabled;
            }
            catch (Exception e) { return "diag: " + e.Message; }
        }

        private static string F(object o) { return o is float ? ((float)o).ToString("0") : (o == null ? "?" : o.ToString()); }

        internal static string Gear(GameObject car)
        {
            try { var h = Of(car); var g = h != null ? Get(h.Transmission, "Gear") : null; return g != null ? g.ToString() : "?"; }
            catch (Exception) { return "?"; }
        }

        internal static void SetHandbrake(GameObject car, float value)
        {
            try { var h = Of(car); if (h == null || !Set(h.Input, "Handbrake", value)) Plugin.Verbose("no input.Handbrake"); }
            catch (Exception e) { Plugin.Log.LogWarning("SetHandbrake: " + e.Message); }
        }

        internal static void SetInput(GameObject car, float throttle, float steering, float brakes)
        {
            try
            {
                var h = Of(car);
                if (h == null || h.Input == null) { Plugin.Log.LogWarning("No VehicleController.input"); return; }
                if (h.Throttle == throttle && h.Steering == steering && h.Brakes == brakes && Time.fixedTime - h.WrittenAt < InputRefresh) return;
                h.Throttle = throttle; h.Steering = steering; h.Brakes = brakes; h.WrittenAt = Time.fixedTime;
                Set(h.Input, "Throttle", throttle);
                Set(h.Input, "Steering", steering);
                Set(h.Input, "Brakes", brakes);
            }
            catch (Exception e) { Plugin.Log.LogWarning("SetInput: " + e.Message); }
        }

        // the next SetInput writes whatever it is given (after the game's INPUT FSMs had the input for a while)
        internal static void ForgetInput(GameObject car)
        {
            var h = Of(car);
            if (h != null) { h.Throttle = h.Steering = h.Brakes = float.NaN; h.WrittenAt = -1f; }
        }
    }
}

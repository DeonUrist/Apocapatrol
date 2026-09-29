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
            if (!InGame()) return;
            if (_busy || !Plugin.Pressed(Plugin.SpawnKey.Value)) return;
            StartCoroutine(Build());
        }

        private bool InGame()
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

        private IEnumerator Build()
        {
            _busy = true;
            try
            {
                Vector3 p, fwd;
                if (!PlayerPose(out p, out fwd)) { Plugin.Log.LogWarning("No player found"); yield break; }

                var body = Prefabs.Find(Plugin.Body.Value, "vehicle");
                if (body == null) { Plugin.Log.LogWarning("Body prefab not found: " + Plugin.Body.Value); yield break; }

                var pos = p + fwd * Plugin.SpawnDistance.Value + Vector3.up * 1.0f;
                var car = UnityEngine.Object.Instantiate(body, pos, Quaternion.LookRotation(fwd));
                car.SetActive(true);
                if (Plugin.RegisterWithGame.Value) { Register.Name(car, body.name); Register.Add(car, true); }
                Plugin.Log.LogInfo("Car frame " + car.name + " at " + pos);

                yield return null;   // let the frame's FSMs start (hinges, getEngine, START ...)

                int parts = 0;
                parts += AttachAll(car, new[] { "hinge_wheel_FL", "hinge_wheel_FR", "hinge_wheel_RL", "hinge_wheel_RR" }, Plugin.Wheel.Value, "wheel");
                parts += AttachAll(car, new[] { "hinge_engine" }, Plugin.Engine.Value, "engine");
                parts += AttachAll(car, new[] { "hinge_radiator" }, Plugin.Radiator.Value, "radiator");
                parts += AttachAll(car, new[] { "hinge_steeringwheel" }, Plugin.SteeringWheel.Value, "steeringwheel");
                parts += AttachAll(car, new[] { "hinge_seat_driver" }, Plugin.Seat.Value, "seat");
                Plugin.Log.LogInfo(parts + " parts attached");

                if (Plugin.FillFuel.Value) Fuel(car);
                if (Plugin.ReleaseHandbrake.Value) Handbrake(car, false);

                yield return null;
                bool hasDriver = false;
                if (!string.IsNullOrEmpty(Plugin.Driver.Value))
                {
                    if (Plugin.Driver.Value.Trim().EndsWith("_Dead", StringComparison.OrdinalIgnoreCase)) SeatDriver(car);
                    else hasDriver = SeatLiveDriver(car);
                }

                yield return new WaitForSeconds(1.5f);
                if (Plugin.VerboseLog.Value) LogHingeStates(car);

                if (Plugin.StartEngine.Value)
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
                        if (!running && Plugin.NwhStartFallback.Value)
                        {
                            Nwh.StartEngine(car);
                            yield return new WaitForSeconds(1f);
                            Plugin.Log.LogInfo("After NWH StartEngine(): running=" + Nwh.EngineRunning(car) + "  START state: " + start.ActiveStateName);
                        }
                    }
                }

                if (hasDriver) { Plugin.Log.LogInfo("Driver in place; the Crew component takes it from here"); yield break; }

                if (Plugin.DriveTestSeconds.Value > 0f)
                {
                    Plugin.Log.LogInfo("Drive test: throttle " + Plugin.DriveTestThrottle.Value + " for " + Plugin.DriveTestSeconds.Value + " s");
                    var drive = FindFsm(car, "DriveTrigger", "Drive");
                    var ctl = new InputControl(car);   // silences the game's INPUT FSMs, which write input.* from the axes every frame
                    ctl.Take();
                    float t = 0f; var rb = car.GetComponent<Rigidbody>();
                    float report = 1f;
                    while (t < Plugin.DriveTestSeconds.Value && car != null)
                    {
                        if (drive != null && drive.Fsm.Initialized && drive.ActiveStateName == "inCar")
                        { Plugin.Log.LogInfo("Drive test aborted: player got in at t=" + t.ToString("0.0")); break; }
                        // the automatic gearbox stays in neutral until told to shift into 1st (D) once
                        if (Nwh.GearIndex(car) <= 0) Nwh.ShiftInto(car, 1);
                        Nwh.SetInput(car, Plugin.DriveTestThrottle.Value, 0f, 0f);
                        t += Time.deltaTime;
                        if (t >= report) { report += 1f; Plugin.Verbose("  t=" + t.ToString("0.0") + " speed " + (rb != null ? (rb.velocity.magnitude * 3.6f).ToString("0.0") : "?") + " km/h  gear " + Nwh.Gear(car) + "  " + Nwh.Diag(car)); }
                        yield return null;
                    }
                    if (car != null)
                    {
                        Plugin.Log.LogInfo("Drive test over: " + (rb != null ? (rb.velocity.magnitude * 3.6f).ToString("0.0") : "?") + " km/h");
                        bool playerIn = drive != null && drive.Fsm.Initialized && drive.ActiveStateName == "inCar";
                        if (!playerIn) Nwh.SetInput(car, 0f, 0f, 0f);   // gas released; the car rolls out by itself
                        ctl.Release();
                    }
                }
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
            if (Plugin.RegisterWithGame.Value) { Register.Name(part, prefab.name); Register.Add(part, false); }
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
        private static void SeatDriver(GameObject car)
        {
            var prefab = Prefabs.FindAny(Plugin.Driver.Value);
            if (prefab == null) { Plugin.Log.LogWarning("Driver prefab not found: " + Plugin.Driver.Value); return; }
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null) { Plugin.Log.LogWarning("No sitPos on " + car.name); return; }

            var off = new Vector3(Plugin.DriverOffsetX.Value, Plugin.DriverOffsetY.Value, Plugin.DriverOffsetZ.Value);
            var pos = sit.position + car.transform.right * off.x + car.transform.up * off.y + car.transform.forward * off.z;
            var rot = Quaternion.LookRotation(car.transform.forward, car.transform.up);
            var drv = UnityEngine.Object.Instantiate(prefab, pos, rot);
            drv.SetActive(true);
            if (Plugin.RegisterDriver.Value) { Register.Name(drv, prefab.name); Register.Add(drv, false); }
            else drv.name = prefab.name + "(Driver)";

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
        }

        // A live enemy at the wheel with its AI off: instantiated at sitPos, the mover/AI FSMs disabled before they Start,
        // root Rigidbody kinematic and parented to the seat, collisions with the car ignored. Health/Damage/Bodypart stay
        // vanilla so it can be shot; the Crew component drives the car and reacts to its death.
        private static bool SeatLiveDriver(GameObject car)
        {
            var prefab = Prefabs.FindAny(Plugin.Driver.Value);
            if (prefab == null) { Plugin.Log.LogWarning("Driver prefab not found: " + Plugin.Driver.Value); return false; }
            var sit = FindChild(car.transform, "sitPos") ?? FindChild(car.transform, "hinge_seat_driver");
            if (sit == null) { Plugin.Log.LogWarning("No sitPos on " + car.name); return false; }

            var off = new Vector3(Plugin.DriverOffsetX.Value, Plugin.DriverOffsetY.Value, Plugin.DriverOffsetZ.Value);
            var pos = sit.position + car.transform.right * off.x + car.transform.up * off.y + car.transform.forward * off.z;
            var rot = Quaternion.LookRotation(car.transform.forward, car.transform.up);
            var drv = UnityEngine.Object.Instantiate(prefab, pos, rot);
            drv.SetActive(true);
            var crew = Crew.Attach(car, drv);
            crew.MuteAi();                                   // before the FSMs' Start
            if (Plugin.RegisterDriver.Value) { Register.Name(drv, prefab.name); Register.Add(drv, false); }
            else drv.name = prefab.name + "(Driver)";

            foreach (var a in drv.GetComponentsInChildren<Collider>(true))
                foreach (var b in car.GetComponentsInChildren<Collider>(true))
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

            var root = drv.GetComponent<Rigidbody>() ?? drv.AddComponent<Rigidbody>();
            root.isKinematic = true;
            root.interpolation = RigidbodyInterpolation.None;
            drv.transform.SetParent(sit, true);
            if (Plugin.PoseEnabled.Value) Pose.Apply(drv, sit);
            Plugin.Log.LogInfo("Live driver " + drv.name + " on " + sit.name + " at " + pos);
            return true;
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

        // =============================================================== helpers

        private static bool PlayerPose(out Vector3 p, out Vector3 fwd)
        {
            var pl = GameObject.Find("Player");
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

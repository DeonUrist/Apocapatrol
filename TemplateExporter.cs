using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocapatrol
{
    // Apocatemplater's vehicle recorder, hosted by Apocapatrol's persistent runner.
    // Uses the same TemplateFile schema as the loader; no separate plugin or hotkey by default.
    internal sealed class TemplateExporter : MonoBehaviour
    {
        private static ManualLogSource Log { get { return Plugin.Log; } }
        private static ConfigEntry<Key> _key;
        private string _toast = "";
        private float _toastUntil;

        // hinges/parts that move by themselves (suspension travel, steering, wheel spin): never recorded as "moved", their pose at
        // dump time is not the builder's intent
        private static readonly string[] Moving = { "wheel", "steering", "suspension", "handbrake", "gearlever", "pedal" };
        // doors, hood and trunk swing their hinge when opened: the hinge is never recorded (only engine / exhaust / radiator hinges can
        // be adjusted in the game anyway)
        private static readonly string[] Opening = { "door", "hood", "trunk" };
        // parts the hinge's Shake FSM wobbles while the engine runs (iTweenRotateTo on the part, SetRotation 0 when idle): their own
        // offset is never recorded. Their hinge IS: the adjust tool (AdjustHinge FSM) moves the hinge transform itself, 0.05 per press
        private static readonly string[] Shaking = { "engine", "exhaust", "radiator", "HP " };

        internal static void Configure(ConfigFile config)
        {
            _key = config.Bind("Debug", "TemplateDumpKey", Key.None, "Open template creation only while seated in a vehicle. None = unbound. Saves custom templates into PlayerTemplates after confirmation");
        }
        private readonly EditorSession _session = new EditorSession();
        private Transform _draftCar;
        private string _templateName = "", _driverName = "Scraffa", _passengerName = "Sprokka", _lootId = "", _riderName = "None";
        private Action _afterDraw;
        private bool _truck;
        private bool _bike;   // the true motorcycle (no rear, no seats behind): its templates carry no loot setting at all
        private Rect _rect;

        private void Update()
        {
            if (_session.Open)
            {
                _session.Maintain();
                if (_draftCar == null || FindCar(out _) != _draftCar) { Close(); Toast("Template canceled: you are no longer seated in that vehicle"); return; }
                if (Plugin.Pressed(Key.Escape)) { if (_ledgerDropdown.IsOpen) _ledgerDropdown.Close(); else Close(); }
                return;
            }
            if (_key == null || EditorSession.Busy || !Application.isFocused || !Plugin.Pressed(_key.Value)) return;
            var patrol = GetComponent<Patrol>(); if (patrol == null || !patrol.InGame()) return;
            string how; var car = FindCar(out how); if (car == null) return;
            if (!_session.Acquire()) return;
            _draftCar = car; _truck = PrefabName(car.name).Equals("Rustcargo", StringComparison.OrdinalIgnoreCase) && FrameChildren(car, "PhysicsLock").Any();
            _templateName = ""; _driverName = "Scraffa"; _passengerName = "None"; _riderName = "None"; _ledgerDropdown.Close();
            _bike = MotorcycleIntegration.IsBody(PrefabName(car.name));
            var profiles = _truck ? EditorStore.Data.TruckLoot : EditorStore.Data.CarLoot; _lootId = _bike ? "" : !_truck && profiles.Any(p => p.Id == "car-empty") ? "car-empty" : profiles.Count > 0 ? profiles[0].Id : "";
        }
        private void Close() { _session.Close(); _draftCar = null; _ledgerDropdown.Close(); }
        private void OnDestroy() { _session.Close(true); }
        private void LateUpdate() { _session.Maintain(); }
        private void OnGUI()
        {
            if (!_session.Open || _draftCar == null) { DrawToast(); return; }   // just saved/closed: the session lets go a frame later
            _session.Maintain(); GUI.depth = -1000;
            float scale = Mathf.Min((Screen.width - 32f) / LedgerSkin.Width, (Screen.height - 32f) / 660f);
            var old = LedgerSkin.Begin(); var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - LedgerSkin.Width * scale) / 2, (Screen.height - 660 * scale) / 2), Quaternion.identity, new Vector3(scale, scale, 1));
            _rect = new Rect(0, 0, LedgerSkin.Width, 660);
            try { GUI.Window(GetInstanceID(), _rect, DrawDraft, "", GUIStyle.none); }
            finally { GUI.skin = old; GUI.matrix = matrix; }
            _ledgerDropdown.Flush();
            if (_afterDraw != null) { var action = _afterDraw; _afterDraw = null; action(); }
            DrawToast();
        }
        private readonly LedgerDropdown _ledgerDropdown = new LedgerDropdown();
        private void DrawDraft(int id)
        {
            if (_draftCar == null) return;   // closed during this OnGUI pass (Save/Cancel run after the Layout event; Repaint still comes)
            _ledgerDropdown.Before(); bool enabled = GUI.enabled;
            LedgerSkin.Window(_rect); LedgerSkin.Header(_rect.width);
            GUI.enabled = enabled && !_ledgerDropdown.IsOpen;
            if (LedgerSkin.Button(new Rect(_rect.width - 96, 24, 70, 34), "CLOSE", gameFont: true, transparent: true)) _afterDraw = Close;
            var box = new Rect((_rect.width - 600) / 2, 104, 600, 474); LedgerSkin.Panel(box);
            float x = box.x + 22, input = x + 160, w = box.width - 204;
            LedgerSkin.Label(new Rect(x, box.y + 22, box.width - 44, 26), "Save vehicle template", LedgerSkin.DialogTitle);
            LedgerSkin.Label(new Rect(x, box.y + 53, box.width - 44, 20), (_truck ? "Storage truck" : "Vehicle") + " · " + PrefabName(_draftCar.name), LedgerSkin.Small, LedgerSkin.Muted);
            LedgerSkin.Label(new Rect(x, box.y + 88, 144, 34), "Template name");
            _templateName = GUI.TextField(new Rect(input, box.y + 88, w, 34), _templateName, LedgerSkin.Input);
            if (_templateName.Length == 0 && GUI.GetNameOfFocusedControl() == "") LedgerSkin.Label(new Rect(input + 10, box.y + 88, w - 20, 34), "Automatic name", null, LedgerSkin.Muted);
            var people = new[] { "None", "Scraffa", "Spanna", "Sprokka", "Boltjaw", "Flexa", "Lugnut", "Scrud" }.Select(n => new KeyValuePair<string, string>(n, n)).ToList();
            DraftLedgerDropdown(new Rect(input, box.y + 135, w, 34), "Driver", _driverName, people, value => _driverName = value, box.yMax - 84);
            DraftLedgerDropdown(new Rect(input, box.y + 182, w, 34), "Passenger", _passengerName, people, value => _passengerName = value, box.yMax - 84);
            var profiles = _truck ? EditorStore.Data.TruckLoot : EditorStore.Data.CarLoot;
            var profile = profiles.FirstOrDefault(p => p.Id == _lootId);
            if (_bike)
            {
                LedgerSkin.Label(new Rect(x, box.y + 229, 144, 34), "Car loot");
                LedgerSkin.Label(new Rect(input, box.y + 229, w, 34), "None - a motorcycle carries no loot", null, LedgerSkin.Muted);
            }
            else if (!_truck)
            {
                LedgerSkin.Label(new Rect(x, box.y + 229, 144, 34), "Car loot");
                var anchor = new Rect(input, box.y + 229, w, 34);
                if (LedgerSkin.Dropdown(anchor, profile != null ? profile.Name : "None")) _ledgerDropdown.Open(anchor, new[] { new KeyValuePair<string, string>("", "None") }.Concat(profiles.Select(p => new KeyValuePair<string, string>(p.Id, p.Name))).ToList(), _lootId, value => _lootId = value, box.yMax - 84);
            }
            if (!_truck && !_bike) DraftLedgerDropdown(new Rect(input, box.y + 276, w, 34), "Blastlance rider", _riderName, people, value => _riderName = value, box.yMax - 84);
            else { LedgerSkin.Label(new Rect(x, box.y + 276, 144, 34), "Blastlance rider"); LedgerSkin.Label(new Rect(input, box.y + 276, w, 34), "None - cars only", null, LedgerSkin.Muted); }
            LedgerSkin.Label(new Rect(x, box.y + 333, box.width - 44, 20), "Attached parts and adjusted hinges are recorded automatically.", LedgerSkin.Small, LedgerSkin.Muted);
            LedgerSkin.Label(new Rect(x, box.y + 350, box.width - 44, 20), "Stored in PlayerTemplates. Locked cargo items are excluded.", LedgerSkin.Small, LedgerSkin.Muted);
            LedgerSkin.Rule(x, box.yMax - 73, box.width - 44);
            if (LedgerSkin.Button(new Rect(box.xMax - 213, box.yMax - 55, 77, 34), "Cancel")) _afterDraw = Close;
            if (LedgerSkin.Button(new Rect(box.xMax - 128, box.yMax - 55, 106, 34), "Save template", primary: true))
                _afterDraw = () => { try { Dump(); Close(); } catch (Exception e) { Log.LogError("Template save failed: " + e); Toast("Template not saved: " + e.Message); } };
            LedgerSkin.Rule(6, 590, _rect.width - 12);
            GUI.enabled = enabled; _ledgerDropdown.Draw();
        }
        private void DraftLedgerDropdown(Rect r, string label, string value, List<KeyValuePair<string, string>> people, Action<string> setter, float bottom)
        {
            LedgerSkin.Label(new Rect(r.x - 160, r.y, 144, 34), label);
            if (LedgerSkin.Dropdown(r, value)) _ledgerDropdown.Open(r, people, value, setter, bottom);
        }

        private void DrawToast()
        {
            if (Time.unscaledTime > _toastUntil || _toast.Length == 0) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16, wordWrap = true };
            style.normal.textColor = Color.white;
            float w = Mathf.Min(900f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - w) / 2f, 40f, w, 90f), _toast, style);
        }

        private void Toast(string s) { _toast = s; _toastUntil = Time.unscaledTime + 8f; }

        // =============================================================== which car

        private static bool IsVehicle(Transform t)
        {
            if (t == null) return false;
            foreach (var c in t.GetComponents<Component>()) if (c != null && c.GetType().Name == "VehicleController") return true;
            return false;
        }

        private static Transform VehicleOf(Transform t)
        {
            for (var a = t; a != null; a = a.parent) if (IsVehicle(a)) return a;
            return null;
        }

        private static Transform FindCar(out string how)
        {
            how = "seated vehicle";
            var player = GameObject.Find("Player");
            return player != null ? VehicleOf(player.transform.parent) : null;
        }

        // =============================================================== dump

        private Dictionary<string, GameObject> _assets;

        private GameObject Asset(string name)
        {
            if (MotorcycleIntegration.IsBody(name)) return MotorcycleIntegration.Template();
            if (_assets == null)
            {
                _assets = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go != null && !go.scene.IsValid() && go.transform.parent == null && !_assets.ContainsKey(go.name)) _assets[go.name] = go;
            }
            GameObject r;
            return _assets.TryGetValue(name, out r) ? r : null;
        }

        private static readonly Regex CloneSuffix = new Regex(@"(\(Clone\)\d*|\s\(\d+\))+$");
        internal static string PrefabName(string instance) { return CloneSuffix.Replace(instance ?? "", "").Trim(); }

        private static bool IsPart(Transform t)
        {
            try { return VehicleAttachments.IsPart(t); } catch (Exception) { return false; }
        }

        private static bool IsMoving(string name) { return Has(name, Moving); }



        private static bool Has(string name, string[] words)
        {
            string n = (name ?? "").ToLowerInvariant();
            foreach (var m in words) if (n.Contains(m.ToLowerInvariant())) return true;
            return false;
        }

        private static string RelPath(Transform root, Transform t)
        {
            var names = new List<string>();
            for (var a = t; a != null && a != root; a = a.parent) names.Add(Seg(a));
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        // a path segment: the name, plus "#k" when k earlier siblings share that name (frames have several "collider" children)
        private static string Seg(Transform a)
        {
            var parent = a.parent;
            if (parent == null) return a.name;
            int k = 0;
            for (int i = 0; i < parent.childCount; i++) { var c = parent.GetChild(i); if (c == a) break; if (c.name == a.name) k++; }
            return k > 0 ? a.name + "#" + k : a.name;
        }

        // same as Apocapatrol's Patrol.FindPath
        private static Transform FindPath(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;
            var t = root;
            foreach (var raw in path.Split('/'))
            {
                string seg = raw; int want = 0;
                int hash = raw.LastIndexOf('#');
                if (hash > 0 && int.TryParse(raw.Substring(hash + 1), out want)) seg = raw.Substring(0, hash); else want = 0;
                Transform next = null; int seen = 0;
                for (int i = 0; i < t.childCount; i++)
                    if (t.GetChild(i).name == seg) { if (seen == want) { next = t.GetChild(i); break; } seen++; }
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        private static float[] V(Vector3 v) { return new[] { R(v.x), R(v.y), R(v.z) }; }
        private static float R(float f) { return Mathf.Round(f * 10000f) / 10000f; }

        private class Walker
        {
            internal readonly List<TemplatePart> Parts = new List<TemplatePart>();
            internal readonly List<string> Notes = new List<string>();
            internal TemplateExporter Owner;
            internal bool Offsets;
            internal Transform Player;
            internal int Moved;

            // container = the frame or a part (hinge paths are relative to it); prefabRoot = its asset, for the moved-hinge check
            internal void Walk(Transform t, Transform container, GameObject prefabRoot, int parentIdx)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (Player != null && c == Player) continue;
                    if (c.name == "PhysicsLock") continue;            // bed items: read separately
                    if (!IsPart(c)) { Walk(c, container, prefabRoot, parentIdx); continue; }

                    var hinge = c.parent;
                    var p = new TemplatePart { parent = parentIdx, hinge = RelPath(container, hinge), prefab = PrefabName(c.name) };
                    var asset = Owner.Asset(p.prefab);
                    if (asset == null) Notes.Add("no asset named \"" + p.prefab + "\" (from " + c.name + ") - check the name in the file");
                    // a real hinge (hinge_*): the hinge pose is recorded when it was moved with the adjust tool (engine / exhaust / radiator);
                    // anything else (a collider, the part itself) = an attachable placed freely (metal plates, spikes).
                    bool realHinge = hinge.name.StartsWith("hinge", StringComparison.OrdinalIgnoreCase) && !VehicleAttachments.IsFreeAttachment(c.gameObject);
                    bool opening = Has(p.hinge, Opening) || Has(c.name, Opening);
                    if (Offsets && realHinge && prefabRoot != null && !IsMoving(p.hinge) && !opening)
                    {
                        var orig = FindPath(prefabRoot.transform, p.hinge);
                        if (orig != null && ((hinge.localPosition - orig.localPosition).sqrMagnitude > 1e-6f || Quaternion.Angle(hinge.localRotation, orig.localRotation) > 0.2f))
                        {
                            p.hingePos = V(hinge.localPosition); p.hingeRot = V(hinge.localEulerAngles); Moved++;
                        }
                    }
                    // 1.0.5: the part's own pose on its hinge, for every part: the game snaps some parts (doors, hoods, trunks from another
                    // car) to a non-zero spot when they are fitted, and the build must put them there too. Not for the parts that move
                    // by themselves - wheels (spin, suspension) and the engine / exhaust / radiator (the hinge's Shake wobble).
                    bool selfMoving = Has(hinge.name, new[] { "hinge_wheel" }) || Has(c.name, Shaking) || Has(hinge.name, Shaking);
                    if (!realHinge || (Offsets && !selfMoving))
                    {
                        if (!realHinge || c.localPosition.sqrMagnitude > 1e-8f || Quaternion.Angle(c.localRotation, Quaternion.identity) > 0.01f)
                        {
                            p.pos = V(c.localPosition); p.rot = V(c.localEulerAngles);
                            if (realHinge) Moved++;
                        }
                    }
                    Parts.Add(p);
                    Walk(c, c, asset, Parts.Count - 1);
                }
            }
        }

        // items locked in the frame's PhysicsLock boxes (not the ones in a roof rack / crate part): "prefab:count;..."
        private static string BedItems(Transform car, out int count)
        {
            count = 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var pl in FrameChildren(car, "PhysicsLock"))
                Items(pl, counts, order, ref count);
            var sb = new StringBuilder();
            foreach (var n in order) { if (sb.Length > 0) sb.Append(';'); sb.Append(n).Append(':').Append(counts[n]); }
            return sb.ToString();
        }

        private static IEnumerable<Transform> FrameChildren(Transform t, string name)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (IsPart(c)) continue;
                if (c.name == name) yield return c;
                else foreach (var r in FrameChildren(c, name)) yield return r;
            }
        }

        private static void Items(Transform t, Dictionary<string, int> counts, List<string> order, ref int count)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                bool item = false;
                foreach (var f in c.GetComponents<PlayMakerFSM>()) if (f.FsmName == "LockPhysics") { item = true; break; }
                if (!item) { Items(c, counts, order, ref count); continue; }
                string n = PrefabName(c.name);
                int k;
                if (!counts.TryGetValue(n, out k)) order.Add(n);
                counts[n] = k + 1;
                count++;
            }
        }

        private void Dump()
        {
            string how;
            var car = FindCar(out how);
            if (car == null || car != _draftCar) throw new InvalidOperationException("You are no longer seated in the captured vehicle");
            _assets = null;   // a fresh asset list per dump
            string body = PrefabName(car.name);
            var bodyAsset = Asset(body);

            var player = GameObject.Find("Player");
            var w = new Walker { Owner = this, Offsets = true, Player = player != null ? player.transform : null };
            w.Walk(car, car, bodyAsset, -1);
            if (bodyAsset == null) w.Notes.Add("no frame asset named \"" + body + "\" - set \"body\" by hand");

            bool hasPassengerSeat = w.Parts.Any(p => p.parent < 0 && p.hinge.EndsWith("hinge_seat_passenger", StringComparison.Ordinal));
            bool hasDriverSeat = w.Parts.Any(p => p.parent < 0 && p.hinge.EndsWith("hinge_seat_driver", StringComparison.Ordinal));
            string kind = _truck ? "truck"
                        : string.Equals(body, "Junker", StringComparison.OrdinalIgnoreCase) ? "junker" : (string.Equals(body, "Halfbreed", StringComparison.OrdinalIgnoreCase) || MotorcycleIntegration.IsBody(body)) ? "motorcycle" : "small";

            if (w.Parts.Count == 0) throw new InvalidOperationException("No attached parts found on this vehicle");

            string name = Sanitize(_templateName);
            string dir = CarTemplates.PlayerFolder;
            Directory.CreateDirectory(dir);
            if (name.Length == 0)
            {
                name = Sanitize(body) + "_Custom";
                for (int i = 2; CarTemplate.Find(name) != null || File.Exists(Path.Combine(dir, name + ".json")); i++) name = Sanitize(body) + "_Custom_" + i;
            }

            if (CarTemplate.Find(name) != null || File.Exists(EditorStore.SafePath(dir, name + ".json"))) throw new IOException("A template with that name already exists");
            var file = new TemplateFile
            {
                name = name, body = body, kind = kind, lootPreset = _truck || _bike ? "" : _lootId,
                rams = kind == "truck" ? "Cars" : "Pedestrians",
                driver = hasDriverSeat ? (_driverName == "None" ? "" : _driverName) : "",
                passenger = hasPassengerSeat ? (_passengerName == "None" ? "" : _passengerName) : "",
                rider = _truck || _bike || _riderName == "None" ? "" : _riderName,
                parts = w.Parts.ToArray(),
                source = "Apocatemplater (" + Plugin.NAME + ") " + Plugin.VERSION + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ", from " + car.name,
            };
            string path = Path.Combine(dir, name + ".json");
            EditorStore.AtomicWrite(path, file.ToJson());
            CarTemplates.Refresh(true);

            var log = new StringBuilder();
            log.Append("Template ").Append(name).Append(" <- ").Append(how).Append(" ").Append(car.name).Append(": ").Append(w.Parts.Count).Append(" part(s)");
            if (w.Moved > 0) log.Append(", ").Append(w.Moved).Append(" moved hinge/part offset(s)");
            log.Append(" -> ").Append(path);
            Log.LogInfo(log.ToString());
            for (int i = 0; i < w.Parts.Count; i++)
            {
                var p = w.Parts[i];
                Log.LogInfo("  [" + i + "] " + (p.parent >= 0 ? "on [" + p.parent + "] " : "") + p.hinge + " = " + p.prefab
                    + (p.hingePos.Length > 0 ? "  (hinge moved)" : "") + (p.pos.Length > 0 ? "  (placed at its own pose)" : ""));
            }
            foreach (var n in w.Notes) Log.LogWarning("  " + n);
            if (!hasDriverSeat) Log.LogWarning("  no driver seat on the car: the template has no driver (a raider car needs one to drive)");

            Toast("Saved template " + name + " (" + body + ", " + w.Parts.Count + " parts" + ")\n" + path
                + (w.Notes.Count > 0 ? "\n" + w.Notes.Count + " warning(s) - see the BepInEx log" : "")
                + "\nOpen the Apocapatrol spawner menu to build it.");
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").Trim()) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }

}

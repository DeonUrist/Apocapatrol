using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Apocapatrol
{
    public class PatrolSaveData
    {
        public int version = 1;
        public string saveFile;
        public int worldSeed;
        public PatrolCarData[] cars = new PatrolCarData[0];
        public float convoyCooldown = -1f;    // seconds until the next automatic convoy roll (schema 3), -1 = unknown
    }

    public class PatrolCarData
    {
        public string carName, bodyPrefab, ramTargets;
        public float positionX, positionY, positionZ;
        public string driverPrefab, driverPhase;
        public bool driverPresent;
        public float driverHealth = -1f, seatedSeconds;
        public string passengerPrefab;
        public bool passengerPresent;
        public float passengerHealth = -1f;
        public bool playerEntered;           // schema 4: the player has sat in it (never cleaned up)
        public float farSeconds;             // schema 4: cleanup timer
        public bool exploded;                // schema 5: self-destructed; a dead chassis (removed beyond 1000 m)
        public string cargoKey = "";         // schema 6: the loot type it was built with ("" = none / unknown) - picks the container texture
    }

    internal sealed class PatrolMarker : MonoBehaviour
    {
        private string _bodyPrefab, _driverPrefab, _passengerPrefab;
        private GameObject _driver, _passenger;
        private RamTargets _rams = RamTargets.Pedestrians;

        // every live marker: the cleanup and the save walk this list instead of FindObjectsOfType (a scan of every MonoBehaviour)
        internal static readonly List<PatrolMarker> All = new List<PatrolMarker>();
        private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        private void OnDisable() { All.Remove(this); }
        private void OnDestroy() { All.Remove(this); }

        internal RamTargets Rams { get { return _rams; } }
        internal bool PlayerEntered;          // the player has sat in this car: it is theirs, the cleanup never removes it
        internal float FarSeconds;            // how long it has been beyond the cleanup distance
        internal bool Exploded;               // self-destructed: a dead chassis, removed beyond Explode.RemoveDistance
        internal string CargoKey = "";        // loot type at build (Food, Water, ...; "" = none): the container texture, kept across saves

        internal static PatrolMarker Attach(GameObject car, string bodyPrefab, string driverPrefab, GameObject driver,
            string passengerPrefab, GameObject passenger, RamTargets rams)
        {
            var marker = car.GetComponent<PatrolMarker>() ?? car.AddComponent<PatrolMarker>();
            if (car.GetComponent<Ram.Sensor>() == null) car.AddComponent<Ram.Sensor>();
            marker._rams = rams;
            marker._bodyPrefab = bodyPrefab ?? "";
            marker._driverPrefab = driverPrefab ?? "";
            marker._passengerPrefab = passengerPrefab ?? "";
            marker._driver = driver;
            marker._passenger = passenger;
            return marker;
        }

        internal GameObject Passenger { get { return _passenger; } }
        internal string PassengerPrefab { get { return _passengerPrefab; } }

        // the passenger's Health FSM and its Health variable, resolved once per passenger (the Crew asks every frame after the
        // driver's death; this used to allocate a GetComponents array + a LINQ search per frame)
        private PlayMakerFSM _passengerHealth; private GameObject _passengerHealthOf;
        internal FsmFloat PassengerHealthVar;
        internal PlayMakerFSM PassengerHealthFsm
        {
            get
            {
                if (_passenger == null) return null;
                if (_passengerHealthOf != _passenger)
                {
                    _passengerHealthOf = _passenger; PassengerHealthVar = null;
                    _passengerHealth = _passenger.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Health");
                }
                return _passengerHealth;
            }
        }

        // the passenger became the driver: the car is saved with it at the wheel and an empty passenger seat
        internal void Promote(GameObject newDriver)
        {
            _driverPrefab = _passengerPrefab;
            _driver = newDriver;
            _passenger = null;
            _passengerHealth = null; _passengerHealthOf = null; PassengerHealthVar = null;
        }

        // the passenger left the car (it lives on as an ordinary registered mob, saved by the game itself)
        internal void PassengerLeft() { _passenger = null; _passengerHealth = null; _passengerHealthOf = null; PassengerHealthVar = null; }
        internal string DriverPrefab { get { return _driverPrefab; } }
        internal string BodyPrefab { get { return _bodyPrefab; } }
        internal void DriverLeft() { _driver = null; }

        internal PatrolCarData Snapshot()
        {
            var crew = GetComponent<Crew>();
            float driverHealth = Patrol.GetHealth(_driver);
            float passengerHealth = Patrol.GetHealth(_passenger);
            bool driverPresent = _driver != null && (driverHealth < 0f || driverHealth > 0f);
            bool passengerPresent = _passenger != null && (passengerHealth < 0f || passengerHealth > 0f);
            CrewPhase phase = crew != null ? crew.Phase : CrewPhase.Released;
            // Health can reach zero one frame before Crew observes destruction. Never persist that transient as
            // an alive autonomous driver with no occupant.
            if (!driverPresent && (phase == CrewPhase.Waiting || phase == CrewPhase.Driving)) phase = CrewPhase.Released;
            var p = transform.position;
            return new PatrolCarData
            {
                carName = gameObject.name,
                bodyPrefab = _bodyPrefab,
                ramTargets = _rams.ToString(),
                positionX = p.x, positionY = p.y, positionZ = p.z,
                driverPrefab = _driverPrefab,
                driverPresent = driverPresent,
                driverHealth = driverHealth,
                driverPhase = phase.ToString(),
                seatedSeconds = crew != null ? crew.SeatedSeconds : 0f,
                passengerPrefab = _passengerPrefab,
                passengerPresent = passengerPresent,
                passengerHealth = passengerHealth,
                playerEntered = PlayerEntered,
                farSeconds = FarSeconds,
                exploded = Exploded,
                cargoKey = CargoKey ?? ""
            };
        }
    }

    internal static class PatrolPersistence
    {
        private const int SchemaVersion = 6;   // 1 = no ramTargets (read as Pedestrians), 2 = no convoy cooldown, 3 = no cleanup fields, 4 = no exploded flag, 5 = no cargo key
        private const int Magic = 0x434F5041; // "APOC" in little-endian; rejects unrelated/corrupt files.
        private const int MaxCarsPerSave = 1024;
        private static PlayMakerFSM _saveLoad, _newGoSave;
        private static string _lastState, _loadSlot;
        private static float _nextScan;
        private static bool _restoreRunning;

        internal static void Tick(MonoBehaviour runner)
        {
            if (Time.unscaledTime >= _nextScan && (!Alive(_saveLoad) || !Alive(_newGoSave)))
            {
                _nextScan = Time.unscaledTime + 1f;
                var saveLoadGo = GameObject.Find("SaveLoadGame");
                if (saveLoadGo != null)
                    _saveLoad = saveLoadGo.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "SaveLoadGame");
                var newGo = GameObject.Find("NewGO_ArrayList");
                if (newGo != null)
                    _newGoSave = newGo.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Save_NewGO_ArrayList");
            }
            if (!Alive(_saveLoad) || !_saveLoad.Fsm.Initialized) return;

            string state = _saveLoad.ActiveStateName ?? "";
            if (state != _lastState)
            {
                if (state == "SaveGame") Save(CurrentSaveSlot(), CurrentSeed());
                if (state == "LoadGame") _loadSlot = CurrentSaveSlot();
                if (state == "LoadGame" || state == "setSeed") Convoy.SetCooldown(-1f);   // new game / load: the clock is re-rolled (or restored from the sidecar)
                if (state == "isPlay" && !string.IsNullOrEmpty(_loadSlot) && !_restoreRunning)
                {
                    string slot = _loadSlot;
                    _loadSlot = null;
                    runner.StartCoroutine(RestoreWhenReady(slot, CurrentSeed()));
                }
                _lastState = state;
            }
        }

        internal static void ResetForScene()
        {
            _saveLoad = null; _newGoSave = null; _lastState = null; _loadSlot = null;
            _restoreRunning = false; _nextScan = 0f;
        }

        private static string CurrentSaveSlot()
        {
            // ES3's default path is changed by the selected save/load-slot button and is authoritative in every
            // phase. The two FSM variables are fallbacks; SaveLoadGame.SaveFile remains at SaveGame1 on some loads.
            try
            {
                string path = ES3Settings.defaultSettings.path;
                if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
            }
            catch (Exception) { }
            string slot = Alive(_newGoSave) ? FsmString(_newGoSave, "SaveFile") : null;
            return string.IsNullOrEmpty(slot) ? FsmString(_saveLoad, "SaveFile") : slot;
        }

        private static int CurrentSeed()
        {
            var seed = Alive(_saveLoad) ? _saveLoad.FsmVariables.GetFsmInt("seed") : null;
            return seed != null ? seed.Value : 0;
        }

        private static string FsmString(PlayMakerFSM fsm, string name)
        {
            var value = fsm != null ? fsm.FsmVariables.GetFsmString(name) : null;
            return value != null ? value.Value : null;
        }

        private static void Save(string slot, int seed)
        {
            string path;
            if (!TryPath(slot, out path)) { Plugin.Log.LogWarning("Persistence: invalid save slot " + slot); return; }
            try
            {
                var data = new PatrolSaveData { version = SchemaVersion, saveFile = Path.GetFileName(slot), worldSeed = seed, convoyCooldown = Convoy.CurrentCooldown() };
                var cars = new List<PatrolCarData>();
                foreach (var marker in PatrolMarker.All)
                    if (marker != null) cars.Add(marker.Snapshot());
                data.cars = cars.ToArray();
                AtomicWrite(path, data);
                Plugin.Verbose("Persistence: saved " + data.cars.Length + " patrol car(s) for " + data.saveFile);
            }
            catch (Exception e) { Plugin.Log.LogError("Persistence save failed: " + e); }
        }

        private static IEnumerator RestoreWhenReady(string slot, int seed)
        {
            _restoreRunning = true;
            PatrolSaveData data = Load(slot);
            if (data == null) { _restoreRunning = false; yield break; }
            if (data.version < 1 || data.version > SchemaVersion || data.worldSeed != seed
                || !string.Equals(data.saveFile, Path.GetFileName(slot), StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning("Persistence: sidecar version/slot/seed does not match " + slot + "; skipped");
                _restoreRunning = false; yield break;
            }

            if (data.convoyCooldown >= 0f) Convoy.SetCooldown(data.convoyCooldown);
            var savedCars = data.cars ?? new PatrolCarData[0];
            var pending = new List<PatrolCarData>(savedCars);
            float until = Time.realtimeSinceStartup + 15f;
            var roots = new List<GameObject>();
            while (pending.Count > 0 && Time.realtimeSinceStartup < until)
            {
                // cars are root objects: one pass over the scene roots per round, not a scan of every Transform per car
                roots.Clear();
                for (int s = 0; s < SceneManager.sceneCount; s++) { var sc = SceneManager.GetSceneAt(s); if (sc.isLoaded) roots.AddRange(sc.GetRootGameObjects()); }
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var car = FindCar(pending[i], roots);
                    if (car == null) continue;
                    try
                    {
                        RestoreCar(car, pending[i]);
                        pending.RemoveAt(i);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Persistence: failed to restore " + pending[i].carName + ": " + e);
                        pending.RemoveAt(i);
                    }
                }
                if (pending.Count > 0) yield return new WaitForSecondsRealtime(0.5f);
            }
            Plugin.Verbose("Persistence: restored " + (savedCars.Length - pending.Count) + "/" + savedCars.Length
                + " patrol car(s) from " + slot);
            foreach (var missing in pending) Plugin.Log.LogWarning("Persistence: saved patrol car not found: " + missing.carName);
            _restoreRunning = false;
        }

        private static GameObject FindCar(PatrolCarData data, List<GameObject> roots)
        {
            foreach (var go in roots)
            {
                if (go == null || go.name != data.carName) continue;
                var t = go.transform;
                if (!string.IsNullOrEmpty(data.bodyPrefab)
                    && !t.name.StartsWith(data.bodyPrefab + "(Clone)", StringComparison.OrdinalIgnoreCase)) continue;
                var saved = new Vector3(data.positionX, data.positionY, data.positionZ);
                if ((t.position - saved).sqrMagnitude > 625f) continue;
                bool vehicle = t.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "VehicleController");
                if (vehicle) return t.gameObject;
            }
            return null;
        }

        private static void RestoreCar(GameObject car, PatrolCarData data)
        {
            if (car.GetComponent<PatrolMarker>() != null) return;
            Paint.Apply(car, data.bodyPrefab);   // the game rebuilt the frame from its prefab: the mod's paint again
            Paint.ApplyCargo(car, data.cargoKey);
            GameObject driver = null, passenger = null;
            CrewPhase phase;
            if (!Enum.TryParse(data.driverPhase, out phase)) phase = CrewPhase.Waiting;
            if (data.exploded)
            {
                // a dead chassis: nobody inside, nothing works; its FSMs restarted with the scene, so it is deadened again
                var dead = PatrolMarker.Attach(car, data.bodyPrefab, data.driverPrefab, null, data.passengerPrefab, null, CarTemplate.ParseRams(data.ramTargets));
                dead.PlayerEntered = data.playerEntered; dead.FarSeconds = data.farSeconds; dead.Exploded = true; dead.CargoKey = data.cargoKey ?? "";
                Explode.RestoreDead(car, data.bodyPrefab);
                Plugin.Verbose("Persistence: dead chassis restored: " + car.name);
                return;
            }

            if (data.driverPresent && !string.IsNullOrEmpty(data.driverPrefab))
            {
                if (data.driverPrefab.EndsWith("_Dead", StringComparison.OrdinalIgnoreCase))
                    driver = Patrol.SeatDriver(car, data.driverPrefab);
                else
                    driver = Patrol.SeatLiveDriver(car, data.driverPrefab, data.driverHealth, phase, data.seatedSeconds);
            }
            else if (!string.IsNullOrEmpty(data.driverPrefab) && phase != CrewPhase.Waiting)
                Crew.Restore(car, null, phase, data.seatedSeconds);

            if (data.passengerPresent && !string.IsNullOrEmpty(data.passengerPrefab))
                passenger = Patrol.SeatPassenger(car, data.passengerPrefab, data.passengerHealth);

            var mk = PatrolMarker.Attach(car, data.bodyPrefab, data.driverPrefab, driver, data.passengerPrefab, passenger, CarTemplate.ParseRams(data.ramTargets));
            mk.PlayerEntered = data.playerEntered;
            mk.FarSeconds = data.farSeconds;
            mk.CargoKey = data.cargoKey ?? "";
            var crew = car.GetComponent<Crew>();
            if (crew != null && phase != CrewPhase.Released && phase != CrewPhase.DeadRolling) crew.Revive();   // handbrake off + ignition, like a fresh build
            Plugin.Verbose("Persistence: crew restored on " + car.name + " (" + phase + ")");
        }

        private static PatrolSaveData Load(string slot)
        {
            string path;
            if (!TryPath(slot, out path)) return null;
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var data = Read(candidate);
                    if (data != null) return data;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Persistence: cannot read " + candidate + ": " + e.Message); }
            }
            Plugin.Verbose("Persistence: no patrol sidecar for " + slot);
            return null;
        }

        private static bool TryPath(string slot, out string path)
        {
            path = null;
            string name = Path.GetFileName(slot ?? "");
            if (string.IsNullOrEmpty(name) || !name.StartsWith("SaveGame", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".es3", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '.')) return false;
            path = Path.Combine(Paths.ConfigPath, "Apocapatrol", "Saves", name + ".bin");
            return true;
        }

        private static void AtomicWrite(string path, PatrolSaveData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp", backup = path + ".bak";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(data.version);
                WriteString(writer, data.saveFile);
                writer.Write(data.worldSeed);
                var cars = data.cars ?? new PatrolCarData[0];
                writer.Write(cars.Length);
                foreach (var car in cars) WriteCar(writer, car);
                writer.Write(data.convoyCooldown);
                writer.Flush();
                stream.Flush();
            }
            if (File.Exists(path)) File.Replace(temp, path, backup);
            else File.Move(temp, path);
        }

        private static PatrolSaveData Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("wrong file header");
                int version = reader.ReadInt32();
                string slot = reader.ReadString();
                int seed = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (count < 0 || count > MaxCarsPerSave) throw new InvalidDataException("invalid car count " + count);
                var cars = new PatrolCarData[count];
                for (int i = 0; i < count; i++) cars[i] = ReadCar(reader, version);
                float cooldown = version >= 3 ? reader.ReadSingle() : -1f;
                if (stream.Position != stream.Length) Plugin.Verbose("Persistence: sidecar has trailing data: " + path);
                return new PatrolSaveData { version = version, saveFile = slot, worldSeed = seed, cars = cars, convoyCooldown = cooldown };
            }
        }

        private static void WriteCar(BinaryWriter writer, PatrolCarData car)
        {
            WriteString(writer, car.carName); WriteString(writer, car.bodyPrefab);
            writer.Write(car.positionX); writer.Write(car.positionY); writer.Write(car.positionZ);
            WriteString(writer, car.driverPrefab); WriteString(writer, car.driverPhase);
            writer.Write(car.driverPresent); writer.Write(car.driverHealth); writer.Write(car.seatedSeconds);
            WriteString(writer, car.passengerPrefab); writer.Write(car.passengerPresent); writer.Write(car.passengerHealth);
            WriteString(writer, car.ramTargets ?? "Pedestrians");
            writer.Write(car.playerEntered); writer.Write(car.farSeconds);
            writer.Write(car.exploded);
            WriteString(writer, car.cargoKey);
        }

        private static PatrolCarData ReadCar(BinaryReader reader, int version)
        {
            var car = new PatrolCarData
            {
                carName = ReadString(reader), bodyPrefab = ReadString(reader),
                positionX = reader.ReadSingle(), positionY = reader.ReadSingle(), positionZ = reader.ReadSingle(),
                driverPrefab = ReadString(reader), driverPhase = ReadString(reader),
                driverPresent = reader.ReadBoolean(), driverHealth = reader.ReadSingle(), seatedSeconds = reader.ReadSingle(),
                passengerPrefab = ReadString(reader), passengerPresent = reader.ReadBoolean(), passengerHealth = reader.ReadSingle()
            };
            car.ramTargets = version >= 2 ? ReadString(reader) : "Pedestrians";
            if (version >= 4) { car.playerEntered = reader.ReadBoolean(); car.farSeconds = reader.ReadSingle(); }
            else if (car.driverPhase == "Released") car.playerEntered = true;   // older save: an empty raider car may be one the player drove - keep it
            if (version >= 5) car.exploded = reader.ReadBoolean();
            if (version >= 6) car.cargoKey = ReadString(reader);
            return car;
        }

        private static void WriteString(BinaryWriter writer, string value) { writer.Write(value ?? ""); }

        private static string ReadString(BinaryReader reader)
        {
            string value = reader.ReadString();
            if (value.Length > 4096) throw new InvalidDataException("string field is too long");
            return value;
        }

        private static bool Alive(PlayMakerFSM fsm) { return fsm != null && fsm.gameObject != null; }
    }
}

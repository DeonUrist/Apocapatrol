using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    public class PatrolSaveData
    {
        public int version = 1;
        public string saveFile;
        public int worldSeed;
        public PatrolCarData[] cars = new PatrolCarData[0];
    }

    public class PatrolCarData
    {
        public string carName, bodyPrefab;
        public float positionX, positionY, positionZ;
        public string driverPrefab, driverPhase;
        public bool driverPresent;
        public float driverHealth = -1f, seatedSeconds;
        public string passengerPrefab;
        public bool passengerPresent;
        public float passengerHealth = -1f;
    }

    internal sealed class PatrolMarker : MonoBehaviour
    {
        private string _bodyPrefab, _driverPrefab, _passengerPrefab;
        private GameObject _driver, _passenger;

        internal static PatrolMarker Attach(GameObject car, string bodyPrefab, string driverPrefab, GameObject driver,
            string passengerPrefab, GameObject passenger)
        {
            var marker = car.GetComponent<PatrolMarker>() ?? car.AddComponent<PatrolMarker>();
            marker._bodyPrefab = bodyPrefab ?? "";
            marker._driverPrefab = driverPrefab ?? "";
            marker._passengerPrefab = passengerPrefab ?? "";
            marker._driver = driver;
            marker._passenger = passenger;
            return marker;
        }

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
                positionX = p.x, positionY = p.y, positionZ = p.z,
                driverPrefab = _driverPrefab,
                driverPresent = driverPresent,
                driverHealth = driverHealth,
                driverPhase = phase.ToString(),
                seatedSeconds = crew != null ? crew.SeatedSeconds : 0f,
                passengerPrefab = _passengerPrefab,
                passengerPresent = passengerPresent,
                passengerHealth = passengerHealth
            };
        }
    }

    internal static class PatrolPersistence
    {
        private const int SchemaVersion = 1;
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
                var data = new PatrolSaveData { version = SchemaVersion, saveFile = Path.GetFileName(slot), worldSeed = seed };
                var cars = new List<PatrolCarData>();
                foreach (var marker in UnityEngine.Object.FindObjectsOfType<PatrolMarker>())
                    if (marker != null) cars.Add(marker.Snapshot());
                data.cars = cars.ToArray();
                AtomicWrite(path, data);
                Plugin.Log.LogInfo("Persistence: saved " + data.cars.Length + " patrol car(s) for " + data.saveFile);
            }
            catch (Exception e) { Plugin.Log.LogError("Persistence save failed: " + e); }
        }

        private static IEnumerator RestoreWhenReady(string slot, int seed)
        {
            _restoreRunning = true;
            PatrolSaveData data = Load(slot);
            if (data == null) { _restoreRunning = false; yield break; }
            if (data.version != SchemaVersion || data.worldSeed != seed
                || !string.Equals(data.saveFile, Path.GetFileName(slot), StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning("Persistence: sidecar version/slot/seed does not match " + slot + "; skipped");
                _restoreRunning = false; yield break;
            }

            var savedCars = data.cars ?? new PatrolCarData[0];
            var pending = new List<PatrolCarData>(savedCars);
            float until = Time.realtimeSinceStartup + 15f;
            while (pending.Count > 0 && Time.realtimeSinceStartup < until)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var car = FindCar(pending[i]);
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
            Plugin.Log.LogInfo("Persistence: restored " + (savedCars.Length - pending.Count) + "/" + savedCars.Length
                + " patrol car(s) from " + slot);
            foreach (var missing in pending) Plugin.Log.LogWarning("Persistence: saved patrol car not found: " + missing.carName);
            _restoreRunning = false;
        }

        private static GameObject FindCar(PatrolCarData data)
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t.parent != null || t.name != data.carName) continue;
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
            GameObject driver = null, passenger = null;
            CrewPhase phase;
            if (!Enum.TryParse(data.driverPhase, out phase)) phase = CrewPhase.Waiting;

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

            PatrolMarker.Attach(car, data.bodyPrefab, data.driverPrefab, driver, data.passengerPrefab, passenger);
            Plugin.Log.LogInfo("Persistence: crew restored on " + car.name + " (" + phase + ")");
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
                for (int i = 0; i < count; i++) cars[i] = ReadCar(reader);
                if (stream.Position != stream.Length) Plugin.Verbose("Persistence: sidecar has trailing data: " + path);
                return new PatrolSaveData { version = version, saveFile = slot, worldSeed = seed, cars = cars };
            }
        }

        private static void WriteCar(BinaryWriter writer, PatrolCarData car)
        {
            WriteString(writer, car.carName); WriteString(writer, car.bodyPrefab);
            writer.Write(car.positionX); writer.Write(car.positionY); writer.Write(car.positionZ);
            WriteString(writer, car.driverPrefab); WriteString(writer, car.driverPhase);
            writer.Write(car.driverPresent); writer.Write(car.driverHealth); writer.Write(car.seatedSeconds);
            WriteString(writer, car.passengerPrefab); writer.Write(car.passengerPresent); writer.Write(car.passengerHealth);
        }

        private static PatrolCarData ReadCar(BinaryReader reader)
        {
            return new PatrolCarData
            {
                carName = ReadString(reader), bodyPrefab = ReadString(reader),
                positionX = reader.ReadSingle(), positionY = reader.ReadSingle(), positionZ = reader.ReadSingle(),
                driverPrefab = ReadString(reader), driverPhase = ReadString(reader),
                driverPresent = reader.ReadBoolean(), driverHealth = reader.ReadSingle(), seatedSeconds = reader.ReadSingle(),
                passengerPrefab = ReadString(reader), passengerPresent = reader.ReadBoolean(), passengerHealth = reader.ReadSingle()
            };
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // The convoy / enemy-car spawner: heat from the game's Distance Travelled and boss kills, a cooldown clock, the spawn-type rolls,
    // the exact vehicle roster and allowed cargo loaded from the editor,
    // the spot ahead of (or behind) the player, and the staggered builds. Spawned cars are ordinary patrol cars: the Crew drives
    // off as soon as the engine runs and the Pilot hunts the player.

    internal class Convoy : MonoBehaviour
    {
        private static Convoy _inst;
        private float _cooldown = -1f;          // seconds until the next automatic roll; < 0 = not rolled yet
        private PlayMakerFSM _distanceFsm;
        private float _nextHeatScan;
        private float _km, _heat;
        private int _bosses;
        private Patrol _patrol;

        private void Awake() { _inst = this; }

        internal static void ResetForScene()
        {
            if (_inst == null) return;
            _inst._cooldown = -1f; LastGroup = "";
            _inst._distanceFsm = null;
            _inst._km = _inst._heat = 0f;
            _inst._bosses = 0;
        }

        internal static float CurrentCooldown() { return _inst != null ? _inst._cooldown : -1f; }

        // [Debug] AiOverlay with no raider car driving: the real time until the next automatic roll
        internal static string OverlayLine()
        {
            if (_inst == null) return "Raiders: spawner not running";
            if (!Plugin.ConvoyEnabled.Value || Plugin.MaxConvoyCooldown.Value <= 0f) return "Raiders: automatic spawns off";
            float s = _inst._cooldown;
            string clock = s < 0f ? "not rolled yet" : "next roll in " + ((int)(s / 60f)).ToString("0") + ":" + ((int)(s % 60f)).ToString("00");
            return "Raiders: " + clock + "   (heat " + (_inst._heat * 100f).ToString("0") + " %, " + _inst._km.ToString("0.0") + " km, bosses " + _inst._bosses + ")";
        }
        internal static void SetCooldown(float seconds)
        {
            if (_inst == null) return;
            _inst._cooldown = seconds;
            if (seconds >= 0f) Plugin.Verbose("Convoy: clock restored, next roll in " + (seconds / 60f).ToString("0.0") + " min");
        }

        internal float Heat { get { return _heat; } }
        internal float Km { get { return _km; } }
        internal int Bosses { get { return _bosses; } }

        private void Update()
        {
            try { Tick(); }
            catch (Exception e) { Plugin.Log.LogError("Convoy: " + e); _cooldown = 60f; }
        }

        private void Tick()
        {
            if (_patrol == null) _patrol = GetComponent<Patrol>();
            if (_patrol == null || !_patrol.InGame()) return;
            if (Time.unscaledTime >= _nextHeatScan) { _nextHeatScan = Time.unscaledTime + 5f; UpdateHeat(); }
            if (Plugin.MaxConvoyCooldown.Value <= 0f) return;
            if (_cooldown < 0f) { ResetCooldown("start"); return; }
            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;
            // 2.5.3: the clock runs whether or not the spawner is enabled, and is saved either way - switching the mod off, saving and
            // loading cannot postpone a raid. Off: the raid that was due simply passes, the next one is rolled
            if (!Plugin.ConvoyEnabled.Value) { ResetCooldown("spawner off, raid skipped"); return; }
            Auto();
        }

        // ------------------------------------------------------------ heat

        // Heat = 25 % per HeatIntervalKm of the game's Distance Travelled (__GameManager__ [DistanceTravelled].distance, km from the
        // starting area), capped at MaxHeat. Boss kills = the game's Boss_* global flags.
        private void UpdateHeat()
        {
            if (_distanceFsm == null || _distanceFsm.gameObject == null)
            {
                _distanceFsm = null;
                var gm = GameObject.Find("__GameManager__");
                if (gm != null) foreach (var f in gm.GetComponents<PlayMakerFSM>()) if (f.FsmName == "DistanceTravelled") { _distanceFsm = f; break; }
            }
            float km = -1f;
            if (_distanceFsm != null && _distanceFsm.Fsm.Initialized)
            {
                var v = _distanceFsm.FsmVariables.GetFsmFloat("distance");
                if (v != null) km = v.Value;
            }
            if (km < 0f)
            {
                var pl = PlayerRef.Player; var st = GameObject.Find("Starter_Area");
                if (pl != null && st != null) km = Vector3.Distance(pl.position, st.transform.position) / 1000f;
            }
            if (km < 0f) return;
            _km = km;
            _bosses = BossKills();
            _heat = HeatFor(km);
        }

        // [General] Fury Road: every group's minimum distance / boss kills count as met
        private float RollKm { get { return Plugin.FuryRoad.Value ? 1e6f : _km; } }
        private int RollBosses { get { return Plugin.FuryRoad.Value ? 999 : _bosses; } }

        internal static float HeatFor(float km) { return Mathf.Clamp(km / Mathf.Max(1f, Plugin.HeatIntervalKm.Value) * 0.25f, 0f, Mathf.Max(0f, Plugin.MaxHeat.Value)); }

        internal static int BossKills()
        {
            int n = 0;
            try
            {
                foreach (var b in FsmVariables.GlobalVariables.BoolVariables)
                    if (b != null && b.Name != null && b.Name.StartsWith("Boss_") && b.Value) n++;
            }
            catch (Exception) { }
            return n;
        }

        // ------------------------------------------------------------ clock

        // Cooldown = random Min..Max minutes, a little shorter at high heat (x 1 / (0.75 + 0.25 x heat): heat 3 = two thirds).
        private void ResetCooldown(string why)
        {
            float a = Mathf.Max(0f, Plugin.MinConvoyCooldown.Value), b = Mathf.Max(a, Plugin.MaxConvoyCooldown.Value);
            float factor = 1f / (0.75f + 0.25f * Mathf.Max(0f, _heat));
            _cooldown = UnityEngine.Random.Range(a, b) * 60f * factor;
            Plugin.Verbose("Convoy: next roll in " + (_cooldown / 60f).ToString("0.0") + " min (" + why + ", heat " + (_heat * 100f).ToString("0") + " %)");
        }

        private void Auto()
        {
            // an autosave is due within the next half minute (or running): spawn after it, so the group is complete when it is saved
            if (AutosaveSoon(30f)) { _cooldown = 45f; Plugin.Verbose("Convoy: spawn postponed 45 s, Apocasaver autosave due"); return; }
            if (_patrol != null && _patrol.SaveBusy()) { _cooldown = 10f; return; }
            bool convoy;
            var group = EditorStore.Roll(RollKm, RollBosses, Plugin.PatrolSpawnChancePercent.Value, () => UnityEngine.Random.value, out convoy, LastGroup);
            if (group == null) { ResetCooldown("no eligible groups"); return; }
            if (!ClearOldGroups()) return;
            if (!Launch(group, convoy, false)) { _cooldown = 60f; return; }
            LastGroup = group.Id;
            ResetCooldown("after " + group.Name);
        }

        // Before an automatic spawn: raider cars that still have a live crew (and that the player never sat in) are an earlier group.
        // All of them 300 m or more away -> removed with their crew, parts and cargo, then the new group comes. Any of them closer ->
        // no spawn now, and the next roll comes sooner: half the usual cooldown, weighted toward the short end.
        private const float OldGroupDistance = 300f;
        private bool ClearOldGroups()
        {
            var player = PlayerRef.Player;
            if (player == null) return true;
            var old = new List<PatrolMarker>();
            float nearest = float.MaxValue;
            foreach (var m in PatrolMarker.All.ToArray())
            {
                if (m == null || m.Exploded || m.PlayerEntered) continue;
                var crew = m.GetComponent<Crew>();
                if (crew == null || !crew.CrewAlive) continue;
                old.Add(m);
                nearest = Mathf.Min(nearest, Vector3.Distance(m.transform.position, player.position));
            }
            if (old.Count == 0) return true;
            if (nearest < OldGroupDistance)
            {
                ResetCooldownRetry(old.Count + " live raider car(s), nearest " + nearest.ToString("0") + " m");
                return false;
            }
            foreach (var m in old) Cleanup.Remove(m, "an older group, " + OldGroupDistance.ToString("0") + "+ m away, before a new spawn");
            Plugin.Verbose("Convoy: removed " + old.Count + " older raider car(s) before the new spawn (nearest " + nearest.ToString("0") + " m)");
            return true;
        }

        // Apocasaver (optional, by reflection): true while it autosaves, or when its next autosave is due within `within` s. An autosave
        // that is overdue (it waits for the player to be on foot) does not block spawns - the builds themselves wait out a running save.
        private static bool _asResolved;
        private static FieldInfo _asCurrent, _asLast, _asArmed;
        private static PropertyInfo _asSaving;
        private static BepInEx.Configuration.ConfigEntry<float> _asInterval;
        private static BepInEx.Configuration.ConfigEntry<bool> _asEnabled;
        internal static bool AutosaveSoon(float within)
        {
            try
            {
                if (!_asResolved)
                {
                    _asResolved = true;
                    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Apocasaver");
                    var plugin = asm != null ? asm.GetType("Apocasaver.Plugin") : null;
                    var runner = asm != null ? asm.GetType("Apocasaver.Runner") : null;
                    if (plugin == null || runner == null) return false;
                    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    _asCurrent = plugin.GetField("Current", S);
                    var iv = plugin.GetField("IntervalMinutes", S); _asInterval = iv != null ? iv.GetValue(null) as BepInEx.Configuration.ConfigEntry<float> : null;
                    var en = plugin.GetField("Enabled", S); _asEnabled = en != null ? en.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool> : null;
                    _asLast = runner.GetField("_lastSave", I); _asArmed = runner.GetField("_armed", I); _asSaving = runner.GetProperty("IsAutosaving", I);
                    Plugin.Verbose("Convoy: Apocasaver found, spawns wait for its autosaves" + (_asCurrent != null && _asLast != null && _asInterval != null ? "" : " (timer not readable)"));
                }
                if (_asCurrent == null) return false;
                var r = _asCurrent.GetValue(null) as UnityEngine.Object;
                if (r == null) return false;
                if (_asSaving != null && _asSaving.GetValue(r, null) is bool && (bool)_asSaving.GetValue(r, null)) return true;
                if (_asLast == null || _asInterval == null || (_asEnabled != null && !_asEnabled.Value)) return false;
                if (_asArmed != null && _asArmed.GetValue(r) is bool && !(bool)_asArmed.GetValue(r)) return false;
                float remaining = _asInterval.Value * 60f - (Time.realtimeSinceStartup - (float)_asLast.GetValue(r));
                return remaining > -3f && remaining < within;
            }
            catch (Exception e) { Plugin.Verbose("Convoy: Apocasaver timer: " + e.Message); _asCurrent = null; return false; }
        }

        // half the usual Min..Max cooldown, the roll squared so short waits are likelier
        private void ResetCooldownRetry(string why)
        {
            float a = Mathf.Max(0f, Plugin.MinConvoyCooldown.Value), b = Mathf.Max(a, Plugin.MaxConvoyCooldown.Value);
            float factor = 1f / (0.75f + 0.25f * Mathf.Max(0f, _heat));
            float u = UnityEngine.Random.value;
            _cooldown = (a + (b - a) * u * u) * 60f * 0.5f * factor;
            Plugin.Verbose("Convoy: spawn postponed (" + why + "), next roll in " + (_cooldown / 60f).ToString("0.0") + " min");
        }

        internal void DebugSpawn(SpawnGroup group, bool convoy)
        {
            if (!Plugin.AllowDebugSpawns.Value) return;
            UpdateHeat(); Launch(group, convoy, true);
        }

        internal void DebugRoll()
        {
            if (!Plugin.AllowDebugSpawns.Value) return;
            UpdateHeat(); bool convoy;
            var group = EditorStore.Roll(RollKm, RollBosses, Plugin.PatrolSpawnChancePercent.Value, () => UnityEngine.Random.value, out convoy);
            if (group != null) Launch(group, convoy, true);
            else Plugin.Log.LogInfo("Test spawning: no eligible groups with a positive chance");
        }

        private bool Launch(SpawnGroup definition, bool convoy, bool debug)
        {
            CarTemplates.Refresh(false);
            var group = EditorStore.Compose(definition, convoy, () => UnityEngine.Random.value);
            if (_patrol == null) _patrol = GetComponent<Patrol>();
            Vector3 origin, dir; bool driving;
            if (!Heading(out origin, out dir, out driving)) return false;
            List<Vector3> spots = null; Vector3 facing = Vector3.forward; string where = "";
            for (int attempt = 0; attempt < 6 && spots == null; attempt++)
            {
                float angle = UnityEngine.Random.Range(-45f, 45f);
                var d = Quaternion.Euler(0f, angle, 0f) * dir;
                var center = origin + d * Plugin.ConvoySpawnDistance.Value;
                facing = -d;                                                   // toward the player
                spots = Formation(center, facing, group.Count);
                where = (driving ? "ahead" : "behind") + (angle < -10f ? "-left" : angle > 10f ? "-right" : "") + " " + Plugin.ConvoySpawnDistance.Value.ToString("0") + " m";
            }
            if (spots == null) { Plugin.Log.LogWarning("Convoy: no ground for " + definition.Name + " around the player"); return false; }

            Plugin.Verbose("Convoy: " + definition.Name + (debug ? " (debug)" : "") + " at heat " + (_heat * 100f).ToString("0") + " % (km " + _km.ToString("0.0") + ", bosses " + _bosses
                + "), "
                + where + ": " + string.Join(", ", group.Select(t => t.Name).ToArray()));
            StartCoroutine(BuildAll(group, spots, Quaternion.LookRotation(facing)));
            if (Plugin.SpawnWarning.Value) _warnUntil = Time.time + WarnSeconds;
            return true;
        }

        // Where the player is and which way "ahead" is: the car's travel direction while driving; behind the camera on foot.
        private static bool Heading(out Vector3 origin, out Vector3 dir, out bool driving)
        {
            origin = Vector3.zero; dir = Vector3.forward; driving = false;
            var car = PlayerRef.PlayerCar;
            var pl = PlayerRef.Player;
            var cam = Camera.main;
            if (car != null)
            {
                driving = true;
                origin = car.transform.position;
                var rb = car.GetComponent<Rigidbody>();
                dir = rb != null && rb.velocity.sqrMagnitude > 1f ? rb.velocity : car.transform.forward;
            }
            else if (pl != null || cam != null)
            {
                origin = pl != null ? pl.position : cam.transform.position;
                dir = -(cam != null ? cam.transform.forward : pl.forward);       // behind
            }
            else return false;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
            dir.Normalize();
            return true;
        }

        // Rows of three facing the player (the first car - the truck in a convoy - in the middle of the first row), 7 m apart,
        // rows 9 m behind each other. Every spot needs open terrain under it; null when most of them have none.
        private static List<Vector3> Formation(Vector3 center, Vector3 facing, int count)
        {
            var right = Vector3.Cross(Vector3.up, facing).normalized;
            var spots = new List<Vector3>();
            int missing = 0;
            for (int i = 0; i < count; i++)
            {
                int row = i / 3, col = i % 3;
                float side = col == 0 ? 0f : col == 1 ? -1f : 1f;
                var p = center + right * (side * 7f) - facing * (row * 9f);
                Vector3 g;
                if (GroundAt(p, out g) || GroundAt(p + right * 4f, out g) || GroundAt(p - right * 4f, out g)) spots.Add(g + Vector3.up * 1.0f);
                else missing++;
            }
            if (missing > count / 2 || spots.Count == 0) return null;
            return spots;
        }

        // open terrain straight below: the nearest hit of a downward ray from high above must be the terrain (not a roof, rock or car)
        private static bool GroundAt(Vector3 p, out Vector3 ground)
        {
            ground = p;
            var hits = Physics.RaycastAll(new Vector3(p.x, p.y + 200f, p.z), Vector3.down, 600f, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;
            RaycastHit best = hits[0];
            foreach (var h in hits) if (h.distance < best.distance) best = h;
            if (!(best.collider is TerrainCollider)) return false;
            if (Vector3.Angle(best.normal, Vector3.up) > 30f) return false;
            ground = best.point;
            return true;
        }

        // One build every BuildInterval seconds (each build itself is spread over frames), the crews held; when the last build
        // has finished the whole group is released at once and departs together.
        private const float BuildInterval = 0.7f;

        private IEnumerator BuildAll(List<CarTemplate> group, List<Vector3> spots, Quaternion rot)
        {
            var cars = new List<GameObject>();
            int pending = 0, gen = Patrol.SceneGen;
            for (int i = 0; i < group.Count && i < spots.Count; i++)
            {
                // a save or the pause menu: the rest of the group waits (up to a minute) instead of being dropped; a load ends it
                float waitUntil = Time.realtimeSinceStartup + 60f;
                while (_patrol != null && !_patrol.InGame() && Patrol.SceneGen == gen && Time.realtimeSinceStartup < waitUntil) yield return null;
                if (_patrol == null || !_patrol.InGame() || Patrol.SceneGen != gen) break;
                pending++;
                _patrol.SpawnAt(group[i], spots[i], rot, true, car => { pending--; if (car != null) cars.Add(car); });
                yield return new WaitForSeconds(BuildInterval);
            }
            float until = Time.time + 30f;
            while (pending > 0 && Time.time < until) yield return null;
            int released = 0;
            foreach (var car in cars)
            {
                if (car == null) continue;
                var crew = car.GetComponent<Crew>();
                if (crew != null) { crew.Release(); released++; }
            }
            Plugin.Verbose("Convoy: " + released + " car(s) released");
        }

        // ------------------------------------------------------------ the spawn warning ([General] SpawnWarning)

        // "You hear a sound of distant engines": red, large, top left, for WarnSeconds of game time (frozen while paused), fading out
        // over the last FadeSeconds. Shown for automatic and test spawns of patrols / convoys (Launch), not for single template spawns.
        private const string WarnText = "You hear a sound of distant engines";
        private const float WarnSeconds = 6f, FadeSeconds = 1.5f;
        private float _warnUntil = -1f;
        private GUIStyle _warnStyle; private int _warnSize; private Font _warnFont;

        private void OnGUI()
        {
            if (_warnUntil < 0f || Event.current.type != EventType.Repaint) return;
            float left = _warnUntil - Time.time;
            if (left <= 0f) { _warnUntil = -1f; return; }
            if (Time.timeScale <= 0f || _patrol == null || !_patrol.InGame()) return;   // the menu / a save: shown again afterwards
            int size = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 26f), 18, 64);
            var font = PatrolSkin.GameFont;
            if (_warnStyle == null || size != _warnSize || font != _warnFont)
            {
                _warnSize = size; _warnFont = font;
                _warnStyle = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, wordWrap = false };
                if (font != null) _warnStyle.font = font;
            }
            float a = Mathf.Clamp01(left / FadeSeconds);
            float x = Mathf.Round(Screen.height / 30f), y = Mathf.Round(Screen.height / 12f);
            var rect = new Rect(x, y, Screen.width - 2f * x, size * 2f);
            var old = GUI.color;
            int d = Mathf.Max(1, size / 16);
            GUI.color = new Color(0f, 0f, 0f, 0.85f * a);                       // shadow for contrast on bright sand
            GUI.Label(new Rect(rect.x + d, rect.y + d, rect.width, rect.height), WarnText, _warnStyle);
            GUI.color = new Color(0.9f, 0.08f, 0.05f, a);
            GUI.Label(rect, WarnText, _warnStyle);
            GUI.color = old;
        }

        // the automatic clock, for the save sidecar (seconds; < 0 = not rolled yet)
        internal float CooldownSeconds { get { return _cooldown; } set { _cooldown = value; } }

        // 2.5.3: the group the last automatic spawn used (saved with the clock): the next roll avoids it when another group is eligible, so
        // a save made just before a raid does not bring the same patrol on every load (the clock itself is kept as saved - on purpose)
        internal static string LastGroup = "";
    }
}

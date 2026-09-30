using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapatrol
{
    // The convoy / enemy-car spawner: heat from the game's Distance Travelled and boss kills, a cooldown clock, the spawn-type rolls,
    // the group composition (drawn from the template park by name: <Body>_Basic / <Body>_Advanced, Junker_*, Rustcargo_*),
    // the spot ahead of (or behind) the player, and the staggered builds. Spawned cars are ordinary patrol cars: the Crew drives
    // off as soon as the engine runs and the Pilot hunts the player.
    internal enum SpawnKind { BasicCars, AdvancedCars, SuperCars, BasicConvoy, AdvancedConvoy }

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
            _inst._cooldown = -1f;
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
            if (!Plugin.ConvoyEnabled.Value || Plugin.MaxConvoyCooldown.Value <= 0f) return;
            if (_cooldown < 0f) { ResetCooldown("start"); return; }
            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;
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
            _heat = Mathf.Clamp(km / Mathf.Max(1f, Plugin.HeatIntervalKm.Value) * 0.25f, 0f, Mathf.Max(0f, Plugin.MaxHeat.Value));
        }

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

        private bool Allowed(SpawnKind k)
        {
            switch (k)
            {
                case SpawnKind.BasicCars: return _km >= Plugin.BasicCarsKm.Value && _bosses >= Plugin.BasicCarsBosses.Value;
                case SpawnKind.AdvancedCars: return _km >= Plugin.AdvancedCarsKm.Value && _bosses >= Plugin.AdvancedCarsBosses.Value;
                case SpawnKind.SuperCars: return _km >= Plugin.SuperCarsKm.Value && _bosses >= Plugin.SuperCarsBosses.Value;
                case SpawnKind.BasicConvoy: return _km >= Plugin.BasicConvoyKm.Value && _bosses >= Plugin.BasicConvoyBosses.Value;
                case SpawnKind.AdvancedConvoy: return _km >= Plugin.AdvancedConvoyKm.Value && _bosses >= Plugin.AdvancedConvoyBosses.Value;
            }
            return false;
        }

        private static float Weight(SpawnKind k, float heat)
        {
            switch (k)
            {
                case SpawnKind.BasicCars: return Plugin.BasicCarsChance.Value;
                case SpawnKind.AdvancedCars: return Plugin.AdvancedCarsChance.Value * heat;
                case SpawnKind.SuperCars: return Plugin.SuperCarsChance.Value * heat;
                case SpawnKind.BasicConvoy: return Plugin.BasicConvoyChance.Value;
                case SpawnKind.AdvancedConvoy: return Plugin.AdvancedConvoyChance.Value * heat;
            }
            return 0f;
        }

        // Weighted pick among the given kinds (the weights act as chances; a total above 100 is normalised, below 100 the
        // remainder is nothing... except that a spawn that was decided on always yields something, so we just normalise).
        private static SpawnKind? Roll(List<SpawnKind> kinds, float heat)
        {
            float total = 0f;
            foreach (var k in kinds) total += Mathf.Max(0f, Weight(k, heat));
            if (kinds.Count == 0 || total <= 0f) return kinds.Count > 0 ? kinds[0] : (SpawnKind?)null;
            float r = UnityEngine.Random.value * total;
            foreach (var k in kinds)
            {
                r -= Mathf.Max(0f, Weight(k, heat));
                if (r <= 0f) return k;
            }
            return kinds[kinds.Count - 1];
        }

        // Automatic event: which groups are allowed -> cars or convoy (JustCarsToConvoyRatio) -> which type -> spawn.
        private void Auto()
        {
            var cars = new List<SpawnKind>();
            var convoys = new List<SpawnKind>();
            foreach (SpawnKind k in Enum.GetValues(typeof(SpawnKind)))
                if (Allowed(k)) { if (k >= SpawnKind.BasicConvoy) convoys.Add(k); else cars.Add(k); }
            if (cars.Count == 0 && convoys.Count == 0) { Plugin.Verbose("Convoy: nothing allowed yet (km " + _km.ToString("0.0") + ", bosses " + _bosses + ")"); ResetCooldown("nothing allowed"); return; }

            List<SpawnKind> pool;
            if (convoys.Count > 0 && cars.Count > 0) pool = UnityEngine.Random.value < Plugin.JustCarsToConvoyRatio.Value ? cars : convoys;
            else pool = convoys.Count > 0 ? convoys : cars;
            var kind = Roll(pool, _heat);
            if (kind == null) { ResetCooldown("no kind"); return; }
            if (!ClearOldGroups()) return;
            if (!Launch(kind.Value, _heat, false)) { _cooldown = 60f; return; }   // no spot: try again in a minute
            ResetCooldown("after " + kind.Value);
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

        // half the usual Min..Max cooldown, the roll squared so short waits are likelier
        private void ResetCooldownRetry(string why)
        {
            float a = Mathf.Max(0f, Plugin.MinConvoyCooldown.Value), b = Mathf.Max(a, Plugin.MaxConvoyCooldown.Value);
            float factor = 1f / (0.75f + 0.25f * Mathf.Max(0f, _heat));
            float u = UnityEngine.Random.value;
            _cooldown = (a + (b - a) * u * u) * 60f * 0.5f * factor;
            Plugin.Verbose("Convoy: spawn postponed (" + why + "), next roll in " + (_cooldown / 60f).ToString("0.0") + " min");
        }

        // Debug buttons: every requirement counts as met; the real heat, but at least 100 % so a full group appears.
        internal void DebugCars()
        {
            UpdateHeat();
            var kind = Roll(new List<SpawnKind> { SpawnKind.BasicCars, SpawnKind.AdvancedCars, SpawnKind.SuperCars }, Mathf.Max(1f, _heat));
            if (kind != null) Launch(kind.Value, Mathf.Max(1f, _heat), true);
        }

        internal void DebugConvoy()
        {
            UpdateHeat();
            var kind = Roll(new List<SpawnKind> { SpawnKind.BasicConvoy, SpawnKind.AdvancedConvoy }, Mathf.Max(1f, _heat));
            if (kind != null) Launch(kind.Value, Mathf.Max(1f, _heat), true);
        }

        // ------------------------------------------------------------ composition

        private static bool IsTruckTpl(CarTemplate t) { return (t.Body ?? "").StartsWith("Rust", StringComparison.OrdinalIgnoreCase) || t.Cargo.Length > 0; }
        private static bool IsJunkerTpl(CarTemplate t) { return string.Equals(t.Body, "Junker", StringComparison.OrdinalIgnoreCase); }
        private static bool IsAdvancedTpl(CarTemplate t) { return t.Name.IndexOf("Advanced", StringComparison.OrdinalIgnoreCase) >= 0; }
        private static bool IsBasicTpl(CarTemplate t) { return t.Name.EndsWith("_Basic", StringComparison.OrdinalIgnoreCase) || (!IsAdvancedTpl(t) && IsTruckTpl(t)); }

        private static CarTemplate Pick(Func<CarTemplate, bool> match)
        {
            var list = CarTemplate.Park.Where(match).ToList();
            return list.Count == 0 ? null : list[UnityEngine.Random.Range(0, list.Count)];
        }

        // small = not a junker, not a truck
        private static CarTemplate Small(bool advanced) { return Pick(t => !IsTruckTpl(t) && !IsJunkerTpl(t) && (advanced ? IsAdvancedTpl(t) : IsBasicTpl(t))); }
        private static CarTemplate Junker(bool advanced) { return Pick(t => IsJunkerTpl(t) && (advanced ? IsAdvancedTpl(t) : IsBasicTpl(t))); }
        // a convoy's truck is always a loot truck (a template with cargo); a plain truck only if the park had no loot truck of that tier
        private static CarTemplate Truck(bool advanced)
        {
            return Pick(t => IsTruckTpl(t) && t.Cargo.Length > 0 && (advanced ? IsAdvancedTpl(t) : !IsAdvancedTpl(t)))
                ?? Pick(t => IsTruckTpl(t) && (advanced ? IsAdvancedTpl(t) : !IsAdvancedTpl(t)));
        }

        // base count x heat (capped at 100 %: the group grows with the heat until it is complete, never beyond - heat above 100 %
        // only makes the tougher groups likelier and the clock shorter) x [General] PatrolSizePercent, never below min
        // (1 = a spawn always brings a car)
        private static int Scaled(int baseCount, float heat, int min) { return Mathf.Max(min, Mathf.RoundToInt(baseCount * Mathf.Min(1f, heat) * Plugin.SizeFactor)); }
        private static int Scaled(int baseCount, float heat) { return Scaled(baseCount, heat, 1); }

        // Cars are rolled body first (small or junker), then whether that car is advanced; minimums are applied afterwards.
        // A convoy always starts with its truck; the escort counts are scaled (down to nothing at a small patrol size, but the
        // truck never travels completely alone).
        internal static List<CarTemplate> Compose(SpawnKind kind, float heat)
        {
            var list = new List<CarTemplate>();
            switch (kind)
            {
                case SpawnKind.BasicCars:
                {
                    int n = Scaled(3, heat);
                    for (int i = 0; i < n; i++) Add(list, Small(false));
                    break;
                }
                case SpawnKind.AdvancedCars:
                {
                    int n = Scaled(3, heat);
                    bool junker = UnityEngine.Random.value < Mathf.Clamp01(0.5f * heat);
                    int junkerAt = junker ? UnityEngine.Random.Range(0, n) : -1;
                    var adv = new bool[n];
                    for (int i = 0; i < n; i++) adv[i] = UnityEngine.Random.value < 0.5f;
                    EnsureAdvanced(adv, 1);
                    for (int i = 0; i < n; i++) Add(list, i == junkerAt ? Junker(adv[i]) ?? Small(adv[i]) : Small(adv[i]));
                    break;
                }
                case SpawnKind.SuperCars:
                {
                    int n = Scaled(5, heat);
                    var adv = new bool[n];
                    for (int i = 0; i < n; i++) adv[i] = UnityEngine.Random.value < 0.5f;
                    EnsureAdvanced(adv, 2);
                    for (int i = 0; i < n; i++)
                    {
                        bool junker = UnityEngine.Random.value < 0.5f;
                        Add(list, junker ? Junker(adv[i]) ?? Small(adv[i]) : Small(adv[i]));
                    }
                    break;
                }
                case SpawnKind.BasicConvoy:
                case SpawnKind.AdvancedConvoy:
                {
                    bool advancedConvoy = kind == SpawnKind.AdvancedConvoy;
                    Add(list, Truck(advancedConvoy) ?? Truck(!advancedConvoy));                 // the truck first, always
                    int escortMin = Plugin.SizeFactor < 1f ? 0 : 1;                                // at 100 %+ every escort role keeps its old minimum of one
                    int junkers = Scaled(2, heat, escortMin), smalls = Scaled(UnityEngine.Random.Range(3, 6), heat, escortMin);
                    if (junkers + smalls == 0) smalls = 1;                                         // never a lone truck
                    int n = junkers + smalls;
                    var adv = new bool[n];
                    if (advancedConvoy)
                    {
                        for (int i = 0; i < n; i++) adv[i] = UnityEngine.Random.value < 0.5f;   // half of the others advanced ...
                        EnsureAdvanced(adv, 2);                                                 // ... and at least two
                    }
                    else if (UnityEngine.Random.value < 0.5f) adv[UnityEngine.Random.Range(0, n)] = true;   // 50 %: one advanced car
                    for (int i = 0; i < n; i++)
                        Add(list, i < junkers ? Junker(adv[i]) ?? Small(adv[i]) : Small(adv[i]));
                    break;
                }
            }
            return list;
        }

        private static void Add(List<CarTemplate> list, CarTemplate t) { if (t != null) list.Add(t); }

        private static void EnsureAdvanced(bool[] adv, int min)
        {
            int have = adv.Count(a => a);
            var idx = Enumerable.Range(0, adv.Length).Where(i => !adv[i]).OrderBy(i => UnityEngine.Random.value).ToList();
            for (int k = 0; have < min && k < idx.Count; k++) { adv[idx[k]] = true; have++; }
        }

        internal static string Label(SpawnKind k)
        {
            switch (k)
            {
                case SpawnKind.BasicCars: return "Basic enemy cars";
                case SpawnKind.AdvancedCars: return "Advanced enemy cars";
                case SpawnKind.SuperCars: return "Super advanced enemy cars";
                case SpawnKind.BasicConvoy: return "Basic convoy";
                default: return "Advanced convoy";
            }
        }

        // ------------------------------------------------------------ placement + launch

        private bool Launch(SpawnKind kind, float heat, bool debug)
        {
            var group = Compose(kind, heat);
            if (group.Count == 0) { Plugin.Log.LogWarning("Convoy: no templates for " + Label(kind)); return false; }
            Vector3 origin, dir; bool driving;
            if (!Heading(out origin, out dir, out driving)) { Plugin.Log.LogWarning("Convoy: no player"); return false; }

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
            if (spots == null) { Plugin.Log.LogWarning("Convoy: no ground for " + Label(kind) + " around the player"); return false; }

            Plugin.Verbose("Convoy: " + Label(kind) + (debug ? " (debug)" : "") + " at heat " + (heat * 100f).ToString("0") + " % (km " + _km.ToString("0.0") + ", bosses " + _bosses
                + ", patrol size " + Plugin.PatrolSizePercent.Value + " %), "
                + where + ": " + string.Join(", ", group.Select(t => t.Name).ToArray()));
            StartCoroutine(BuildAll(group, spots, Quaternion.LookRotation(facing)));
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
            int pending = 0;
            for (int i = 0; i < group.Count && i < spots.Count; i++)
            {
                if (_patrol == null || !_patrol.InGame()) break;
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

        // the automatic clock, for the save sidecar (seconds; < 0 = not rolled yet)
        internal float CooldownSeconds { get { return _cooldown; } set { _cooldown = value; } }
    }
}

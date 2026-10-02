using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Apocapatrol
{
    // JSON car templates (the Apocatemplater dumper writes them; schema in CarTemplateFile.cs). Two sources, merged into
    // CarTemplate.Park after the built-in park (same name = replaces the earlier one):
    //   embedded - the repo's CarTemplates\*.json, compiled into the DLL as resources "Apocapatrol.CarTemplates.<file>.json"
    //              (build.sh / csproj) = the "hardcoded" ones that ship with the mod;
    //   file     - BepInEx\plugins\Apocapatrol\CarTemplates\*.json, re-read whenever the spawner menu opens and a file changed,
    //              so a fresh dump can be built in game without a restart. Wins over embedded.
    // Every template takes part in the patrol / convoy picks by its kind (small / junker / truck) and tier (basic / advanced),
    // weighted by "weight", unless "spawns" is false.
    internal static class CarTemplates
    {
        private const string ResourcePrefix = "Apocapatrol.CarTemplates.";
        private static string _dir = "";
        private static List<CarTemplate> _embedded = new List<CarTemplate>();
        private static string _stamp = null;

        internal static string Folder { get { return _dir; } }
        internal static string Summary = "";   // last merge, logged by Plugin.Awake

        internal static void Init(string dllPath)
        {
            try { _dir = Path.Combine(Path.GetDirectoryName(dllPath) ?? "", "CarTemplates"); }
            catch (Exception) { _dir = ""; }
        }

        // Awake: embedded once, the folder now
        internal static void Load()
        {
            _embedded = LoadEmbedded();
            Refresh(true);
        }

        // the spawner menu calls this on every open: the folder is re-read only when a file was added, removed or rewritten
        internal static void Refresh(bool force)
        {
            string stamp = Stamp();
            if (!force && stamp == _stamp) return;
            _stamp = stamp;
            Rebuild(LoadFolder());
        }

        private static void Rebuild(List<CarTemplate> files)
        {
            var list = new List<CarTemplate>(CarTemplate.Builtin);
            var replaced = new List<string>();
            foreach (var t in _embedded.Concat(files))
            {
                int i = list.FindIndex(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) { replaced.Add(t.Name + " (" + list[i].Origin + " -> " + t.Origin + ")"); list[i] = t; }
                else list.Add(t);
            }
            CarTemplate.Park = list.ToArray();
            Summary = "Car templates: " + CarTemplate.Builtin.Length + " built-in, " + _embedded.Count + " embedded, " + files.Count + " from files";
            Plugin.Verbose("Car templates: " + CarTemplate.Builtin.Length + " built-in, " + _embedded.Count + " embedded, " + files.Count + " from " + _dir
                + (replaced.Count > 0 ? "; replaced " + string.Join(", ", replaced.ToArray()) : "") + " -> park of " + list.Count);
        }

        private static List<CarTemplate> LoadEmbedded()
        {
            var r = new List<CarTemplate>();
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var res in asm.GetManifestResourceNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!res.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !res.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    string text;
                    using (var s = asm.GetManifestResourceStream(res))
                    {
                        if (s == null) continue;
                        using (var rd = new StreamReader(s, Encoding.UTF8)) text = rd.ReadToEnd();
                    }
                    string fallback = res.Substring(ResourcePrefix.Length, res.Length - ResourcePrefix.Length - ".json".Length);
                    var t = Parse(text, fallback, "embedded", res);
                    if (t != null) r.Add(t);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Car templates (embedded): " + e.Message); }
            return r;
        }

        private static List<CarTemplate> LoadFolder()
        {
            var r = new List<CarTemplate>();
            if (_dir.Length == 0 || !Directory.Exists(_dir)) return r;
            try
            {
                foreach (var path in Directory.GetFiles(_dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string text;
                    try { text = File.ReadAllText(path); }
                    catch (Exception e) { Plugin.Log.LogWarning("Car template " + path + ": " + e.Message); continue; }
                    var t = Parse(text, Path.GetFileNameWithoutExtension(path), "file", path);
                    if (t != null) r.Add(t);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Car templates (" + _dir + "): " + e.Message); }
            return r;
        }

        private static string Stamp()
        {
            if (_dir.Length == 0 || !Directory.Exists(_dir)) return "";
            var sb = new StringBuilder();
            try
            {
                foreach (var path in Directory.GetFiles(_dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(path);
                    sb.Append(fi.Name).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('|').Append(fi.Length).Append(';');
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        private static string S(string s) { return (s ?? "").Trim(); }

        internal static CarTemplate Parse(string json, string fallbackName, string origin, string where)
        {
            TemplateFile f;
            try { f = TemplateFile.FromJson(json); }
            catch (Exception e) { Plugin.Log.LogWarning("Car template " + where + ": not valid JSON (" + e.Message + ")"); return null; }
            if (f == null) { Plugin.Log.LogWarning("Car template " + where + ": empty"); return null; }
            if (S(f.body).Length == 0) { Plugin.Log.LogWarning("Car template " + where + ": no \"body\""); return null; }

            var parts = (f.parts ?? new TemplatePart[0]).Where(p => p != null).ToArray();
            if (parts.Length == 0) { Plugin.Log.LogWarning("Car template " + where + ": no \"parts\" - skipped (a frame without parts would not drive)"); return null; }
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].parent >= i) { Plugin.Log.LogWarning("Car template " + where + ": part [" + i + "] " + parts[i].prefab + " names parent [" + parts[i].parent + "], which is not an earlier part - attached to the frame instead"); parts[i].parent = -1; }
                parts[i].hinge = S(parts[i].hinge); parts[i].prefab = S(parts[i].prefab);
            }

            var t = new CarTemplate
            {
                Name = S(f.name).Length > 0 ? S(f.name) : fallbackName,
                Body = S(f.body),
                Kind = S(f.kind),
                Kinds = ParseKinds(f.kind),
                Tier = S(f.tier).ToLowerInvariant(),
                Spawns = f.spawns,
                Weight = Mathf.Max(0f, f.weight),
                Driver = S(f.driver),
                Passenger = S(f.passenger),
                Cargo = S(f.cargo),
                LootScaleMin = f.lootScaleMin, LootScaleMax = f.lootScaleMax,
                Bumpers = f.bumpers != null && f.bumpers.Length > 0 ? f.bumpers.Select(S).ToArray() : null,
                FillFuel = f.fillFuel, ReleaseHandbrake = f.releaseHandbrake,
                Parts = parts,
                Origin = origin,
            };
            if (t.Tier.Length == 0) t.Tier = t.Name.IndexOf("Advanced", StringComparison.OrdinalIgnoreCase) >= 0 ? "advanced" : "basic";
            t.Rams = S(f.rams).Length > 0 ? CarTemplate.ParseRams(S(f.rams)) : (t.IsTruck ? RamTargets.Cars : RamTargets.Pedestrians);
            foreach (var p in parts)   // for the menu summary / Describe
            {
                if (p.parent >= 0) continue;
                if (p.hinge.EndsWith("hinge_engine", StringComparison.Ordinal)) t.Engine = p.prefab;
                else if (p.hinge.EndsWith("hinge_seat_driver", StringComparison.Ordinal)) t.Seat = p.prefab;
                else if (p.hinge.EndsWith("hinge_seat_passenger", StringComparison.Ordinal)) t.PassengerSeat = p.prefab;
            }
            if (t.Driver.Length > 0 && t.Seat.Length == 0) Plugin.Log.LogWarning("Car template " + t.Name + ": a driver but no part on hinge_seat_driver");
            return t;
        }

        // "small;motorcycle" / "small, motorcycle" / "small motorcycle" -> { "small", "motorcycle" }
        internal static string[] ParseKinds(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return new string[0];
            return kind.Split(new[] { ';', ',', ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).Distinct().ToArray();
        }

        // A "cargo" value that is an item spec ("dogfood_can:6;akms:1") rather than "Random" or a [Loot] key
        internal static bool IsItemSpec(string cargo) { return cargo != null && (cargo.IndexOf(':') >= 0 || cargo.IndexOf(';') >= 0); }
    }
}

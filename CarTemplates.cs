using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Apocapatrol
{
    internal static class CarTemplates
    {
        private const string Prefix = "Apocapatrol.DefaultTemplates.";
        private static readonly Dictionary<string, string> _defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _root, _stamp;
        internal static string Folder { get; private set; }
        internal static string PlayerFolder { get; private set; }
        internal static string Summary = "";
        internal static void Init(string dllPath)
        {
            _root = Path.GetDirectoryName(Path.GetFullPath(dllPath)); Folder = Path.Combine(_root, "BaseTemplates"); PlayerFolder = Path.Combine(_root, "PlayerTemplates");
            Directory.CreateDirectory(Folder); Directory.CreateDirectory(PlayerFolder);
        }
        internal static void Load()
        {
            _defaults.Clear(); var asm = Assembly.GetExecutingAssembly();
            foreach (string resource in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                using (var stream = asm.GetManifestResourceStream(resource)) using (var reader = new StreamReader(stream)) _defaults[resource.Substring(Prefix.Length, resource.Length - Prefix.Length - 5)] = reader.ReadToEnd();
            foreach (var pair in _defaults) { string path = EditorStore.SafePath(Folder, pair.Key + ".json"); if (!File.Exists(path)) EditorStore.AtomicWrite(path, pair.Value); }
            UpgradeMotorcycleLoot(); MigrateLegacy(); Refresh(true);
        }
        private static void UpgradeMotorcycleLoot()
        {
            string shipped;
            if (!_defaults.TryGetValue("motorcycle_template", out shipped)) return;
            string path = EditorStore.SafePath(Folder, "motorcycle_template.json");
            try
            {
                var old = TemplateFile.FromJson(File.ReadAllText(path));
                if (old.lootPreset != "car-standard") return;
                old.lootPreset = "car-empty";
                // Only update the unchanged 2.0.2 default; preserve edited templates and loot profiles.
                if (old.ToJson() != TemplateFile.FromJson(shipped).ToJson()) return;
                string backup = Path.Combine(_root, "TemplateUpgradeBackups"); Directory.CreateDirectory(backup);
                File.Copy(path, EditorStore.UniquePath(backup, "motorcycle_template-2_0_2", ".json"));
                EditorStore.AtomicWrite(path, old.ToJson());
            }
            catch (Exception e) { Warn("Motorcycle loot upgrade: " + e.Message); }
        }
        private static void MigrateLegacy()
        {
            string legacy = Path.Combine(_root, "CarTemplates"), marker = Path.Combine(_root, "legacy-templates-imported.txt");
            if (!Directory.Exists(legacy) || File.Exists(marker)) return;
            foreach (string path in Directory.GetFiles(legacy, "*.json")) try
            {
                var file = TemplateFile.FromJson(File.ReadAllText(path)); string name = string.IsNullOrWhiteSpace(file.name) ? Path.GetFileNameWithoutExtension(path) : file.name;
                if (_defaults.ContainsKey(name)) name += "_Legacy";
                string dest = EditorStore.UniquePath(PlayerFolder, name, ".json"); file.name = Path.GetFileNameWithoutExtension(dest); file.schema = 2;
                if (file.lootPreset.Length == 0) file.lootPreset = file.body == "Rustcargo" ? (file.cargo.Length > 0 ? "truck-food" : "truck-empty") : "car-standard";
                EditorStore.AtomicWrite(dest, file.ToJson());
            }
            catch (Exception e) { Warn("Legacy template preserved but import failed " + path + ": " + e.Message); }
            File.WriteAllText(marker, "Imported copies into PlayerTemplates. Originals remain in CarTemplates.");
        }
        internal static void RestoreDefaults() { foreach (var pair in _defaults) EditorStore.AtomicWrite(EditorStore.SafePath(Folder, pair.Key + ".json"), pair.Value); }
        internal static void Refresh(bool force)
        {
            string stamp = Stamp(Folder) + Stamp(PlayerFolder); if (!force && stamp == _stamp) return; _stamp = stamp;
            var list = new Dictionary<string, CarTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _defaults)
            {
                var t = Parse(pair.Value, pair.Key, "default", pair.Key); if (t == null) continue;
                t.IsDefault = t.Favorite = true; t.SourcePath = EditorStore.SafePath(Folder, pair.Key + ".json"); list[t.Name] = t;
            }
            LoadFolder(Folder, true, list); LoadFolder(PlayerFolder, false, list);
            CarTemplate.Park = list.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (CarTemplate.Park.Length == 0) CarTemplate.Park = CarTemplate.Builtin;
            Summary = CarTemplate.Park.Length + " templates: code defaults + BaseTemplates + PlayerTemplates";
        }
        private static void LoadFolder(string folder, bool favorite, Dictionary<string, CarTemplate> list)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string path in Directory.GetFiles(folder, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) try
            {
                var t = Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), favorite ? "base" : "player", path);
                if (t == null || (!favorite && list.ContainsKey(t.Name))) continue;
                t.IsDefault = _defaults.ContainsKey(t.Name); t.Favorite = favorite || t.IsDefault; t.SourcePath = Path.GetFullPath(path); list[t.Name] = t;
            }
            catch (Exception e) { Warn("Template file preserved: " + path + ": " + e.Message); }
        }
        private static string Stamp(string folder)
        {
            if (!Directory.Exists(folder)) return "";
            return string.Join(";", Directory.GetFiles(folder, "*.json").OrderBy(x => x).Select(path => { var info = new FileInfo(path); return info.Name + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks; }).ToArray());
        }
        internal static void Favorite(CarTemplate t)
        {
            if (t.IsDefault || t.SourcePath.Length == 0) return;
            string source = ManagedPath(t.SourcePath), target = EditorStore.SafePath(t.Favorite ? PlayerFolder : Folder, Path.GetFileName(source));
            if (File.Exists(target)) throw new IOException("That filename already exists in the destination folder");
            File.Move(source, target); Refresh(true);
        }
        internal static void Delete(CarTemplate t)
        {
            if (t.IsDefault || t.Favorite) throw new InvalidOperationException("Unfavourite custom templates before deleting them");
            Recycle(t.SourcePath);
            foreach (var g in EditorStore.Data.Patrols.Concat(EditorStore.Data.Convoys)) g.Templates.RemoveAll(n => n.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
            EditorStore.Save(); Refresh(true);
        }
        private static string ManagedPath(string path)
        {
            string full = Path.GetFullPath(path); var folders = new[] { Folder, PlayerFolder, Path.Combine(_root, "OldTemplatesFolder") };
            if (!folders.Any(dir => string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) || !full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("File outside managed template folders");
            return full;
        }
        internal static void Recycle(string path) { string file = ManagedPath(path); if (File.Exists(file)) ShellRecycle.Move(file); }
        private static void Warn(string message) { if (Plugin.Log != null) Plugin.Log.LogWarning(message); }

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
                Spawns = f.spawns,
                Weight = Math.Max(0f, f.weight),
                Driver = S(f.driver),
                Passenger = S(f.passenger),
                LootPreset = S(f.lootPreset),
                Bumpers = f.bumpers != null && f.bumpers.Length > 0 ? f.bumpers.Select(S).ToArray() : null,
                FillFuel = f.fillFuel, ReleaseHandbrake = f.releaseHandbrake,
                FuelLitres = f.fuelLitres,
                Parts = parts,
                Origin = origin,
            };
            if (t.LootPreset.Length == 0 && f.schema < 2) t.LootPreset = t.IsTruck ? (f.cargo.Length > 0 ? "truck-food" : "truck-empty") : "car-standard";
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

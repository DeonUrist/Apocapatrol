using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Apocapatrol
{
    internal static class EditorStore
    {
        internal static EditorData Data;
        internal static string Root, StatePath;

        internal static EditorData Defaults()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Apocapatrol.EditorDefaults.json"))
            {
                if (stream == null) throw new InvalidOperationException("Missing shipped editor defaults");
                using (var reader = new StreamReader(stream)) return EditorData.FromJson(reader.ReadToEnd());
            }
        }

        internal static void Load(string root)
        {
            Root = Path.GetFullPath(root); StatePath = Path.Combine(Root, "PatrolsAndLoot.json");
            Directory.CreateDirectory(Root);
            Data = Defaults();
            if (!File.Exists(StatePath)) { Save(); UpgradeMotorcycleDefault(); return; }
            try { Data = EditorData.FromJson(File.ReadAllText(StatePath)); }
            catch (Exception e)
            {
                File.Copy(StatePath, UniquePath(Root, "PatrolsAndLoot.invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), ".json"));
                if (Plugin.Log != null) Plugin.Log.LogWarning("Editor settings invalid; preserved a copy and loaded defaults: " + e.Message);
            }
            UpgradeMotorcycleDefault();
        }

        private static void UpgradeMotorcycleDefault()
        {
            string marker = Path.Combine(Root, "motorcycle-template-upgrade.txt");
            if (File.Exists(marker)) return;
            var bikers = Data.Patrols.FirstOrDefault(g => g.Id == "basic-bikers");
            if (bikers != null && bikers.Templates.SequenceEqual(new[] { "Halfbreed_Basic", "Halfbreed_Basic", "Halfbreed_Basic" }))
            {
                bikers.Templates = new List<string> { "motorcycle_template", "motorcycle_template", "motorcycle_template" }; Save();
            }
            File.WriteAllText(marker, "2.0.2 motorcycle default added; custom rosters are preserved.");
        }

        internal static void Save() { AtomicWrite(StatePath, Data.ToJson()); }
        internal static void AtomicWrite(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        internal static string UniquePath(string dir, string stem, string extension)
        {
            stem = SafeName(stem);
            if (stem.Length == 0) throw new ArgumentException("Empty filename");
            string path = SafePath(dir, stem + extension);
            for (int i = 1; File.Exists(path); i++) path = SafePath(dir, stem + "_" + i + extension);
            return path;
        }
        internal static string SafeName(string name) { return new string((name ?? "").Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray()); }
        internal static string SafePath(string dir, string name)
        {
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name) != name) throw new ArgumentException("Path outside template folder");
            return path;
        }
        internal static LootProfile Loot(bool truck, string id) { return (truck ? Data.TruckLoot : Data.CarLoot).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)); }
        internal static LootProfile RollTruckLoot(Func<double> random, IEnumerable<string> allowed = null)
        {
            var filter = allowed != null ? new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase) : null;
            var eligible = Data.TruckLoot.Where(p => p.SpawnChance > 0 && (filter == null || filter.Contains(p.Id))).ToList(); double total = eligible.Sum(p => (double)p.SpawnChance);
            if (total <= 0) return null;
            double roll = Math.Min(.999999999999, Math.Max(0, random())) * total;
            foreach (var p in eligible) { roll -= p.SpawnChance; if (roll < 0) return p; }
            return eligible.LastOrDefault();
        }

        internal static SpawnGroup Roll(float km, int bosses, float patrolPercent, Func<double> random, out bool convoy)
        {
            bool p = Data.Patrols.Any(g => g.Eligible(km, bosses)), c = Data.Convoys.Any(g => g.Eligible(km, bosses));
            double percentage = Math.Max(0, Math.Min(100, patrolPercent));
            convoy = c && (!p || percentage <= 0 || (percentage < 100 && random() >= percentage / 100d));
            return SpawnGroup.Roll(convoy ? Data.Convoys : Data.Patrols, km, bosses, random);
        }

        internal static List<CarTemplate> Compose(SpawnGroup group, bool convoy, Func<double> random)
        {
            var list = new List<CarTemplate>();
            foreach (string name in group.Templates)
            {
                var template = CarTemplate.Park.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (template != null && (convoy || !template.IsTruck)) list.Add(template.CloneForSpawn());
            }
            if (!convoy && list.Count == 0) list.Add(CarTemplate.FallbackCar.CloneForSpawn());
            if (convoy && !list.Any(t => t.IsTruck)) { var fallback = CarTemplate.FallbackTruck.CloneForSpawn(); fallback.SpawnLoot = new Dictionary<string, int>(); fallback.SpawnCargoKey = ""; list.Insert(0, fallback); }
            if (convoy) list = list.OrderByDescending(t => t.IsTruck).ToList();
            if (convoy) foreach (var truck in list.Where(t => t.IsTruck)) truck.SpawnCargoOptions = (group.AllowedCargo ?? new List<string>()).ToArray();
            if (convoy && group.UniformCargo)
            {
                var trucks = list.Where(t => t.IsTruck && t.SpawnLoot == null).ToList();
                var shared = trucks.Count > 0 ? RollTruckLoot(random, group.AllowedCargo) : null;
                var roll = shared != null ? shared.Roll(random) : new Dictionary<string, int>();
                    foreach (var truck in trucks) { truck.SpawnLoot = new Dictionary<string, int>(roll, StringComparer.OrdinalIgnoreCase); truck.SpawnCargoKey = CargoTexture(roll, shared != null ? shared.TextureKey : ""); }
            }
            return list;
        }

        internal static string CargoTexture(Dictionary<string, int> items, string hint)
        {
            return string.IsNullOrWhiteSpace(hint) ? "none" : hint;

        }

        internal static void Reset(bool recycleCustom)
        {
            CarTemplates.Refresh(true);
            var custom = CarTemplate.Park.Where(t => !t.IsDefault).ToList();
            string old = Path.Combine(Root, "OldTemplatesFolder"); Directory.CreateDirectory(old);
            var moved = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (string file in Directory.GetFiles(CarTemplates.Folder, "*.json"))
                {
                    string source = SafePath(CarTemplates.Folder, Path.GetFileName(file));
                    string target = UniquePath(old, Path.GetFileNameWithoutExtension(file), ".json");
                    File.Move(source, target); moved.Add(new KeyValuePair<string, string>(source, target));
                }
                CarTemplates.RestoreDefaults();
                if (!recycleCustom)
                    foreach (var template in custom.Where(t => t.Favorite))
                    {
                        var match = moved.FirstOrDefault(pair => string.Equals(pair.Key, template.SourcePath, StringComparison.OrdinalIgnoreCase));
                        if (match.Value != null) File.Copy(match.Value, template.SourcePath);
                    }
                else
                    foreach (var template in custom)
                    {
                        string file = template.SourcePath;
                        var match = moved.FirstOrDefault(pair => string.Equals(pair.Key, file, StringComparison.OrdinalIgnoreCase));
                        if (match.Value != null) file = match.Value;
                        CarTemplates.Recycle(file);
                    }
                var defaults = Defaults();
                defaults.CarLoot.AddRange(Data.CarLoot.Where(p => !defaults.CarLoot.Any(d => d.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))).ToList());
                defaults.TruckLoot.AddRange(Data.TruckLoot.Where(p => !defaults.TruckLoot.Any(d => d.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))).ToList());
                Data = defaults; Save(); CarTemplates.Refresh(true);
            }
            catch
            {
                foreach (var pair in moved)
                    if (File.Exists(pair.Value) && !File.Exists(pair.Key)) File.Move(pair.Value, pair.Key);
                CarTemplates.Refresh(true);
                throw;
            }
        }
    }
}

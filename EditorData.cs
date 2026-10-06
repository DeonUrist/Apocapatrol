using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Apocapatrol
{
    public sealed class LootItem
    {
        public string Id = "";
        public float Chance;
    }

    public sealed class LootProfile
    {
        public string Id = "", Name = "", TextureKey = "";
        public bool Default;
        public float SpawnChance = 10;
        public List<LootItem> Items = new List<LootItem>();

        public static int RollCount(double percent, Func<double> random)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0) return 0;
            double count = Math.Min(100000d, percent) / 100d;
            int whole = (int)Math.Floor(count);
            return whole + (count > whole && random() < count - whole ? 1 : 0);
        }

        public Dictionary<string, int> Roll(Func<double> random)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items)
            {
                int count = RollCount(item.Chance, random);
                if (count <= 0 || string.IsNullOrWhiteSpace(item.Id)) continue;
                int previous; result.TryGetValue(item.Id, out previous);
                result[item.Id] = previous + count;
            }
            return result;
        }


    }

    public sealed class SpawnGroup
    {
        public string Id = "", Name = "";
        public float MinKm, Chance;
        public int MinBosses;
        public bool UniformCargo;
        public List<string> Templates = new List<string>();
        public List<string> AllowedCargo = new List<string>();
        public bool Eligible(float km, int bosses) { return Chance > 0 && km >= MinKm && bosses >= MinBosses; }

        public static SpawnGroup Roll(IEnumerable<SpawnGroup> source, float km, int bosses, Func<double> random)
        {
            var list = source.Where(g => g.Eligible(km, bosses)).ToList();
            double total = list.Sum(g => (double)g.Chance);
            if (total <= 0) return null;
            double roll = random() * total;
            foreach (var group in list) { roll -= group.Chance; if (roll < 0) return group; }
            return list[list.Count - 1];
        }
    }

    public sealed class EditorData
    {
        public List<SpawnGroup> Patrols = new List<SpawnGroup>(), Convoys = new List<SpawnGroup>();
        public List<LootProfile> CarLoot = new List<LootProfile>(), TruckLoot = new List<LootProfile>();
        public List<string> TemplateOrder = new List<string>();   // the template lists' order in the editor (names; unlisted ones follow, defaults first, by name)

        public static EditorData FromJson(string text)
        {
            var root = Json.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Expected editor JSON object");
            var result = new EditorData();
            result.Patrols = Groups(root, "patrols"); result.Convoys = Groups(root, "convoys");
            result.CarLoot = Loot(root, "carLoot"); result.TruckLoot = Loot(root, "truckLoot");
            object order;
            if (root.TryGetValue("templateOrder", out order) && order is List<object>) result.TemplateOrder = ((List<object>)order).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in result.Convoys.Where(g => g.AllowedCargo == null))
                g.AllowedCargo = g.Id.EndsWith("-truck", StringComparison.OrdinalIgnoreCase) ? new List<string> { "truck-empty" } : result.TruckLoot.Where(p => p.Id != "truck-empty").Select(p => p.Id).ToList();
            return result;
        }

        private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> root, string key)
        {
            object value;
            if (!root.TryGetValue(key, out value) || !(value is List<object>)) throw new FormatException("Missing array: " + key);
            foreach (var row in (List<object>)value)
            {
                var obj = row as Dictionary<string, object>;
                if (obj == null) throw new FormatException("Invalid object in " + key);
                yield return obj;
            }
        }

        private static List<SpawnGroup> Groups(Dictionary<string, object> root, string key)
        {
            var result = new List<SpawnGroup>();
            foreach (var row in Rows(root, key))
            {
                var g = new SpawnGroup { Id = Json.Text(row, "id", ""), Name = Json.Text(row, "name", ""), MinKm = Number(row, "minKm", 0, 10000), MinBosses = (int)Number(row, "minBosses", 0, 7), Chance = Number(row, "chance", 0, 100), UniformCargo = Json.Bool(row, "uniformCargo", false) };
                object values;
                if (row.TryGetValue("templates", out values) && values is List<object>) g.Templates = ((List<object>)values).OfType<string>().ToList();
                g.AllowedCargo = row.TryGetValue("allowedCargo", out values) && values is List<object> ? ((List<object>)values).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() : key == "convoys" ? null : new List<string>();
                if (g.Id.Length == 0 || result.Any(x => x.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase))) throw new FormatException("Missing or duplicate group id");
                result.Add(g);
            }
            return result;
        }

        private static List<LootProfile> Loot(Dictionary<string, object> root, string key)
        {
            var result = new List<LootProfile>();
            foreach (var row in Rows(root, key))
            {
                var p = new LootProfile { Id = Json.Text(row, "id", ""), Name = Json.Text(row, "name", ""), TextureKey = Json.Text(row, "textureKey", ""), Default = Json.Bool(row, "default", false) };
                p.SpawnChance = row.ContainsKey("spawnChance") ? Number(row, "spawnChance", 0, 100) : DefaultCargoChance(p.Id);
                foreach (var item in Rows(row, "items"))
                {
                    string id = Json.Text(item, "id", "");
                    if (id.Length == 0 || p.Items.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new FormatException("Missing or duplicate loot item");
                    p.Items.Add(new LootItem { Id = id, Chance = Number(item, "chance", 0, 100000) });
                }
                if (p.Id.Length == 0 || result.Any(x => x.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))) throw new FormatException("Missing or duplicate loot id");
                result.Add(p);
            }
            return result;
        }

        private static float Number(Dictionary<string, object> row, string key, float min, float max)
        {
            float n = Json.Num(row, key, min);
            if (float.IsInfinity(n) || float.IsNaN(n)) throw new FormatException("Invalid " + key);
            return Math.Max(min, Math.Min(max, n));
        }
        private static float DefaultCargoChance(string id)
        {
            switch (id)
            {
                case "truck-food": return 18; case "truck-water": return 14;
                case "truck-gasoline": case "truck-diesel": case "truck-medicine": case "truck-weapons": return 11;
                case "truck-drugs": case "truck-mechanic": case "truck-corpses": return 7;
                case "truck-rats": return 3; case "truck-empty": return 0; default: return 10;
            }
        }

        public string ToJson()
        {
            Func<SpawnGroup, object> group = g => StoreJson.Object("id", g.Id, "name", g.Name, "minKm", g.MinKm, "minBosses", g.MinBosses, "chance", g.Chance, "uniformCargo", g.UniformCargo, "templates", g.Templates, "allowedCargo", g.AllowedCargo);
            Func<LootProfile, object> loot = p => StoreJson.Object("id", p.Id, "name", p.Name, "textureKey", p.TextureKey, "default", p.Default, "spawnChance", p.SpawnChance, "items", p.Items.Select(i => StoreJson.Object("id", i.Id, "chance", i.Chance)).ToList());
            return StoreJson.Write(StoreJson.Object("schema", 2, "patrols", Patrols.Select(group).ToList(), "convoys", Convoys.Select(group).ToList(), "carLoot", CarLoot.Select(loot).ToList(), "truckLoot", TruckLoot.Select(loot).ToList(), "templateOrder", TemplateOrder));
        }
    }

    internal static class StoreJson
    {
        internal static Dictionary<string, object> Object(params object[] values)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1];
            return result;
        }
        internal static string Write(object value) { var b = new StringBuilder(); Append(b, value, 0); return b.Append('\n').ToString(); }
        private static void Append(StringBuilder b, object value, int depth)
        {
            if (value == null) { b.Append("null"); return; }
            if (value is string) { Json.Str(b, (string)value); return; }
            if (value is bool) { b.Append((bool)value ? "true" : "false"); return; }
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                b.Append("{\n"); int n = 0;
                foreach (var pair in dict) { if (n++ > 0) b.Append(",\n"); b.Append(' ', (depth + 1) * 2); Json.Str(b, pair.Key); b.Append(": "); Append(b, pair.Value, depth + 1); }
                b.Append('\n').Append(' ', depth * 2).Append('}'); return;
            }
            var list = value as IEnumerable;
            if (list != null) { b.Append('['); int n = 0; foreach (var item in list) { if (n++ > 0) b.Append(", "); Append(b, item, depth + 1); } b.Append(']'); return; }
            b.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }
}

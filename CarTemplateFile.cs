using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apocapatrol
{
    // The car template file (JSON). The same file lives in Apocatemplater as TemplateFile.cs (only the namespace differs) - keep
    // them identical. Every field has a default, so a hand-written file may leave any out.
    // Read/written by the small JSON code below, NOT Unity's JsonUtility: in a BepInEx plugin JsonUtility silently drops the
    // nested TemplatePart[] array (Apocatemplater 1.0.1 wrote files without "parts").
    public class TemplateFile
    {
        public int schema = 1;
        public string name = "";            // template name (spawner menu, logs); "" = the file name
        public string body = "";            // frame prefab (PipeRat, Poloska, TinyTyrant, Junker, Rustcargo, Halfbreed ...)
        public string kind = "";            // one or more kinds, ';'-separated: "small", "junker", "truck", "small;motorcycle" ...
                                            // ("" = guessed from the body like the built-in park); every listed kind counts
        public string tier = "";            // basic | advanced          ("" = guessed from the name: "Advanced" in it = advanced)
        public bool spawns = true;          // false = only in the spawner menu, never picked by patrols / convoys
        public float weight = 1f;           // relative pick chance among the templates of the same kind + tier (built-ins are 1)
        public string rams = "";            // None | Pedestrians | Cars ("" = Cars for trucks, Pedestrians otherwise)
        public string driver = "";          // crew prefab (Scraffa, Spanna, Sprokka, Boltjaw, Flexa, Lugnut, Scrud); "" = nobody
        public string passenger = "";
        public string cargo = "";           // "" none | "Random" (rolled by the [Loot] chances) | a loot key (Food ...) | an item spec "a:2;b:1-3"
        public float lootScaleMin = 1f, lootScaleMax = 1f;
        public string[] bumpers = new string[0];   // a front bumper rolled per build ("" = none), only if no part sits on hinge_bumper_front
        public bool fillFuel = true, releaseHandbrake = true;
        public TemplatePart[] parts = new TemplatePart[0];
        public string source = "";          // who wrote it (Apocatemplater version, date, the car it came from) - information only

        // ------------------------------------------------------------ write
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            P(sb, "schema", schema); P(sb, "name", name); P(sb, "body", body); P(sb, "kind", kind); P(sb, "tier", tier);
            P(sb, "spawns", spawns); P(sb, "weight", weight); P(sb, "rams", rams); P(sb, "driver", driver); P(sb, "passenger", passenger);
            P(sb, "cargo", cargo); P(sb, "lootScaleMin", lootScaleMin); P(sb, "lootScaleMax", lootScaleMax);
            sb.Append("    \"bumpers\": [");
            for (int i = 0; i < (bumpers ?? new string[0]).Length; i++) { if (i > 0) sb.Append(", "); Json.Str(sb, bumpers[i]); }
            sb.Append("],\n");
            P(sb, "fillFuel", fillFuel); P(sb, "releaseHandbrake", releaseHandbrake);
            sb.Append("    \"parts\": [");
            var ps = parts ?? new TemplatePart[0];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("        { \"parent\": ").Append(p.parent).Append(", \"hinge\": "); Json.Str(sb, p.hinge);
                sb.Append(", \"prefab\": "); Json.Str(sb, p.prefab);
                Arr(sb, "hingePos", p.hingePos); Arr(sb, "hingeRot", p.hingeRot); Arr(sb, "pos", p.pos); Arr(sb, "rot", p.rot);
                sb.Append(" }");
            }
            sb.Append(ps.Length > 0 ? "\n    ],\n" : "],\n");
            sb.Append("    \"source\": "); Json.Str(sb, source); sb.Append("\n}\n");
            return sb.ToString();
        }

        private static void P(StringBuilder sb, string k, object v)
        {
            sb.Append("    \"").Append(k).Append("\": ");
            if (v is string) Json.Str(sb, (string)v);
            else if (v is bool) sb.Append((bool)v ? "true" : "false");
            else if (v is float) sb.Append(((float)v).ToString("0.####", CultureInfo.InvariantCulture));
            else sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
            sb.Append(",\n");
        }

        private static void Arr(StringBuilder sb, string k, float[] a)
        {
            if (a == null || a.Length == 0) return;   // empty = untouched: left out of the file
            sb.Append(", \"").Append(k).Append("\": [");
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(a[i].ToString("0.####", CultureInfo.InvariantCulture)); }
            sb.Append(']');
        }

        // ------------------------------------------------------------ read
        public static TemplateFile FromJson(string text)
        {
            var root = Json.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("the file is not a JSON object");
            var f = new TemplateFile();
            f.schema = (int)Json.Num(root, "schema", f.schema);
            f.name = Json.Text(root, "name", f.name); f.body = Json.Text(root, "body", f.body);
            f.kind = Json.Text(root, "kind", f.kind); f.tier = Json.Text(root, "tier", f.tier);
            f.spawns = Json.Bool(root, "spawns", f.spawns); f.weight = Json.Num(root, "weight", f.weight);
            f.rams = Json.Text(root, "rams", f.rams); f.driver = Json.Text(root, "driver", f.driver); f.passenger = Json.Text(root, "passenger", f.passenger);
            f.cargo = Json.Text(root, "cargo", f.cargo);
            f.lootScaleMin = Json.Num(root, "lootScaleMin", f.lootScaleMin); f.lootScaleMax = Json.Num(root, "lootScaleMax", f.lootScaleMax);
            f.fillFuel = Json.Bool(root, "fillFuel", f.fillFuel); f.releaseHandbrake = Json.Bool(root, "releaseHandbrake", f.releaseHandbrake);
            f.source = Json.Text(root, "source", f.source);
            object o;
            if (root.TryGetValue("bumpers", out o) && o is List<object>)
            {
                var l = new List<string>();
                foreach (var b in (List<object>)o) l.Add(b as string ?? "");
                f.bumpers = l.ToArray();
            }
            if (root.TryGetValue("parts", out o) && o is List<object>)
            {
                var l = new List<TemplatePart>();
                foreach (var e in (List<object>)o)
                {
                    var d = e as Dictionary<string, object>;
                    if (d == null) continue;
                    var p = new TemplatePart();
                    p.parent = (int)Json.Num(d, "parent", p.parent);
                    p.hinge = Json.Text(d, "hinge", p.hinge); p.prefab = Json.Text(d, "prefab", p.prefab);
                    p.hingePos = Json.Floats(d, "hingePos"); p.hingeRot = Json.Floats(d, "hingeRot");
                    p.pos = Json.Floats(d, "pos"); p.rot = Json.Floats(d, "rot");
                    l.Add(p);
                }
                f.parts = l.ToArray();
            }
            return f;
        }
    }

    public class TemplatePart
    {
        public int parent = -1;             // index of the part this one is attached to; -1 = the frame
        public string hinge = "";           // path from the frame (or the parent part) to the hinge / collider it sits on, e.g. "parts/hinge_wheel_FL"
        public string prefab = "";          // part prefab (asset root name)
        public float[] hingePos = new float[0];   // hinge local position / euler angles when moved off the prefab's (adjust tool); empty = untouched
        public float[] hingeRot = new float[0];
        public float[] pos = new float[0];        // the part's own local pose under its hinge (plates / spikes placed freely); empty = zero
        public float[] rot = new float[0];
    }

    // Minimal JSON: objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double, strings, bools, null.
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i < s.Length) throw new FormatException("text after the end at " + At(s, i));
            return v;
        }

        private static string At(string s, int i)
        {
            int line = 1; for (int k = 0; k < i && k < s.Length; k++) if (s[k] == '\n') line++;
            return "line " + line;
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') i++;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; }   // // comments allowed
                else break;
            }
        }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of the file");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("expected a \"key\" at " + At(s, i));
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + At(s, i));
                    i++;
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; Ws(s, ref i); if (i < s.Length && s[i] == '}') { i++; return d; } continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("expected ',' or '}' at " + At(s, i));
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; Ws(s, ref i); if (i < s.Length && s[i] == ']') { i++; return l; } continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("expected ',' or ']' at " + At(s, i));
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double num;
            if (i > start && double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out num)) return num;
            throw new FormatException("unexpected '" + c + "' at " + At(s, start));
        }

        private static string Str(string s, ref int i)
        {
            i++;   // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= s.Length) { sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; }
                        break;
                    default: sb.Append(e); break;   // \" \\ \/
                }
            }
            throw new FormatException("unterminated string");
        }

        public static void Str(StringBuilder sb, string v)
        {
            sb.Append('"');
            foreach (char c in v ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        public static string Text(Dictionary<string, object> d, string k, string def)
        {
            object o; return d.TryGetValue(k, out o) && o is string ? (string)o : def;
        }

        public static float Num(Dictionary<string, object> d, string k, float def)
        {
            object o; return d.TryGetValue(k, out o) && o is double ? (float)(double)o : def;
        }

        public static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object o; return d.TryGetValue(k, out o) && o is bool ? (bool)o : def;
        }

        public static float[] Floats(Dictionary<string, object> d, string k)
        {
            object o;
            if (!d.TryGetValue(k, out o) || !(o is List<object>)) return new float[0];
            var l = (List<object>)o;
            var r = new List<float>();
            foreach (var e in l) if (e is double) r.Add((float)(double)e);
            return r.Count == 3 ? r.ToArray() : new float[0];
        }
    }
}

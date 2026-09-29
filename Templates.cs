using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace Apocapatrol
{
    // A car template: frame + parts + crew, one file per template in BepInEx/config/Apocapatrol/Templates/<Name>.cfg
    // (plain "Key = value" lines, # comments). [Build] Template picks one for the spawn key; empty = the [Build]/[Driver]/
    // [Passenger] fields themselves. The park of templates is what a convoy spawner will draw from later.
    internal class CarTemplate
    {
        internal string Name = "";
        internal string Body = "", Wheel = "", Engine = "", Radiator = "", SteeringWheel = "", Seat = "", PassengerSeat = "";
        internal string Driver = "", Passenger = "";
        internal bool FillFuel = true, ReleaseHandbrake = true;

        internal static string Dir { get { return Path.Combine(Path.Combine(Paths.ConfigPath, "Apocapatrol"), "Templates"); } }
        internal static string PathOf(string name) { return Path.Combine(Dir, name + ".cfg"); }

        // The car the spawn key builds: the selected template, or the config fields.
        internal static CarTemplate Current()
        {
            string want = (Plugin.Template.Value ?? "").Trim();
            if (want.Length == 0) return FromConfig();
            var t = Load(want);
            if (t != null) return t;
            Plugin.Log.LogWarning("Template \"" + want + "\" not found in " + Dir + " (available: " + string.Join(", ", List()) + "); using the [Build] fields");
            return FromConfig();
        }

        internal static CarTemplate FromConfig()
        {
            return new CarTemplate
            {
                Name = "(config)",
                Body = Plugin.Body.Value, Wheel = Plugin.Wheel.Value, Engine = Plugin.Engine.Value, Radiator = Plugin.Radiator.Value,
                SteeringWheel = Plugin.SteeringWheel.Value, Seat = Plugin.Seat.Value, PassengerSeat = Plugin.PassengerSeat.Value,
                Driver = Plugin.Driver.Value, Passenger = Plugin.Passenger.Value,
                FillFuel = Plugin.FillFuel.Value, ReleaseHandbrake = Plugin.ReleaseHandbrake.Value
            };
        }

        internal static string[] List()
        {
            try
            {
                if (!Directory.Exists(Dir)) return new string[0];
                var files = Directory.GetFiles(Dir, "*.cfg");
                var names = new List<string>();
                foreach (var f in files) names.Add(Path.GetFileNameWithoutExtension(f));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names.ToArray();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Templates: " + e.Message); return new string[0]; }
        }

        internal static CarTemplate Load(string name)
        {
            string path = PathOf(name);
            if (!File.Exists(path))
            {
                // case-insensitive match
                foreach (var n in List()) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { path = PathOf(n); name = n; break; }
                if (!File.Exists(path)) return null;
            }
            var t = new CarTemplate { Name = name };
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "body": t.Body = value; break;
                        case "wheel": t.Wheel = value; break;
                        case "engine": t.Engine = value; break;
                        case "radiator": t.Radiator = value; break;
                        case "steeringwheel": t.SteeringWheel = value; break;
                        case "seat": t.Seat = value; break;
                        case "passengerseat": t.PassengerSeat = value; break;
                        case "driver": t.Driver = value; break;
                        case "passenger": t.Passenger = value; break;
                        case "fillfuel": t.FillFuel = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                        case "releasehandbrake": t.ReleaseHandbrake = !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                        default: Plugin.Log.LogWarning("Template " + name + ": unknown key " + key); break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Template " + name + ": " + e.Message); return null; }
            if (t.Body.Length == 0) { Plugin.Log.LogWarning("Template " + name + " has no Body"); return null; }
            return t;
        }

        internal bool Save(string name)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# Apocapatrol car template \"" + name + "\" (frame + parts + crew). Prefab names or in-game item names; empty = no part.");
                sb.AppendLine("# Select it with [Build] Template = " + name);
                sb.AppendLine("Body = " + Body);
                sb.AppendLine("Wheel = " + Wheel);
                sb.AppendLine("Engine = " + Engine);
                sb.AppendLine("Radiator = " + Radiator);
                sb.AppendLine("SteeringWheel = " + SteeringWheel);
                sb.AppendLine("Seat = " + Seat);
                sb.AppendLine("PassengerSeat = " + PassengerSeat);
                sb.AppendLine("Driver = " + Driver);
                sb.AppendLine("Passenger = " + Passenger);
                sb.AppendLine("FillFuel = " + (FillFuel ? "true" : "false"));
                sb.AppendLine("ReleaseHandbrake = " + (ReleaseHandbrake ? "true" : "false"));
                File.WriteAllText(PathOf(name), sb.ToString());
                Plugin.Log.LogInfo("Template \"" + name + "\" saved: " + Describe());
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Template save " + name + ": " + e.Message); return false; }
        }

        internal string Describe()
        {
            return Body + " / " + Wheel + " / " + Engine + " / " + Radiator + " / " + SteeringWheel + " / seats " + Seat + " + " + PassengerSeat
                + " / driver " + (Driver.Length > 0 ? Driver : "-") + " / passenger " + (Passenger.Length > 0 ? Passenger : "-");
        }

        // Templates shipped with the mod: written once when missing, never overwritten (edit the files freely).
        internal static void EnsureBuiltins()
        {
            if (!File.Exists(PathOf("PipeRat_Basic")))
                new CarTemplate
                {
                    Body = "PipeRat", Wheel = "small_wheel_1", Engine = "1.2L I4 59HP 87Nm Gasoline", Radiator = "radiator_small",
                    SteeringWheel = "steeringwheel_7", Seat = "poloska_seat_front_homemade", PassengerSeat = "poloska_seat_front_homemade",
                    Driver = "Scraffa", Passenger = "Sprokka"
                }.Save("PipeRat_Basic");
        }

        // F9: the current [Build]/[Driver]/[Passenger] fields become a template named [Build] SaveAs (or Body_Custom).
        internal static void SaveCurrentFromConfig()
        {
            string name = (Plugin.SaveTemplateAs.Value ?? "").Trim();
            if (name.Length == 0) name = Plugin.Body.Value + "_Custom";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var t = FromConfig();
            if (t.Save(name)) Plugin.Log.LogInfo("Templates now: " + string.Join(", ", List()));
        }
    }
}

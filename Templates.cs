using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // A car template: frame + parts + crew. The park of templates is hardcoded below; [Build] Template picks one by name for
    // the spawn key (empty = the [Build]/[Driver]/[Passenger] fields themselves). A convoy spawner will draw from the park.
    // What the driving AI runs into on purpose. Everything below the chosen level is avoided like any obstacle:
    // None = rams nothing (drives past the player / their car in drive-by runs; creatures and cars are obstacles),
    // Pedestrians = runs over the player on foot and creatures, avoids cars (drive-bys on a driving player),
    // Cars = rams the player's car and other vehicles too (and, being that heavy, pedestrians as well).
    internal enum RamTargets { None, Pedestrians, Cars }

    internal class CarTemplate
    {
        internal string Name = "";
        internal RamTargets Rams = RamTargets.Pedestrians;
        internal string Body = "", Wheel = "", Engine = "", Radiator = "", SteeringWheel = "", Exhaust = "", Seat = "", PassengerSeat = "";
        internal string Driver = "", Passenger = "";
        internal string RearWheel = "";       // wheel item for hinge_wheel_RL/RR; "" = the same as Wheel
        internal string[] Bumpers = null;     // front bumper rolled per build from these ("" = none); null = never a bumper
        internal bool FillFuel = true, ReleaseHandbrake = true;
        internal float FuelLitres = -1f;

        // JSON car templates (CarTemplates.cs; written by the Apocatemplater dumper). Parts != null = this exact part list is
        // attached hinge by hinge instead of the Wheel/Engine/... fields above.
        internal TemplatePart[] Parts;
        internal string Kind = "";            // as written: "small", "small;motorcycle" ...; "" = by body / cargo (the built-in rule)
        internal string[] Kinds = new string[0];   // Kind split on ';' / ',' / spaces, lower case, no duplicates (CarTemplates.ParseKinds)
        internal bool Spawns = true;          // false: spawner menu only, never in a patrol / convoy
        internal float Weight = 1f;           // relative pick chance among the templates a patrol slot accepts
        internal string LootPreset = "", SourcePath = "";
        internal bool IsDefault, Favorite;
        internal Dictionary<string, int> SpawnLoot;
        internal string SpawnCargoKey;
        internal string[] SpawnCargoOptions;
        internal CarTemplate CloneForSpawn() { return (CarTemplate)MemberwiseClone(); }
        internal string Origin = "built-in";  // built-in | embedded | file (for the log and the spawner menu)

        // the spawn roles (Convoy picks, spawner menu column)
        // A template may have several kinds ("small;motorcycle"): it counts as each of them. Without any kind the built-in rule decides:
        // truck = Rust* body, junker = Junker body, small = everything else.
        internal bool HasKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            if (Kinds != null && Kinds.Length > 0) return Array.IndexOf(Kinds, kind.Trim().ToLowerInvariant()) >= 0;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "truck": return (Body ?? "").StartsWith("Rust", StringComparison.OrdinalIgnoreCase);
                case "medium": case "junker": return string.Equals(Body, "Junker", StringComparison.OrdinalIgnoreCase);
                case "small": return !HasKind("truck") && !HasKind("medium");
                default: return false;
            }
        }
        internal bool IsTruck { get { return HasKind("truck"); } }
        internal bool IsJunker { get { return HasKind("medium") || HasKind("junker"); } }   // the medium car role ("junker" still read from older files)
        internal bool IsMotorcycle { get { return HasKind("motorcycle"); } }
        internal bool IsSmall { get { return HasKind("small"); } }
        internal string KindLabel { get { return Kinds != null && Kinds.Length > 0 ? string.Join(";", Kinds) : (IsTruck ? "truck" : IsJunker ? "medium" : "small"); } }

        // front bumper rolls (Denis): small cars a third each nothing / bumper_8 / bumper_3; junkers nothing / junker_bumper_front / bumper_12;
        // trucks always one of bumper_12..15
        internal static readonly string[] SmallBumpers = { "", "bumper_8", "bumper_3" };
        internal static readonly string[] JunkerBumpers = { "", "junker_bumper_front", "bumper_12" };
        internal static readonly string[] TruckBumpers = { "bumper_12", "bumper_13", "bumper_14", "bumper_15" };

        internal string RollBumper()
        {
            if (Bumpers == null || Bumpers.Length == 0) return "";
            return Bumpers[UnityEngine.Random.Range(0, Bumpers.Length)] ?? "";
        }

        internal CarTemplate() { }

        internal CarTemplate(string name, string body, string wheel, string engine, string radiator, string steeringWheel, string exhaust,
            string seat, string passengerSeat, string driver, string passenger, RamTargets rams)
        {
            Name = name; Body = body; Wheel = wheel; Engine = engine; Radiator = radiator; SteeringWheel = steeringWheel; Exhaust = exhaust;
            Seat = seat; PassengerSeat = passengerSeat; Driver = driver; Passenger = passenger; Rams = rams;
        }

        // ------------------------------------------------------------ THE PARK
        // name, body, wheel, engine, radiator, steering wheel, driver seat, passenger seat, driver, passenger, ramsTargets
        // (prefab names or in-game item names; "" = no part / nobody)
        internal static readonly CarTemplate FallbackCar = new CarTemplate("Basic PipeRat", "PipeRat", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust", "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians) { LootPreset = "car-empty", IsDefault = true, Favorite = true };
        internal static readonly CarTemplate FallbackTruck = new CarTemplate("Empty truck", "Rustcargo", "truck_wheel_1", "5.8L I6 120HP 356Nm Diesel", "radiator_truck", "steeringwheel_3", "exhaust_single", "rustallion_seat_front", "rustallion_seat_front", "Scraffa", "Flexa", RamTargets.Cars) { RearWheel = "truck_wheel_2", LootPreset = "truck-empty", IsDefault = true, Favorite = true };
        internal static readonly CarTemplate[] Builtin = { FallbackCar, FallbackTruck };

        // Park = the built-ins above + every JSON template (CarTemplates.Rebuild; a JSON template with a built-in's name replaces it).
        // Declared after Builtin: static initialisers run in source order.
        internal static CarTemplate[] Park = Builtin;

        internal static RamTargets ParseRams(string s)
        {
            if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) return RamTargets.None;
            if (string.Equals(s, "Cars", StringComparison.OrdinalIgnoreCase)) return RamTargets.Cars;
            return RamTargets.Pedestrians;
        }

        internal static string[] Names()
        {
            var names = new string[Park.Length];
            for (int i = 0; i < Park.Length; i++) names[i] = Park[i].Name;
            return names;
        }

        internal static CarTemplate Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = name.Trim();
            foreach (var t in Park) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        internal string Describe()
        {
            if (Parts != null)
                return Body + " / " + Parts.Length + " part(s) (" + Origin + ") / driver " + (Driver.Length > 0 ? Driver : "-") + " / passenger " + (Passenger.Length > 0 ? Passenger : "-")
                    + " / rams " + Rams + " / " + KindLabel
                    ;
            return Body + " / " + Wheel + (RearWheel.Length > 0 ? " + rear " + RearWheel : "") + " / " + Engine + " / " + Radiator + " / " + SteeringWheel + " / " + (Exhaust.Length > 0 ? Exhaust : "no exhaust") + " / seats " + Seat + " + " + PassengerSeat
                + " / driver " + (Driver.Length > 0 ? Driver : "-") + " / passenger " + (Passenger.Length > 0 ? Passenger : "-") + " / rams " + Rams
                ;
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }
    }
}

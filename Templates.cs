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
        internal string Cargo = "";           // "Random" = a loot type rolled by the [Loot] chances, or a fixed [Loot] key ("Food", ...); "" = empty bed
        internal float LootScaleMin = 1f, LootScaleMax = 1f;   // per-spawn loot amount factor rolled in this range (on top of [Loot] Multiplier)
        internal bool FillFuel = true, ReleaseHandbrake = true;

        // JSON car templates (CarTemplates.cs; written by the Apocatemplater dumper). Parts != null = this exact part list is
        // attached hinge by hinge instead of the Wheel/Engine/... fields above.
        internal TemplatePart[] Parts;
        internal string Kind = "";            // small | junker | truck; "" = by body / cargo (the built-in rule)
        internal string Tier = "";            // basic | advanced; "" = by name
        internal bool Spawns = true;          // false: spawner menu only, never in a patrol / convoy
        internal float Weight = 1f;           // relative pick chance among the templates a patrol slot accepts
        internal string Origin = "built-in";  // built-in | embedded | file (for the log and the spawner menu)

        // the spawn roles (Convoy picks, spawner menu column)
        internal bool IsTruck { get { return Kind.Length > 0 ? Kind.Equals("truck", StringComparison.OrdinalIgnoreCase) : (Body ?? "").StartsWith("Rust", StringComparison.OrdinalIgnoreCase) || Cargo.Length > 0; } }
        internal bool IsJunker { get { return Kind.Length > 0 ? Kind.Equals("junker", StringComparison.OrdinalIgnoreCase) : string.Equals(Body, "Junker", StringComparison.OrdinalIgnoreCase); } }
        internal bool IsAdvanced { get { return Tier.Length > 0 ? Tier.Equals("advanced", StringComparison.OrdinalIgnoreCase) : Name.IndexOf("Advanced", StringComparison.OrdinalIgnoreCase) >= 0; } }
        internal bool IsBasic { get { return Tier.Length > 0 ? Tier.Equals("basic", StringComparison.OrdinalIgnoreCase) : Name.EndsWith("_Basic", StringComparison.OrdinalIgnoreCase) || (!IsAdvanced && IsTruck); } }

        // loot types: key, item spec ("prefab:count;prefab:min-max;..."; "@weapons" = rolled by Cargo.WeaponsSpec), default chance (%)
        // (chances: the original 25/20/15/15/15/10 + Diesel 15, Mechanic 10, Corpses 10, Rats 5 = 140, normalised to 100 and rounded to whole numbers:
        //  18/14/11/11/11/11/7/7/7/3 = 100 — whole numbers so the default parses the same under every regional number format)
        internal static readonly string[][] LootDefaults =
        {
            new[] { "Food", "dogfood_can:6-10", "18" },
            new[] { "Water", "Water_Can_Plastic:4;Water_Barrel:1@50", "14" },
            new[] { "Gasoline", "Gasoline_Can:4;Gasoline_Barrel:1@50", "11" },
            new[] { "Diesel", "Diesel_Can:4;Diesel_Barrel:1@50", "11" },
            new[] { "Medicine", "bandage_1:5;first_aid_1:2-3", "11" },
            new[] { "Weapons", "@weapons", "11" },
            new[] { "Drugs", "alcohol_canister:2;weed_bag:3;plant_weed:1", "7" },
            new[] { "Mechanic", "Repairbox_Small|Repairbox_Medium|Repairbox_Large:3;MotorOil_Can_Big:1;"
                + "5.2L V8 230HP 340Nm Gasoline|7L V8 355HP 569Nm Gasoline|8.2L V8 400HP 746Nm Gasoline|18L V8 335HP 1700Nm Diesel:1@15/30#100", "7" },
            new[] { "Corpses", "Scraffa_Dead:3-5", "7" },
            new[] { "Rats", "Rat_Dead:6-8", "3" },
        };

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
        internal static readonly CarTemplate[] Builtin =
        {
            // name, body, wheel, engine, radiator, steering wheel, exhaust, driver seat, passenger seat, driver, passenger, ramsTargets
            new CarTemplate("PipeRat_Basic", "PipeRat", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians) { Bumpers = SmallBumpers },
            new CarTemplate("PipeRat_Advanced", "PipeRat", "small_wheel_2", "2.8L V6 115HP 183Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Lugnut", RamTargets.Pedestrians) { Bumpers = SmallBumpers },
            new CarTemplate("Poloska_Basic", "Poloska", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Boltjaw", RamTargets.Pedestrians) { Bumpers = SmallBumpers },
            new CarTemplate("Poloska_Advanced", "Poloska", "small_wheel_1", "2.8L V6 115HP 183Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians) { Bumpers = SmallBumpers },
            new CarTemplate("TinyTyrant_Basic", "TinyTyrant", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians) { Bumpers = SmallBumpers },
            new CarTemplate("TinyTyrant_Advanced", "TinyTyrant", "small_wheel_2", "2.8L V6 115HP 183Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Lugnut", RamTargets.Pedestrians) { Bumpers = SmallBumpers },
            new CarTemplate("Junker_Basic", "Junker", "small_wheel_1", "2.3L I4 89HP 165Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Sprokka", "Lugnut", RamTargets.Pedestrians) { Bumpers = JunkerBumpers },
            new CarTemplate("Junker_Advanced", "Junker", "small_wheel_1", "2.3L I4 89HP 165Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Sprokka", "Flexa", RamTargets.Pedestrians) { Bumpers = JunkerBumpers },
            new CarTemplate("Rustcargo_Basic", "Rustcargo", "truck_wheel_1", "5.8L I6 120HP 356Nm Diesel", "radiator_truck", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Scraffa", "Flexa", RamTargets.Cars) { RearWheel = "truck_wheel_2", Bumpers = TruckBumpers },
            new CarTemplate("Rustcargo_Advanced", "Rustcargo", "truck_wheel_1_armored", "7L I6 165HP 542Nm Diesel", "radiator_truck_big", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Sprokka", "Flexa", RamTargets.Cars) { RearWheel = "truck_wheel_2_armored", Bumpers = TruckBumpers },
            // the loot truck = Rustcargo_Basic with a loaded bed; the load is rolled by the [Loot] XChance weights, items and amounts in [Loot]
            new CarTemplate("Rustcargo_Loot", "Rustcargo", "truck_wheel_1", "5.8L I6 120HP 356Nm Diesel", "radiator_truck", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Scraffa", "Flexa", RamTargets.Cars) { Cargo = "Random", RearWheel = "truck_wheel_2", Bumpers = TruckBumpers },
            new CarTemplate("Rustcargo_Loot_Advanced", "Rustcargo", "truck_wheel_1_armored", "7L I6 165HP 542Nm Diesel", "radiator_truck_big", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Sprokka", "Flexa", RamTargets.Cars) { Cargo = "Random", LootScaleMin = 1.5f, LootScaleMax = 2f, RearWheel = "truck_wheel_2_armored", Bumpers = TruckBumpers },
        };

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
                    + " / rams " + Rams + " / " + (IsTruck ? "truck" : IsJunker ? "junker" : "small") + " " + (IsAdvanced ? "advanced" : "basic") + (Spawns ? "" : " (menu only)")
                    + (Cargo.Length > 0 ? " / loot " + Cargo : "");
            return Body + " / " + Wheel + (RearWheel.Length > 0 ? " + rear " + RearWheel : "") + " / " + Engine + " / " + Radiator + " / " + SteeringWheel + " / " + (Exhaust.Length > 0 ? Exhaust : "no exhaust") + " / seats " + Seat + " + " + PassengerSeat
                + " / driver " + (Driver.Length > 0 ? Driver : "-") + " / passenger " + (Passenger.Length > 0 ? Passenger : "-") + " / rams " + Rams
                + (Cargo.Length > 0 ? " / loot " + Cargo : "");
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }
    }
}

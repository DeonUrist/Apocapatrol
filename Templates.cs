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
        internal string Cargo = "";           // "Random" = a loot type rolled by the [Loot] chances, or a fixed [Loot] key ("Food", ...); "" = empty bed
        internal float LootScaleMin = 1f, LootScaleMax = 1f;   // per-spawn loot amount factor rolled in this range (on top of [Loot] Multiplier)
        internal bool FillFuel = true, ReleaseHandbrake = true;

        // loot types: key, item spec ("prefab:count;prefab:min-max;..."; "@weapons" = rolled by Cargo.WeaponsSpec), default chance (%)
        internal static readonly string[][] LootDefaults =
        {
            new[] { "Food", "dogfood_can:6", "25" },
            new[] { "Gasoline", "Gasoline_Can:4", "15" },
            new[] { "Water", "Water_Can_Plastic:4", "20" },
            new[] { "Medicine", "bandage_1:4;first_aid_1:2", "15" },
            new[] { "Drugs", "alcohol_canister:2;weed_bag:3;plant_weed:1", "10" },
            new[] { "Weapons", "@weapons", "15" },
        };

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
        internal static readonly CarTemplate[] Park =
        {
            // name, body, wheel, engine, radiator, steering wheel, exhaust, driver seat, passenger seat, driver, passenger, ramsTargets
            new CarTemplate("PipeRat_Basic", "PipeRat", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians),
            new CarTemplate("PipeRat_Advanced", "PipeRat", "small_wheel_2", "2.8L V6 115HP 183Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Spanna", "Lugnut", RamTargets.Pedestrians),
            new CarTemplate("Poloska_Basic", "Poloska", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Boltjaw", RamTargets.Pedestrians),
            new CarTemplate("Poloska_Advanced", "Poloska", "small_wheel_1", "2.8L V6 115HP 183Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians),
            new CarTemplate("TinyTyrant_Basic", "TinyTyrant", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka", RamTargets.Pedestrians),
            new CarTemplate("TinyTyrant_Advanced", "TinyTyrant", "small_wheel_2", "2.8L V6 115HP 183Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Spanna", "Lugnut", RamTargets.Pedestrians),
            new CarTemplate("Junker_Basic", "Junker", "small_wheel_1", "2.3L I4 89HP 165Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Sprokka", "Lugnut", RamTargets.Pedestrians),
            new CarTemplate("Junker_Advanced", "Junker", "small_wheel_1", "2.3L I4 89HP 165Nm Gasoline", "Medium Radiator", "steeringwheel_7", "poloska_exhaust",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Sprokka", "Flexa", RamTargets.Pedestrians),
            new CarTemplate("Rustcargo_Basic", "Rustcargo", "truck_wheel_1", "5.8L I6 120HP 356Nm Diesel", "radiator_truck", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Spanna", "Flexa", RamTargets.Cars),
            new CarTemplate("Rustcargo_Advanced", "Rustcargo", "truck_wheel_2_armored", "7L I6 165HP 542Nm Diesel", "radiator_truck_big", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Sprokka", "Flexa", RamTargets.Cars),
            // the loot truck = Rustcargo_Basic with a loaded bed; the load is rolled by the [Loot] XChance weights, items and amounts in [Loot]
            new CarTemplate("Rustcargo_Loot", "Rustcargo", "truck_wheel_1", "5.8L I6 120HP 356Nm Diesel", "radiator_truck", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Spanna", "Flexa", RamTargets.Cars) { Cargo = "Random" },
            new CarTemplate("Rustcargo_Loot_Advanced", "Rustcargo", "truck_wheel_2_armored", "7L I6 165HP 542Nm Diesel", "radiator_truck_big", "steeringwheel_3", "exhaust_single",
                "rustallion_seat_front", "rustallion_seat_front", "Sprokka", "Flexa", RamTargets.Cars) { Cargo = "Random", LootScaleMin = 1.5f, LootScaleMax = 2f },
        };

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
            return Body + " / " + Wheel + " / " + Engine + " / " + Radiator + " / " + SteeringWheel + " / " + (Exhaust.Length > 0 ? Exhaust : "no exhaust") + " / seats " + Seat + " + " + PassengerSeat
                + " / driver " + (Driver.Length > 0 ? Driver : "-") + " / passenger " + (Passenger.Length > 0 ? Passenger : "-") + " / rams " + Rams
                + (Cargo.Length > 0 ? " / loot " + Cargo : "");
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }
    }
}

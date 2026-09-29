using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // A car template: frame + parts + crew. The park of templates is hardcoded below; [Build] Template picks one by name for
    // the spawn key (empty = the [Build]/[Driver]/[Passenger] fields themselves). A convoy spawner will draw from the park.
    internal class CarTemplate
    {
        internal string Name = "";
        internal string Body = "", Wheel = "", Engine = "", Radiator = "", SteeringWheel = "", Seat = "", PassengerSeat = "";
        internal string Driver = "", Passenger = "";
        internal bool FillFuel = true, ReleaseHandbrake = true;

        internal CarTemplate() { }

        internal CarTemplate(string name, string body, string wheel, string engine, string radiator, string steeringWheel,
            string seat, string passengerSeat, string driver, string passenger)
        {
            Name = name; Body = body; Wheel = wheel; Engine = engine; Radiator = radiator; SteeringWheel = steeringWheel;
            Seat = seat; PassengerSeat = passengerSeat; Driver = driver; Passenger = passenger;
        }

        // ------------------------------------------------------------ THE PARK
        // name, body, wheel, engine, radiator, steering wheel, driver seat, passenger seat, driver, passenger
        // (prefab names or in-game item names; "" = no part / nobody)
        internal static readonly CarTemplate[] Park =
        {
            new CarTemplate("PipeRat_Basic", "PipeRat", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka"),
            new CarTemplate("PipeRat_Advanced", "PipeRat", "small_wheel_2", "2.8L V6 115HP 183Nm Gasoline", "Medium Radiator", "steeringwheel_7",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Spanna", "Lugnut"),
            new CarTemplate("Poloska_Basic", "Poloska", "small_wheel_1", "1.2L I4 59HP 87Nm Gasoline", "radiator_small", "steeringwheel_7",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Boltjaw"),
            new CarTemplate("Poloska_Advanced", "Poloska", "small_wheel_1", "2.8L V6 115HP 183Nm Gasoline", "radiator_small", "steeringwheel_7",
                "poloska_seat_front_homemade", "poloska_seat_front_homemade", "Scraffa", "Sprokka"),
        };

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

        // The car the spawn key builds: the selected template, or the config fields.
        internal static CarTemplate Current()
        {
            string want = (Plugin.Template.Value ?? "").Trim();
            if (want.Length == 0) return FromConfig();
            var t = Find(want);
            if (t != null) return t;
            Plugin.Log.LogWarning("Template \"" + want + "\" is not in the park (" + string.Join(", ", Names()) + "); using the [Build] fields");
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

        internal string Describe()
        {
            return Body + " / " + Wheel + " / " + Engine + " / " + Radiator + " / " + SteeringWheel + " / seats " + Seat + " + " + PassengerSeat
                + " / driver " + (Driver.Length > 0 ? Driver : "-") + " / passenger " + (Passenger.Length > 0 ? Passenger : "-");
        }

        // F9: print the current [Build]/[Driver]/[Passenger] fields as a park entry (paste it into Park above).
        internal static void PrintCurrentAsParkEntry()
        {
            var t = FromConfig();
            string name = (Plugin.TemplateName.Value ?? "").Trim();
            if (name.Length == 0) name = Plugin.Body.Value + "_Custom";
            string line = "new CarTemplate(" + Q(name) + ", " + Q(t.Body) + ", " + Q(t.Wheel) + ", " + Q(t.Engine) + ", " + Q(t.Radiator) + ", "
                + Q(t.SteeringWheel) + ", " + Q(t.Seat) + ", " + Q(t.PassengerSeat) + ", " + Q(t.Driver) + ", " + Q(t.Passenger) + "),";
            Plugin.Log.LogInfo("Park entry for the current fields (Templates.cs, Park[]):\n            " + line);
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }
    }
}

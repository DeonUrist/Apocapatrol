using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed partial class WorldTraffic
    {
        private static string PathOf(Transform root, Transform node)
        {
            if (root == node) return "";
            var segments = new List<string>();
            while (node != null && node != root)
            {
                string name = TemplateExporter.PrefabName(node.name); int ordinal = 0;
                for (int i = 0; node.parent != null && i < node.GetSiblingIndex(); i++)
                    if (TemplateExporter.PrefabName(node.parent.GetChild(i).name) == name) ordinal++;
                segments.Add(name + "#" + ordinal); node = node.parent;
            }
            segments.Reverse(); return string.Join("/", segments.ToArray());
        }

        private static Transform Resolve(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var node = root;
            foreach (var segment in path.Split('/'))
            {
                int hash = segment.LastIndexOf('#');
                if (hash <= 0 || !int.TryParse(segment.Substring(hash + 1), out int wanted)) return null;
                string name = segment.Substring(0, hash); Transform next = null;
                for (int i = 0, seen = 0; i < node.childCount; i++)
                {
                    var child = node.GetChild(i);
                    if (TemplateExporter.PrefabName(child.name) != name) continue;
                    if (seen++ == wanted) { next = child; break; }
                }
                if (next == null) return null;
                node = next;
            }
            return node;
        }

        private static string[] Parts(GameObject car)
        {
            var result = new List<string>();
            foreach (var t in car.GetComponentsInChildren<Transform>(true))
                if (t.CompareTag("vehPart")) result.Add(PathOf(car.transform, t));
            var trailer = TrailerSupport.Attached(car);
            if (trailer != null) foreach (var t in trailer.GetComponentsInChildren<Transform>(true))
                if (t.CompareTag("vehPart")) result.Add("trailer:" + PathOf(trailer.transform, t));
            return result.OrderBy(s => s).ToArray();
        }

        private static bool CanVirtualize(GameObject car, TrafficVehicle record)
        {
            // Missing/looted parts or a detached trailer are left as real salvage; never rebuild them from a pristine template.
            return record.partPaths == null || record.partPaths.SequenceEqual(Parts(car));
        }

        private static void CaptureState(GameObject car, TrafficVehicle record)
        {
            var marker = car.GetComponent<PatrolMarker>(); if (marker == null) return;
            record.crew = marker.Snapshot();
            if (record.partPaths == null) record.partPaths = Parts(car);
            var values = new List<TrafficValue>();
            CaptureValues(car, "", values);
            var trailer = TrailerSupport.Attached(car); if (trailer != null) CaptureValues(trailer, "trailer:", values);
            record.values = values.ToArray();
            record.turrets = new string[7]; record.turretHealth = new float[7];
            for (int i = 0; i < marker.Riders.Length; i++)
            {
                var rider = marker.Riders[i];
                float health = rider != null ? Patrol.GetHealth(rider) : 0f;
                record.turrets[i] = rider != null && health != 0f ? TemplateExporter.PrefabName(rider.name.Replace("(Rider)", "")) : "";
                record.turretHealth[i] = health;
            }
        }

        private static void CaptureValues(GameObject car, string prefix, List<TrafficValue> values)
        {
            foreach (var fsm in car.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                string variable = fsm.FsmName == "Condition" || fsm.FsmName == "Repair" ? "Condition"
                    : fsm.FsmName == "LiquidAmount" ? "Liquid" : null;
                if (variable == null) continue;
                var v = fsm.FsmVariables.GetFsmFloat(variable); if (v == null) continue;
                values.Add(new TrafficValue { path = prefix + PathOf(car.transform, fsm.transform), fsm = fsm.FsmName, variable = variable, value = v.Value });
            }
        }

        private static void ApplyState(GameObject car, TrafficVehicle record)
        {
            var marker = car.GetComponent<PatrolMarker>();
            if (record.crew != null)
            {
                marker.DriverDied=record.crew.driverDied;marker.DriverBailed=record.crew.driverBailed;
                marker.ExplodeRolled=record.crew.explodeRolled;marker.ExplodeSelected=record.crew.explodeSelected;
                var driver = car.GetComponent<Crew>()?.Driver;
                if (driver != null && record.crew.driverHealth >= 0f)
                { Patrol.SetHealth(driver, record.crew.driverHealth); driver.AddComponent<Patrol.LateHealth>().Value = record.crew.driverHealth; }
                if (marker.Passenger != null && record.crew.passengerHealth >= 0f)
                { Patrol.SetHealth(marker.Passenger, record.crew.passengerHealth); marker.Passenger.AddComponent<Patrol.LateHealth>().Value = record.crew.passengerHealth; }
            }
            foreach (var value in record.values ?? new TrafficValue[0])
            {
                bool trailer = value.path.StartsWith("trailer:", StringComparison.Ordinal);
                var root = trailer ? TrailerSupport.Attached(car) : car;
                var t = root != null ? Resolve(root.transform, trailer ? value.path.Substring(8) : value.path) : null;
                if (t == null) continue;
                foreach (var fsm in t.GetComponents<PlayMakerFSM>())
                    if (fsm.FsmName == value.fsm) { var v = fsm.FsmVariables.GetFsmFloat(value.variable); if (v != null) v.Value = value.value; }
            }
            if (record.turretHealth != null)
                for (int i = 0; i < marker.Riders.Length && i < record.turretHealth.Length; i++)
                    if (marker.Riders[i] != null && record.turretHealth[i] >= 0f) Patrol.SetHealth(marker.Riders[i], record.turretHealth[i]);
            // Record the original structural signature and rolled health/fluids after first construction.
            if (record.partPaths == null) CaptureState(car, record);
        }
    }
}

using System;
using UnityEngine;

namespace Apocapatrol
{
    // Saveable world records contain no Unity object references. Physical objects are only a nearby representation.
    [Serializable] public sealed class TrafficBase
    {
        public string id, name;
        public Vector3 center, gate, apron;
        public Vector3 size;
        public bool mappedEntrance;
        public bool cleared;
        public string faction="Scrapyard";
    }

    [Serializable] public sealed class TrafficPoi
    { public string id,name; public Vector3 position; }

    [Serializable] public sealed class TrafficValue
    {
        public string path, fsm, variable;
        public float value;
    }

    [Serializable] public sealed class TrafficVehicle
    {
        public string faction="Scrapyard";
        public int strength=2;
        public string template, definition;
        public string[] merchantTypes;public string merchantGroup;
        public bool truck, retired;
        public string[] lootNames = new string[0];
        public int[] lootCounts = new int[0];
        public string cargoKey;
        public PatrolCarData crew;
        public TrafficValue[] values = new TrafficValue[0];
        public string[] turrets;
        public float[] turretHealth;
        public string[] partPaths;
    }

    [Serializable] public sealed class TrafficJourney
    {
        public string faction="Scrapyard",battle="";
        public string id, name, source, destination;
        public bool convoy, arrived, roaming,merchant,freeRoute;
        public float waitSeconds;
        internal bool TerrainRoute;
        public Vector3 position;
        public Vector3[] route = new Vector3[0];
        public bool[] roadLegs = new bool[0];
        public int leg = 1;
        public float speed, destinationSeconds, unseenSeconds;
        public TrafficVehicle[] vehicles = new TrafficVehicle[0];

        internal Vector3 Heading
        {
            get
            {
                var d = route.Length > 1 ? route[Mathf.Clamp(leg, 1, route.Length - 1)] - position : Vector3.forward;
                d.y = 0f;
                return d.sqrMagnitude > 0.01f ? d.normalized : Vector3.forward;
            }
        }

        internal void Advance(float seconds)
        {
            if (arrived || route.Length < 2) return;
            float remaining = Mathf.Max(0f, RoadPath.Speed(route, leg, position, speed) * seconds);
            while (leg < route.Length)
            {
                var delta = route[leg] - position; delta.y = 0f;
                float distance = delta.magnitude;
                if (distance > remaining && distance > 0.001f)
                { position += delta / distance * remaining; return; }
                position = route[leg++]; remaining -= distance;
            }
            arrived = true; leg = route.Length - 1;
        }

        // Project real progress only onto the current leg: an attack detour cannot skip the remaining route.
        internal void Follow(Vector3 actual, float reach)
        {
            position = actual;
            if (arrived || route.Length < 2) return;
            var end = route[Mathf.Clamp(leg, 1, route.Length - 1)];
            var delta = end - actual; delta.y = 0f;
            if (leg < route.Length - 1)
            {
                var start = route[leg - 1]; var direction = end - start; direction.y = 0f;
                var flatActual = actual - start; flatActual.y = 0f;
                float t = direction.sqrMagnitude > 0.001f ? Vector3.Dot(flatActual, direction) / direction.sqrMagnitude : 1f;
                var projected = start + direction * Mathf.Clamp01(t);
                bool passed = t >= 1f && TrafficLocations.FlatDistance(actual, projected) < 5f;
                if (!passed && delta.magnitude > Mathf.Min(reach, direction.magnitude * 0.35f)) return;
            }
            else if (delta.magnitude > reach) return;
            if (++leg >= route.Length) { arrived = true; leg = route.Length - 1; }
        }

        internal bool Expire(float seconds, bool playerNearby, float grace)
        {
            if (!arrived) return false;
            destinationSeconds += seconds;
            unseenSeconds = playerNearby ? 0f : unseenSeconds + seconds;
            return unseenSeconds >= grace;
        }
    }

    [Serializable] public sealed class TrafficSave
    {
        public int version = 2; // 2 = MapMagic-local world coordinates; 1 = scene coordinates
        public TrafficBase[] bases = new TrafficBase[0];
        public TrafficPoi[] pois = new TrafficPoi[0];
        public TrafficJourney[] journeys = new TrafficJourney[0];
        public TrafficBattle[] battles=new TrafficBattle[0];
    }

    internal static class TrafficRules
    {
        internal static float VisibilityRange(bool night, bool lights) { return !night || lights ? 500f : 0f; }
        internal static int Population(float squareKm, float density, int cap)
        { return Mathf.Min(cap, Mathf.CeilToInt(Mathf.Max(0f, squareKm * density))); }

        internal static float EngineRange(float hp) { return Mathf.Max(0f,hp); }

        internal static bool Pursue(bool convoy, bool truck, float distance, float guardRange, bool detected, bool remembered)
        { return convoy ? !truck && distance <= guardRange : detected || remembered; }
    }
}

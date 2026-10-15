using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Apocapatrol
{
    // Optional, read-only adapter to the existing NPC Overhaul bake. No assembly dependency or second bake.
    // Discovery also works without NPC Overhaul; unloaded roots never delete a remembered location.
    internal static class TrafficLocations
    {
        internal static readonly Dictionary<string, TrafficBase> Known = new Dictionary<string, TrafficBase>();
        internal static readonly Dictionary<string, TrafficPoi> Points = new Dictionary<string, TrafficPoi>();
        private static readonly Dictionary<string, Transform> Roots = new Dictionary<string, Transform>();
        private static readonly HashSet<string> Mapped = new HashSet<string>();
        private static readonly HashSet<string> ExaminedPoints = new HashSet<string>();
        private static readonly Stack<Transform> Scan = new Stack<Transform>();
        private sealed class MapRequest { internal Transform Root; internal Bounds? Box; internal object Structure; }
        private static readonly Queue<MapRequest> MapQueue=new Queue<MapRequest>();
        private static readonly HashSet<Transform> QueuedMaps=new HashSet<Transform>();
        private static float _nextScan, _nextBake;
        private static FieldInfo _structures;
        internal static int Revision { get; private set; }
        private static bool _discoveryReady;
        internal static bool DiscoveryReady=>_discoveryReady;
        internal static void Changed(){Revision++;}
        internal static int NearbyCount(Vector3 player,float range)
        {
            int count=0;foreach(var camp in Known.Values)
                if(camp.faction!="Coyotes"&&!camp.cleared&&TrafficCamps.Loaded(camp.id)&&FlatDistance(camp.center,player)<=range)count++;
            return count;
        }
        internal static TrafficBase[] NearbyBases(Vector3 player,float range)
        {return Known.Values.Where(c=>c.faction!="Coyotes"&&!c.cleared&&TrafficCamps.Loaded(c.id)&&FlatDistance(c.center,player)<=range).ToArray();}
        internal static Bounds[] Blocks() => Known.Values.Select(c=>new Bounds(new Vector3(c.center.x,0f,c.center.z),new Vector3(c.size.x+16f,10000f,c.size.z+16f))).ToArray();
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        internal static void Reset()
        { Known.Clear(); Points.Clear(); ExaminedPoints.Clear(); Roots.Clear(); Mapped.Clear(); Scan.Clear(); MapQueue.Clear();QueuedMaps.Clear();_nextScan = _nextBake = 0f; _structures = null;_discoveryReady=false; Revision++; }

        internal static void Tick()
        {
            if (Scan.Count == 0 && Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 15f;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                { var scene = SceneManager.GetSceneAt(i); if (scene.isLoaded) foreach (var root in scene.GetRootGameObjects()) Scan.Push(root.transform); }
            }
            bool remembered=false;
            for (int i = 0; i < 200 && Scan.Count > 0; i++)
            {
                var t = Scan.Pop(); if (t == null) continue;
                if(t.parent==null)TrafficCamps.ObserveRoot(t);
                RememberPoi(t);
                if (IsCamp(t.name)) { Remember(t, null); remembered=true;break; }
                for (int c = 0; c < t.childCount; c++) Scan.Push(t.GetChild(c));
            }
            if(Scan.Count==0)_discoveryReady=true;
            if(!remembered&&MapQueue.Count>0)
            {
                var next=MapQueue.Dequeue();QueuedMaps.Remove(next.Root);
                if(next.Root!=null)Remember(next.Root,next.Box,next.Structure);
            }
            if (Time.unscaledTime < _nextBake) return;
            _nextBake = Time.unscaledTime + 5f;
            try
            {
                if (_structures == null)
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var nav = assembly.GetType("NPCAI.Nav"); if (nav == null) continue;
                        _structures = nav.GetField("_structures", Flags);
                        break;
                    }
                var structures = _structures?.GetValue(null) as IEnumerable;
                if (structures == null) return;
                foreach (var s in structures)
                {
                    var type = s.GetType(); var root = type.GetField("Root", Flags)?.GetValue(s) as Transform;
                    if (root == null || !IsCamp(root.name)) continue;
                    var box = type.GetField("Box", Flags)?.GetValue(s);
                    if(QueuedMaps.Add(root))MapQueue.Enqueue(new MapRequest{Root=root,Box=box is Bounds?(Bounds?)box:null,Structure=s});
                }
            }
            catch (Exception e) { Plugin.Verbose("Traffic: NPC Overhaul map adapter: " + e.Message); }
        }

        internal static bool IsCamp(string name)
        { return name != null && name.StartsWith("Camp_", StringComparison.OrdinalIgnoreCase) && name.Length > 5 && char.IsDigit(name[5]); }

        internal static bool CoyoteCamp(string name)
        {var n=TemplateExporter.PrefabName(name);return n=="Camp_10"||n=="Camp_15"||n=="Camp_16";}
        internal static bool PoiName(string name)
        {return CoyoteCamp(name)||name.StartsWith("Building_",StringComparison.OrdinalIgnoreCase)||name.StartsWith("GasStation",StringComparison.OrdinalIgnoreCase)||name.StartsWith("Shop_",StringComparison.OrdinalIgnoreCase);}
        private static void RememberPoi(Transform root)
        {
            if(!PoiName(root.name))return;string id=Identity(root);if(Points.ContainsKey(id)||!ExaminedPoints.Add(id))return;
            // Inspect a POI only on first discovery, in the existing incremental scene scan.
            var nodes=root.GetComponentsInChildren<Transform>(true);
            if(nodes.Any(t=>t.name.StartsWith("EnemySpawn_Scrapyard",StringComparison.OrdinalIgnoreCase)))return;
            Vector3 point=root.position;
            var box=new Bounds(point,Vector3.zero);foreach(var collider in root.GetComponentsInChildren<Collider>(true))if(!collider.isTrigger)box.Encapsulate(collider.bounds);
            point=box.center+root.forward*(Mathf.Max(box.extents.x,box.extents.z)+25f);
            if(Points.Count>=1024)return;
            Points[id]=new TrafficPoi{id=id,name=TemplateExporter.PrefabName(root.name),position=TrafficSpace.World(point)};
        }

        internal static string Identity(Transform t)
        {
            var p = TrafficSpace.World(t.position);
            return TemplateExporter.PrefabName(t.name) + ":" + Mathf.RoundToInt(p.x) + ":" + Mathf.RoundToInt(p.z);
        }

        private static void Remember(Transform root, Bounds? baked, object structure = null)
        {
            string id = Identity(root);
            TrafficCamps.Register(id,root);
            // Older NPC Overhaul grids can still contain pre-jump scene coordinates; use current collider geometry instead.
            if (baked.HasValue && !baked.Value.Contains(root.position)) { baked = null; structure = null; }
            bool existed = Known.TryGetValue(id, out var location);
            bool complete = structure != null && (bool)(structure.GetType().GetField("Baked", Flags)?.GetValue(structure) ?? false);
            if (existed && Roots.TryGetValue(id, out var same) && same == root && Mapped.Contains(id)) return;
            if (existed && Roots.TryGetValue(id, out same) && same == root && !complete) return;
            if (existed && Roots.TryGetValue(id, out var old) && old == root && !baked.HasValue) return;
            if (!existed && Known.Count >= 512) return;
            Roots[id] = root;
            Bounds box = baked ?? new Bounds(root.position, Vector3.one * 60f);
            if (!baked.HasValue)
            {
                bool first = true;
                foreach (var collider in root.GetComponentsInChildren<Collider>())
                {
                    if (collider.isTrigger || !collider.enabled) continue;
                    if (first) { box = collider.bounds; first = false; } else box.Encapsulate(collider.bounds);
                }
            }
            location = new TrafficBase { id = id, name = TemplateExporter.PrefabName(root.name),cleared=location!=null&&location.cleared,faction=CoyoteCamp(root.name)?"Coyotes":"Scrapyard" };
            location.center = box.center; location.center.y = root.position.y;
            location.size = box.size;
            Vector3 interior = location.center;
            // Use a real floor from NPC Overhaul's bake, rather than assuming the prefab origin is driveable.
            // The pedestrian bake proposes an entrance anchor; the sphere corridor below certifies vehicle clearance.
            if (complete)
            {
                var type = structure.GetType();
                var floors = type.GetField("FloorY", Flags)?.GetValue(structure) as float[];
                int w = Convert.ToInt32(type.GetField("W", Flags)?.GetValue(structure) ?? 0);
                float cell = Convert.ToSingle(type.GetField("Cell", Flags)?.GetValue(structure) ?? 0f);
                if (floors != null && w > 0 && cell > 0f)
                {
                    float nearest = float.MaxValue;
                    for (int i = 0; i < floors.Length; i++)
                    {
                        if (float.IsNaN(floors[i])) continue;
                        var point = new Vector3(box.min.x + (i % w + 0.5f) * cell, floors[i], box.min.z + (i / w + 0.5f) * cell);
                        float score = (point - root.position).sqrMagnitude;
                        if (score < nearest) { nearest = score; interior = point; }
                    }
                }
            }
            // Named camp entrances take precedence; clearance is independently tested for a vehicle.
            Transform entrance = null;
            foreach (var t in root.GetComponentsInChildren<Transform>())
                if (t.name.IndexOf("entrance", StringComparison.OrdinalIgnoreCase) >= 0) { entrance = t; break; }
            float radius = Mathf.Max(box.extents.x, box.extents.z) + 16f;
            bool found = false; float best = float.MaxValue;
            Vector3 fallback = root.position + root.forward * radius;
            for (int i = 0; i < 16; i++)
            {
                var direction = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                var outer = location.center + direction * radius;
                if (!Convoy.GroundAt(outer, out var ground)) continue;
                var gate = entrance != null ? entrance.position : interior;
                float score = entrance != null ? FlatDistance(ground, gate) : FlatDistance(ground, fallback);
                bool clear = VehicleClear(null, ground + Vector3.up * 2f, gate + Vector3.up * 2f, 1.4f);
                if (clear) score -= 1000f;
                if (score >= best) continue;
                best = score; location.apron = ground; location.gate = clear ? gate : ground;
                location.mappedEntrance = clear; found = true;
            }
            if (!found) return; // terrain isn't loaded yet; keep the previous stable record and try later
            if (found)
            {
                location.center = TrafficSpace.World(location.center);
                location.apron = TrafficSpace.World(location.apron);
                location.gate = TrafficSpace.World(location.gate);
                Known[id] = location;
                Revision++;
                if (complete) Mapped.Add(id);
            }
        }

        internal static float FlatDistance(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }

        internal static float EngineRange(GameObject car)
        {
            return PatrolVisibility.EngineDistance(car);
        }

        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        internal static bool VehicleClear(GameObject car, Vector3 from, Vector3 to, float radius)
        {
            var d = to - from; float length = d.magnitude; if (length < 0.1f) return true;
            int n = Physics.SphereCastNonAlloc(from, radius, d / length, Hits, length,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (n == Hits.Length) return false; // overflow cannot certify clearance
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.collider == null || h.distance > length || h.distance <= 0f) continue;
                if (h.collider is TerrainCollider) continue;
                if (car != null && (h.collider.transform.IsChildOf(car.transform)
                    || (TrailerSupport.Attached(car) != null && h.collider.transform.IsChildOf(TrailerSupport.Attached(car).transform)))) continue;
                if (h.collider.gameObject.layer == 9 || h.collider.GetComponentInParent<PatrolMarker>() != null) continue;
                return false;
            }
            Array.Clear(Hits, 0, Hits.Length);
            return true;
        }

        internal static bool CampApproach(GameObject car, Vector3 target, out Vector3 approach, out bool hold)
        {
            approach = target; hold = false;
            var worldTarget = TrafficSpace.World(target);
            foreach (var camp in Known.Values)
            {
                if (Mathf.Abs(worldTarget.x - camp.center.x) > camp.size.x * 0.5f || Mathf.Abs(worldTarget.z - camp.center.z) > camp.size.z * 0.5f) continue;
                var from = car.transform.position;
                // A human-sized opening is never accepted without checking the chassis corridor.
                float radius = car.GetComponent<TrafficAgent>()?.Truck == true ? 2f : 1.4f;
                if (VehicleClear(car, from + Vector3.up, target + Vector3.up, radius)) return false;
                approach = TrafficSpace.Scene(camp.mappedEntrance ? camp.gate : camp.apron);
                if (FlatDistance(from, approach) < 12f)
                {
                    if (camp.mappedEntrance && VehicleClear(car, from + Vector3.up, target + Vector3.up, radius)) return false;
                    hold = true; // shooters can engage from the apron; the driver does not press into spikes/walls
                }
                return true;
            }
            return false;
        }

        // Strategic travel avoids every known camp footprint except the exterior departure/arrival aprons.
        internal static Vector3[] RoadRoute(TrafficBase source, TrafficBase destination, bool convoy, out bool[] roads)
        {
            return RoadPlanner.Build(source.apron,destination.apron,Plugin.TrafficRoadRouting.Value?TrafficRoads.Tiles.Values.Select(t=>t.Hints).ToArray():new Vector3[0][],
                Blocks(),Plugin.TrafficRoadConnector.Value,unchecked(source.apron.GetHashCode()*397^destination.apron.GetHashCode()),out roads);
        }

        internal static bool LegSafe(Vector3 a, Vector3 b, string source, string destination)
        {
            foreach (var camp in Known.Values)
            {
                var box = new Bounds(new Vector3(camp.center.x, 0f, camp.center.z), new Vector3(camp.size.x + 16f, 10000f, camp.size.z + 16f));
                var aa = a; var bb = b; aa.y = bb.y = 0f;
                if (box.Contains(aa) || box.Contains(bb) || Crosses(box, aa, bb)) return false;
            }
            return true;
        }

        internal static Vector3[] Route(TrafficBase source, TrafficBase destination)
        {
            var route = new List<Vector3> { source.apron, destination.apron };
            for (int attempts = 0; attempts < 32; attempts++)
            {
                bool changed = false;
                for (int leg = 1; leg < route.Count && !changed; leg++)
                    foreach (var camp in Known.Values)
                    {
                        var box = new Bounds(new Vector3(camp.center.x, 0f, camp.center.z), new Vector3(camp.size.x + 16f, 10000f, camp.size.z + 16f));
                        var a = route[leg - 1]; var b = route[leg]; a.y = b.y = 0f;
                        if (box.Contains(a) || box.Contains(b)) continue;
                        if (!box.IntersectRay(new Ray(a, (b - a).normalized), out var hit) || hit >= Vector3.Distance(a, b)) continue;
                        Vector3[] corners = {
                            new Vector3(box.min.x - 2f, route[leg-1].y, box.min.z - 2f),
                            new Vector3(box.min.x - 2f, route[leg-1].y, box.max.z + 2f),
                            new Vector3(box.max.x + 2f, route[leg-1].y, box.min.z - 2f),
                            new Vector3(box.max.x + 2f, route[leg-1].y, box.max.z + 2f) };
                        float best = float.MaxValue; Vector3[] detour = null;
                        foreach (var c in corners) foreach (var d in corners)
                        {
                            if (Crosses(box, a, c) || Crosses(box, c, d) || Crosses(box, d, b)) continue;
                            float cost = FlatDistance(a, c) + FlatDistance(c, d) + FlatDistance(d, b);
                            if (cost < best) { best = cost; detour = c == d ? new[] { c } : new[] { c, d }; }
                        }
                        if (detour == null) return null;
                        route.InsertRange(leg, detour); changed = true; break;
                    }
                if (!changed) return route.ToArray();
            }
            return null; // overlapping geometry has no bounded route in this prototype
        }

        private static bool Crosses(Bounds box, Vector3 a, Vector3 b)
        { a.y = b.y = 0f; var d = b - a; return d.magnitude > 0.01f && box.IntersectRay(new Ray(a, d.normalized), out var hit) && hit < d.magnitude; }
    }
}

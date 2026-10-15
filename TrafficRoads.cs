using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using BepInEx;
using Den.Tools.Splines;
using MapMagic.Core;
using MapMagic.Nodes;
using MapMagic.Nodes.MatrixGenerators;
using MapMagic.Nodes.SplinesGenerators;
using MapMagic.Products;
using MapMagic.Terrains;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class RoadTile
    {
        internal string World;
        internal int X, Z;
        internal bool Approximate;
        internal Vector3[][] Lines;
        internal bool Cached;
        internal ElevationGrid Elevation;
        private Vector3[] _hints;
        internal Vector3[] Hints
        {
            get
            {
                if(_hints!=null)return _hints;
                int total=Lines.Sum(l=>l.Length),stride=Math.Max(1,(total+191)/192),index=0;
                var points=new List<Vector3>(Math.Min(192,total));
                foreach(var line in Lines)foreach(var p in line)if(index++%stride==0)points.Add(p);
                return _hints=points.ToArray();
            }
        }
        internal string Id => X + "_" + Z;
        internal void Compact()
        {
            var hints=Hints;
            Lines=hints.Length>=2?new[]{hints}:new Vector3[0][];
            if(hints.Length<2)_hints=new Vector3[0];
        }
    }

    // The finalized callback runs on MapMagic's worker. Only immutable metadata and spline coordinates
    // cross that boundary. World activation stays on the main thread; disk IO runs on one background queue.
    internal static class TrafficRoads
    {
        private sealed class Capture
        {
            internal string World;
            internal int X, Z;
            internal Rect Rect;
            internal ulong[] Sources;
        }
        private static readonly ConcurrentDictionary<TileData, Capture> Captures = new ConcurrentDictionary<TileData, Capture>();
        private static readonly ConcurrentQueue<RoadTile> Pending = new ConcurrentQueue<RoadTile>();
        internal static readonly Dictionary<string, RoadTile> Tiles = new Dictionary<string, RoadTile>();
        private static readonly Queue<RoadTile> Dirty = new Queue<RoadTile>();
        private static readonly Queue<Tuple<string,int,int>> Loads=new Queue<Tuple<string,int,int>>();
        private static readonly HashSet<string> Requested=new HashSet<string>();
        private static readonly ConcurrentQueue<Action> IO=new ConcurrentQueue<Action>();
        private static readonly ConcurrentQueue<string> Errors=new ConcurrentQueue<string>();
        private static int _ioRunning;
        private static MapMagicObject _map;
        private static Graph _graph;
        private static ulong[] _sources = new ulong[0];
        private static string _world;
        private static int _seed;
        private static float _scanAt, _writeAt;
        private static bool _installed;
        internal static int Revision { get; private set; }
        internal static Vector3 TileSize => _map != null ? (Vector3)_map.tileSize : Vector3.zero;
        internal static string Status => Tiles.Count + " road tiles";
        private static string CacheRoot => Path.Combine(Paths.ConfigPath, "Apocapatrol.roads");

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            TerrainTile.OnBeforeTilePrepare += Prepare;
            TerrainTile.OnTileFinalized += Finalized;
        }

        internal static void Reset()
        {
            Flush(); Captures.Clear(); Tiles.Clear(); Loads.Clear();Requested.Clear();
            _world = null; _map = null; _graph = null; _scanAt = 0f; Revision++;
            while (Pending.TryDequeue(out _)) { }
        }

        private static void Prepare(TerrainTile tile, TileData data)
        {
            if (data == null || data.isDraft || tile == null || tile.mapMagic == null) return;
            try
            {
                Activate(tile.mapMagic);
                Captures[data] = new Capture { World = _world, X = tile.coord.x, Z = tile.coord.z,
                    Rect = tile.WorldRect, Sources = _sources };
            }
            catch (Exception e) { Plugin.Log.LogWarning("Road discovery prepare: " + e.Message); }
        }

        private static void Activate(MapMagicObject map)
        {
            if (map.graph == null) return;
            int seed = map.graph.random.Seed;
            if (_world != null && _map == map && _graph == map.graph && _seed == seed) return;
            Flush(); Tiles.Clear(); Loads.Clear();Requested.Clear();Revision++;
            _map = map; _graph = map.graph; _seed = seed;
            // Follow the actual road terrain-layer inputs upstream to Stroke generators. No authored generator IDs.
            var sources = new HashSet<ulong>();
            var generators = map.graph.generators.ToDictionary(g => g.Id);
            foreach (var output in map.graph.generators.OfType<TexturesOutput200>())
                foreach (var layer in output.layers)
                {
                    string name = layer.name;
                    if (name == null || !name.StartsWith("road", StringComparison.OrdinalIgnoreCase)) continue;
                    var stack = new Stack<ulong>(); stack.Push(LinkedGenerator(map.graph, layer));
                    var seen = new HashSet<ulong>();
                    while (stack.Count > 0)
                    {
                        ulong id = stack.Pop();
                        if (!seen.Add(id) || !generators.TryGetValue(id, out var gen)) continue;
                        if (gen is Stroke200 stroke)
                        { if (map.graph.links.TryGetValue(stroke, out var road)) sources.Add(road.Id); continue; }
                        if (gen is IMultiInlet multi)
                            foreach (var inlet in multi.Inlets()) stack.Push(LinkedGenerator(map.graph, inlet));
                        if (gen is IInlet<object> single) stack.Push(LinkedGenerator(map.graph, single));
                        if (gen is ICustomDependence custom) foreach (var dependency in custom.PriorGens())
                            if (dependency != null) stack.Push(dependency.Id);
                    }
                }
            _sources = sources.Where(s => s != 0).ToArray();
            _world = Fingerprint(map);
            Plugin.Verbose("Roads: " + _sources.Length + " spline sources; cached hints will load on demand, seed " + seed);
        }

        private static ulong LinkedGenerator(Graph graph, IInlet<object> inlet)
        { return graph.links.TryGetValue(inlet, out var outlet) && outlet.Gen != null ? outlet.Gen.Id : 0; }

        private static string Fingerprint(MapMagicObject map)
        {
            var text = new StringBuilder("roads-v1|");
            text.Append(typeof(MapMagicObject).Assembly.ManifestModule.ModuleVersionId).Append('|');
            text.Append(map.graph.random.Seed).Append('|').Append(map.tileSize).Append('|')
                .Append((int)map.tileResolution).Append('|').Append(map.tileMargins).Append('|').Append(map.globals.height);
            // Include actual recipe settings and wiring so edits don't reuse a different world's roads.
            foreach (var gen in map.graph.generators.OrderBy(g => g.Id))
            {
                text.Append('|').Append(gen.Id).Append(':').Append(gen.GetType().FullName);
                foreach (var field in gen.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
                {
                    var type = field.FieldType;
                    if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Vector3) || type == typeof(Vector2))
                        text.Append('|').Append(field.Name).Append('=').Append(Convert.ToString(field.GetValue(gen), CultureInfo.InvariantCulture));
                }
                if (gen is TexturesOutput200 output) foreach (var layer in output.layers)
                    text.Append(':').Append(layer.name).Append(':').Append(layer.prototype != null ? layer.prototype.name : "");
                if (gen is ICustomDependence custom) foreach (var dependency in custom.PriorGens()) text.Append(':').Append(dependency?.Id);
            }
            foreach (var link in map.graph.links.OrderBy(l => l.Key.Id)) text.Append('|').Append(link.Key.Id).Append('=').Append(link.Value.Id);
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").Substring(0, 24);
        }

        private static void Finalized(TerrainTile tile, TileData data, StopToken stop)
        {
            if (data == null || data.isDraft || (stop != null && stop.stop) || !Captures.TryRemove(data, out var capture)) return;
            try
            {
                var lines = new List<Vector3[]>();
                foreach (ulong source in capture.Sources)
                {
                    var spline = data.ReadProduct(source) as SplineSys;
                    if (spline == null) continue;
                    foreach (var line in spline.lines)
                    {
                        var points = new List<Vector3>();
                        foreach (var segment in line.segments)
                        {
                            float polygonLength = segment.start.dir.magnitude + segment.end.dir.magnitude
                                + Vector3.Distance(segment.start.pos + segment.start.dir, segment.end.pos + segment.end.dir);
                            int steps = Mathf.Clamp(Mathf.CeilToInt(polygonLength / 64f), 1, 512);
                            for (int i = 0; i <= steps; i++)
                            { var point = segment.GetPoint((float)i / steps); if (points.Count == 0 || Vector3.Distance(points[points.Count - 1], point) > 0.01f) points.Add(point); }
                        }
                        lines.AddRange(Clip(points.ToArray(), capture.Rect));
                    }
                }
                if (stop != null && stop.stop) return;
                var result=new RoadTile { World = capture.World, X = capture.X, Z = capture.Z, Lines = lines.ToArray() };
                result.Elevation=ElevationGrid.Capture(data.heights,capture.Rect);
                result.Compact();Pending.Enqueue(result);
            }
            catch (Exception e) { /* This callback must never interrupt terrain generation. */ System.Diagnostics.Debug.WriteLine(e); }
        }

        internal static Vector3[][] Clip(Vector3[] points, Rect rect)
        {
            var result = new List<Vector3[]>(); var run = new List<Vector3>();
            for (int i = 1; i < points.Length; i++)
            {
                var a = points[i - 1]; var b = points[i]; float lo = 0f, hi = 1f;
                bool ok = Bound(a.x, b.x - a.x, rect.xMin, rect.xMax, ref lo, ref hi)
                    && Bound(a.z, b.z - a.z, rect.yMin, rect.yMax, ref lo, ref hi);
                if (!ok || hi <= lo) { if (run.Count > 1) result.Add(run.ToArray()); run.Clear(); continue; }
                var start = Vector3.Lerp(a, b, lo); var end = Vector3.Lerp(a, b, hi);
                if (run.Count > 0 && Vector3.Distance(run[run.Count - 1], start) > 0.05f)
                { if (run.Count > 1) result.Add(run.ToArray()); run.Clear(); }
                if (run.Count == 0) run.Add(start);
                if (Vector3.Distance(run[run.Count - 1], end) > 0.01f) run.Add(end);
            }
            if (run.Count > 1) result.Add(run.ToArray()); return result.ToArray();
        }
        private static bool Bound(float a, float d, float min, float max, ref float lo, ref float hi)
        {
            if (Mathf.Abs(d) < 0.00001f) return a >= min && a <= max;
            float x = (min - a) / d, y = (max - a) / d;
            lo = Mathf.Max(lo, Mathf.Min(x, y)); hi = Mathf.Min(hi, Mathf.Max(x, y)); return lo <= hi;
        }

        internal static void Tick()
        {
            if (Time.unscaledTime >= _scanAt)
            {
                _scanAt = Time.unscaledTime + 5f;
                var map = _map!=null?_map:UnityEngine.Object.FindObjectOfType<MapMagicObject>();
                if (map != null && map.graph != null)
                {
                    Activate(map);
                    foreach (var tile in map.tiles.All())
                    {
                        string id = tile.coord.x + "_" + tile.coord.z;
                        if (Tiles.ContainsKey(id) || tile.main == null || !tile.main.applyReady) continue;
                        if (tile.main.data != null && tile.main.data.ProductsCount > 0)
                        { Prepare(tile, tile.main.data); Finalized(tile, tile.main.data, null); }
                        // Don't read full terrain paint/height arrays. Missing road hints use the cheap snake fallback.
                        break; // One existing terrain per scan; ordinary captures are cheaper and exact.
                    }
                }
            }
            for (int i = 0; i < 4 && Pending.TryDequeue(out var tile); i++)
            {
                if (tile.World != _world || Tiles.Count >= 2048 && !Tiles.ContainsKey(tile.Id)) continue;
                if (Tiles.TryGetValue(tile.Id, out var old) && !old.Approximate && tile.Approximate) continue;
                if(Tiles.TryGetValue(tile.Id,out old)&&!old.Approximate&&tile.Cached)continue;
                Tiles[tile.Id] = tile; if(!tile.Cached)Dirty.Enqueue(tile); Revision++;
            }
            if (Time.unscaledTime >= _writeAt && Dirty.Count > 0)
            { _writeAt = Time.unscaledTime + 1f; Save(Dirty.Dequeue()); }
            if(Loads.Count>0)
            {
                var load=Loads.Dequeue();string world=load.Item1;int x=load.Item2,z=load.Item3;
                string path=Path.Combine(CacheRoot,world,x+"_"+z+".roads");
                EnqueueIO(()=>{if(!File.Exists(path))return;var tile=ReadCache(path,world);tile.Compact();tile.Cached=true;Pending.Enqueue(tile);});
            }
            if(Errors.TryDequeue(out var error))Plugin.Log.LogWarning("Road cache: "+error);
        }

        internal static void RequestAlong(Vector3 a,Vector3 b)
        {
            var size=TileSize;if(_world==null||size.x<=0f||size.z<=0f)return;
            var forward=b-a;forward.y=0f;forward.Normalize();var side=Vector3.Cross(Vector3.up,forward);
            for(int i=0;i<=8;i++)
            {
                var center=Vector3.Lerp(a,b,i/8f);
                for(int offset=-1;offset<=1;offset++)
                {
                    var p=center+side*(offset*1400f);int x=Mathf.FloorToInt(p.x/size.x),z=Mathf.FloorToInt(p.z/size.z);string id=x+"_"+z;
                    if(Tiles.ContainsKey(id)||!Requested.Add(id))continue;
                    Loads.Enqueue(Tuple.Create(_world,x,z));
                }
            }
        }

        private static void EnqueueIO(Action action)
        {
            IO.Enqueue(action);
            if(Interlocked.CompareExchange(ref _ioRunning,1,0)==0)Task.Run((Action)RunIO);
        }
        private static void RunIO()
        {
            try{while(IO.TryDequeue(out var action))try{action();}catch(Exception e){Errors.Enqueue(e.Message);}}
            finally{Interlocked.Exchange(ref _ioRunning,0);if(!IO.IsEmpty&&Interlocked.CompareExchange(ref _ioRunning,1,0)==0)Task.Run((Action)RunIO);}
        }

        internal static void Flush() { while (Dirty.Count > 0) Save(Dirty.Dequeue()); }
        private static void Save(RoadTile tile)
        {
            string directory=Path.Combine(CacheRoot,tile.World);
            EnqueueIO(()=>{Directory.CreateDirectory(directory);WriteCache(Path.Combine(directory,tile.Id+".roads"),tile);});
        }
        internal static void WriteCache(string path, RoadTile tile)
        {
            string temporary = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                writer.Write(0x41505244); writer.Write(2); writer.Write(tile.World); writer.Write(tile.X); writer.Write(tile.Z);
                writer.Write(tile.Approximate); writer.Write(tile.Lines.Length);
                foreach (var line in tile.Lines)
                { writer.Write(line.Length); foreach (var p in line) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); } }
                var grid=tile.Elevation;writer.Write(grid!=null);
                if(grid!=null)
                {writer.Write(grid.Area.x);writer.Write(grid.Area.y);writer.Write(grid.Area.width);writer.Write(grid.Area.height);writer.Write(grid.Size);foreach(float height in grid.Heights)writer.Write(height);}
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        internal static RoadTile ReadCache(string path, string world)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if(reader.ReadInt32()!=0x41505244)throw new InvalidDataException("Road cache header");
                int version=reader.ReadInt32();if((version!=1&&version!=2)||reader.ReadString()!=world)throw new InvalidDataException("Road cache identity mismatch");
                var tile = new RoadTile { World = world, X = reader.ReadInt32(), Z = reader.ReadInt32(), Approximate = reader.ReadBoolean() };
                int count = reader.ReadInt32(), total = 0;
                if (count < 0 || count > 4096) throw new InvalidDataException("Road count");
                tile.Lines = new Vector3[count][];
                for (int i = 0; i < count; i++)
                {
                    int n = reader.ReadInt32(); total += n;
                    if (n < 2 || n > 20000 || total > 200000) throw new InvalidDataException("Road points");
                    tile.Lines[i] = new Vector3[n];
                    for (int p = 0; p < n; p++)
                    {
                        var v = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        if (!Finite(v)) throw new InvalidDataException("Road coordinate"); tile.Lines[i][p] = v;
                    }
                }
                if(version>=2&&reader.ReadBoolean())
                {
                    var rect=new Rect(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());int size=reader.ReadInt32();
                    if(!Finite(new Vector3(rect.x,rect.y,rect.width))||!Finite(new Vector3(rect.height,0f,0f))||rect.width<=0f||rect.height<=0f||size<2||size>65)throw new InvalidDataException("Elevation grid dimensions");
                    var heights=new float[size*size];for(int i=0;i<heights.Length;i++){heights[i]=reader.ReadSingle();if(float.IsNaN(heights[i])||float.IsInfinity(heights[i]))throw new InvalidDataException("Elevation sample");}
                    tile.Elevation=new ElevationGrid{Area=rect,Size=size,Heights=heights};
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing road data");
                var hints=tile.Hints;
                return tile;
            }
        }
        private static bool Finite(Vector3 p) => !float.IsNaN(p.x + p.y + p.z) && !float.IsInfinity(p.x + p.y + p.z);
    }
}

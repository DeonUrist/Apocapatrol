using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class RoadJob
    {
        internal volatile bool Complete;
        internal Vector3[] Route;
        internal bool[] Roads;
        internal string Error;
        internal int Revision, Epoch;
        internal bool Elevation;
    }

    // Coarse hints influence a short, fixed waypoint chain. No junction graph, connectivity prediction
    // or full-world search. A single worker computes routes once; driving and maps reuse those arrays.
    internal static class RoadPlanner
    {
        private sealed class Request
        {
            internal RoadJob Job;
            internal Vector3 A,B;
            internal Vector3[][] Hints;
            internal Bounds[] Blocks;
            internal float Influence;
            internal int Seed;
            internal ElevationGrid[] Elevation;
        }
        private static readonly ConcurrentQueue<Request> Queue=new ConcurrentQueue<Request>();
        private static int _running,_epoch;
        private static int _snapshotRoad=-1,_snapshotCamps=-1,_snapshotCount=-1;
        private static RoadTile[] _tiles;
        private static Bounds[] _blocks;
        internal static long Searches;
        internal static void Reset(){Interlocked.Increment(ref _epoch);_snapshotRoad=_snapshotCamps=_snapshotCount=-1;}
        internal static RoadJob RequestRoute(TrafficBase source,TrafficBase destination,bool convoy)
        {
            if(Plugin.TrafficRoadRouting.Value)TrafficRoads.RequestAlong(source.apron,destination.apron);
            if(_snapshotRoad!=TrafficRoads.Revision||_snapshotCamps!=TrafficLocations.Revision||_snapshotCount!=TrafficLocations.Known.Count)
            {
                _snapshotRoad=TrafficRoads.Revision;_snapshotCamps=TrafficLocations.Revision;_snapshotCount=TrafficLocations.Known.Count;
                _tiles=TrafficRoads.Tiles.Values.ToArray();_blocks=TrafficLocations.Blocks();
            }
            int seed=unchecked(source.apron.GetHashCode()*397^destination.apron.GetHashCode()^(convoy?73:19));
            var job=new RoadJob{Epoch=_epoch,Revision=_snapshotRoad};
            var size=TrafficRoads.TileSize;float margin=Plugin.TrafficRoadConnector.Value+1600f;
            var a=source.apron;var b=destination.apron;var bounds=new Rect(Mathf.Min(a.x,b.x)-margin,Mathf.Min(a.z,b.z)-margin,Mathf.Abs(a.x-b.x)+margin*2f,Mathf.Abs(a.z-b.z)+margin*2f);
            var nearby=_tiles.Where(t=>size.x<=0f||bounds.Overlaps(new Rect(t.X*size.x,t.Z*size.z,size.x,size.z))).ToArray();
            var hints=Plugin.TrafficRoadRouting.Value?nearby.Select(t=>t.Hints).ToArray():new Vector3[0][];
            job.Elevation=nearby.Any(t=>t.Elevation!=null);
            Queue.Enqueue(new Request{Job=job,A=a,B=b,Hints=hints,
                Elevation=nearby.Where(t=>t.Elevation!=null).Select(t=>t.Elevation).ToArray(),
                Blocks=_blocks,Influence=Plugin.TrafficRoadConnector.Value,Seed=seed});
            Start();return job;
        }
        private static void Start(){if(Interlocked.CompareExchange(ref _running,1,0)==0)Task.Run((Action)Run);}
        private static void Run()
        {
            try
            {
                while(Queue.TryDequeue(out var request))
                {
                    try
                    {
                        if(request.Job.Epoch!=Volatile.Read(ref _epoch))continue;
                        request.Job.Route=TerrainWaypoints.Build(request.A,request.B,request.Hints,request.Blocks,request.Influence,request.Seed,request.Elevation,out var roads);
                        request.Job.Roads=roads;RoadPath.Prepare(request.Job.Route);Interlocked.Increment(ref Searches);
                    }
                    catch(Exception e){request.Job.Error=e.ToString();}
                    finally{request.Job.Complete=true;}
                }
            }
            finally{Interlocked.Exchange(ref _running,0);if(!Queue.IsEmpty)Start();}
        }
        internal static Vector3[] Build(Vector3 a,Vector3 b,Vector3[][] hints,Bounds[] blocks,float influence,int seed,out bool[] roads)
        {
            float length=TrafficLocations.FlatDistance(a,b);
            int steps=Mathf.Clamp(Mathf.CeilToInt(length/400f),6,16);
            var direction=b-a;direction.y=0f;direction.Normalize();var side=Vector3.Cross(Vector3.up,direction);
            var random=new System.Random(seed);float phase=(float)random.NextDouble()*Mathf.PI*2f;
            float amplitude=Mathf.Min(180f,length*0.16f);
            var route=new List<Vector3>{a};var kinds=new List<bool>{false};
            for(int i=1;i<steps;i++)
            {
                float t=(float)i/steps;
                var desired=Vector3.Lerp(a,b,t)+side*(Mathf.Sin(t*Mathf.PI*3f+phase)*Mathf.Sin(t*Mathf.PI)*amplitude);
                var chosen=desired;float best=influence*influence;bool road=false;
                foreach(var tile in hints)foreach(var point in tile)
                {
                    float along=Vector3.Dot(point-a,direction);
                    if(Mathf.Abs(along-length*t)>length/steps*0.7f)continue;
                    var d=point-desired;d.y=0f;float score=d.sqrMagnitude;
                    if(score>=best)continue;
                    bool inside=false;var flat=point;flat.y=0f;foreach(var box in blocks)if(box.Contains(flat)){inside=true;break;}
                    if(inside)continue;best=score;chosen=point;road=true;
                }
                if(TrafficLocations.FlatDistance(route[route.Count-1],chosen)<12f)continue;
                route.Add(chosen);kinds.Add(road);
            }
            route.Add(b);kinds.Add(false);
            // A coarse route can skirt a known camp without constructing a world navigation graph.
            for(int attempts=0;attempts<16 && route.Count<32;attempts++)
            {
                bool changed=false;
                for(int leg=1;leg<route.Count&&!changed;leg++)foreach(var box in blocks)
                {
                    var start=route[leg-1];var end=route[leg];start.y=end.y=0f;var d=end-start;float distance=d.magnitude;
                    if(box.Contains(start)||box.Contains(end)||distance<0.01f||!box.IntersectRay(new Ray(start,d/distance),out var hit)||hit>=distance)continue;
                    var min=box.min;var max=box.max;
                    var corners=new[]{new Vector3(min.x-3f,route[leg-1].y,min.z-3f),new Vector3(min.x-3f,route[leg-1].y,max.z+3f),
                        new Vector3(max.x+3f,route[leg-1].y,min.z-3f),new Vector3(max.x+3f,route[leg-1].y,max.z+3f)};
                    float cost=float.MaxValue;Vector3[] detour=null;
                    foreach(var c in corners)foreach(var e in corners)
                    {
                        if(Crosses(box,start,c)||Crosses(box,c,e)||Crosses(box,e,end))continue;
                        float score=TrafficLocations.FlatDistance(start,c)+TrafficLocations.FlatDistance(c,e)+TrafficLocations.FlatDistance(e,end);
                        if(score<cost){cost=score;detour=c==e?new[]{c}:new[]{c,e};}
                    }
                    if(detour==null)continue;
                    route.InsertRange(leg,detour);kinds.InsertRange(leg,Enumerable.Repeat(false,detour.Length));kinds[leg+detour.Length]=false;
                    changed=true;break;
                }
                if(!changed)break;
            }
            roads=kinds.ToArray();return route.ToArray();
        }
        private static bool Crosses(Bounds box,Vector3 a,Vector3 b)
        {a.y=b.y=0f;var d=b-a;float length=d.magnitude;return length>0.01f&&box.IntersectRay(new Ray(a,d/length),out var hit)&&hit<length;}
    }
}

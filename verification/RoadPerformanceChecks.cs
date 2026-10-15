using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Apocapatrol;
using UnityEngine;

public partial class TrailerRunner
{
    IEnumerator RoadPerformanceChecks()
    {
        var world=(Behaviour)asm.GetType("Apocapatrol.WorldTraffic").GetField("_instance",All).GetValue(null);world.enabled=false;
        Call("TrafficLocations","Reset");Call("TrafficRoads","Reset");
        var root=Environment.GetCommandLineArgs().First(a=>a.StartsWith("-road-cache=")).Substring(12);
        var tiles=(IDictionary)asm.GetType("Apocapatrol.TrafficRoads").GetField("Tiles",All).GetValue(null);
        var allLines=new List<Vector3[]>();
        foreach(var file in Directory.GetFiles(root,"*.roads",SearchOption.AllDirectories))
        {
            string key;using(var reader=new BinaryReader(File.OpenRead(file))){reader.ReadInt32();reader.ReadInt32();key=reader.ReadString();}
            var tile=Call("TrafficRoads","ReadCache",file,key);tiles[Get(tile,"X")+"_"+Get(tile,"Z")]=tile;
            allLines.AddRange((Vector3[][])Get(tile,"Lines"));
        }
        Check(tiles.Count>0 && allLines.Count>0,"Performance fixture reads the installed road cache without changing it");
        var line=allLines.OrderByDescending(l=>l.Length).First();
        var source=new TrafficBase{id="perf-A",apron=line[0]};var destination=new TrafficBase{id="perf-B",apron=line.Last()};
        TrafficSetting("TrafficRoadRouting",true);
        var watch=new Stopwatch();var planning=new List<double>();
        for(int i=0;i<12;i++)
        {
            object[] args={source,destination,true,null};watch.Restart();Call("TrafficLocations","RoadRoute",args);watch.Stop();planning.Add(watch.Elapsed.TotalMilliseconds);
            yield return null;
        }
        var longRoute=new Vector3[20000];for(int i=0;i<longRoute.Length;i++)longRoute[i]=new Vector3(i*8f,0,Mathf.Sin(i*0.02f)*40f);
        var requestCosts=new List<double>();
        for(int i=0;i<12;i++)
        {
            watch.Restart();var job=Call("RoadPlanner","RequestRoute",source,destination,true);watch.Stop();requestCosts.Add(watch.Elapsed.TotalMilliseconds);
            float until=Time.realtimeSinceStartup+5f;while(!(bool)Get(job,"Complete")&&Time.realtimeSinceStartup<until)yield return null;
            Check((bool)Get(job,"Complete")&&Get(job,"Error")==null&&((Vector3[])Get(job,"Route")).Length<=32,"Asynchronous coarse route completes with a bounded waypoint count");
        }
        var actual=longRoute[19000];
        watch.Restart();for(int i=0;i<1000;i++)Call("RoadPath","Target",longRoute,19000,actual,15f);watch.Stop();
        var report="Cache tiles="+tiles.Count+", points="+allLines.Sum(l=>l.Length)+"\nSynchronous route requests ms: "+string.Join(", ",planning.Select(p=>p.ToString("0.00")))
            +"\nMain-thread async request ms: "+string.Join(", ",requestCosts.Select(p=>p.ToString("0.000")))
            +"\n1000 long-route driving targets ms: "+watch.Elapsed.TotalMilliseconds.ToString("0.00");
        File.WriteAllText(Path.Combine(TrailerProbe.Output,"performance.txt"),report);lines.Add("PASS: "+report);
    }
}

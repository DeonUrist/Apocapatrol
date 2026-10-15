using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apocapatrol;
using Den.Tools;
using MapMagic.Core;
using MapMagic.Products;
using MapMagic.Terrains;
using UnityEngine;

public partial class TrailerRunner
{
    IEnumerator RoadChecks()
    {
        Call("WorldTraffic","ResetForScene");Call("TrafficLocations","Reset");Call("TrafficRoads","Reset");
        var hints=new[]{new[]{Vector3.zero,new Vector3(350,0,150),new Vector3(700,0,250),new Vector3(1050,0,100),new Vector3(1400,0,-100),new Vector3(1750,0,120),new Vector3(2100,0,0)}};
        object[] build={Vector3.zero,new Vector3(2100,0,0),hints,new Bounds[0],350f,123,null};
        var curve=(Vector3[])Call("RoadPlanner","Build",build);var flags=(bool[])build[6];
        Check(curve.Length>2&&curve.Length<=17&&curve.Any(p=>Mathf.Abs(p.z)>80f)&&flags.Any(b=>b),"Sparse route is curved and influenced by cached roads");
        Check(curve.First()==Vector3.zero&&curve.Last()==new Vector3(2100,0,0),"Coarse waypoints preserve exact departure and destination positions");
        build[2]=new Vector3[0][];var snake=(Vector3[])Call("RoadPlanner","Build",build);
        Check(snake.Length>2&&snake.Length<=17&&snake.Any(p=>Mathf.Abs(p.z)>30f)&&!((bool[])build[6]).Any(b=>b),"Missing road hints produce a bounded snake route without waiting for generation");
        Check(((Vector3[])Call("RoadPlanner","Build",build)).SequenceEqual(snake),"The fallback is cached deterministic geometry, not random changes while driving");
        build[1]=new Vector3(100000,0,0);Check(((Vector3[])Call("RoadPlanner","Build",build)).Length<=17,"Very long journeys retain a fixed small waypoint budget");
        var bend=new[]{Vector3.zero,Vector3.right*40,new Vector3(40,0,40),new Vector3(80,0,40)};
        Check((Vector3)Call("RoadPath","Point",bend,60f)==new Vector3(40,0,20),"Cached route distances preserve bend interpolation");
        Check((float)Call("RoadPath","Speed",bend,1,new Vector3(30,0,0),20f)<20f,"Sparse route driving still brakes before a sharp bend");
        var inkType=asm.GetType("Apocapatrol.TrafficMapInk");var ink=Activator.CreateInstance(inkType,true);
        TrafficInvoke(ink,"Begin",128,128);
        TrafficInvoke(ink,"Segment",new Vector2(10,10),new Vector2(60,90),new Color32(255,0,0,255));
        TrafficInvoke(ink,"Segment",new Vector2(60,90),new Vector2(115,10),new Color32(255,0,0,255));
        TrafficInvoke(ink,"End");var pixels=(Color32[])inkType.GetProperty("Pixels",All).GetValue(ink,null);
        Check(pixels[(127-90)*128+60].a>0&&pixels[(127-10)*128+60].a==0,"Cached map texture draws the waypoint bend instead of the endpoint chord");
        TrafficInvoke(ink,"Segment",new Vector2(-20,60),new Vector2(180,60),new Color32(255,0,0,255));
        Check(pixels[(127-60)*128+64].a>0,"Map segments crossing the viewport are clipped and retained even when both endpoints are outside");
        TrafficInvoke(ink,"Dispose");

        var worldType=asm.GetType("Apocapatrol.WorldTraffic");var world=(Behaviour)worldType.GetField("_instance",All).GetValue(null);world.enabled=false;
        var known=(IDictionary)asm.GetType("Apocapatrol.TrafficLocations").GetField("Known",All).GetValue(null);
        var source=new TrafficBase{id="A",apron=Vector3.zero,center=new Vector3(-100,0,0),size=Vector3.one*20};
        var destination=new TrafficBase{id="B",apron=new Vector3(2100,0,0),center=new Vector3(2200,0,0),size=Vector3.one*20};
        known["A"]=source;known["B"]=destination;
        var tileType=asm.GetType("Apocapatrol.RoadTile");var tileRecord=Activator.CreateInstance(tileType,true);
        Set(tileRecord,"World","fixture");Set(tileRecord,"Lines",hints);
        var tiles=(IDictionary)asm.GetType("Apocapatrol.TrafficRoads").GetField("Tiles",All).GetValue(null);tiles["fixture"]=tileRecord;
        TrafficSetting("WorldTrafficEnabled",true);TrafficSetting("TrafficRoadRouting",true);
        var old=new TrafficJourney{id="straight",source="A",destination="B",route=new[]{source.apron,destination.apron},position=source.apron,speed=10f};
        ((IList)Get(world,"_journeys")).Add(old);
        var live=Activator.CreateInstance(worldType.GetNestedType("Live",All),true);var car=new GameObject("Existing physical patrol fixture");
        var agent=car.AddComponent(asm.GetType("Apocapatrol.TrafficAgent"));Set(agent,"Journey",old.id);
        ((IDictionary)Get(live,"Cars"))[0]=car;((IDictionary)Get(world,"_live"))[old.id]=live;
        TrafficInvoke(world,"RefreshRoadRoutes");float deadline=Time.realtimeSinceStartup+5f;
        while(old.route.Length==2&&Time.realtimeSinceStartup<deadline){yield return null;TrafficInvoke(world,"RefreshRoadRoutes");}
        Check(old.route.Length>2&&old.roadLegs.Any(b=>b),"An existing physical patrol adopts a cached curved route instead of keeping its old direct line");
        Check(old.position==source.apron&&car.transform.position==Vector3.zero,"Road-route adoption leaves the physical car and simulated position unchanged");
        var encoded=(string)Call("TrafficCodec","Write",new TrafficSave{journeys=new[]{old}});
        var decoded=(TrafficSave)Call("TrafficCodec","Read",encoded);
        Check(decoded.journeys[0].route.SequenceEqual(old.route),"Saved patrols retain the same sparse waypoint chain used by driving and the map");
        string cachePath=Path.Combine(TrailerProbe.Output,"fixture.roads");Call("TrafficRoads","WriteCache",cachePath,tileRecord);
        var read=Call("TrafficRoads","ReadCache",cachePath,"fixture");
        Check(((Vector3[][])Get(read,"Lines"))[0].SequenceEqual(hints[0]),"Existing binary road caches remain compatible with lazy coarse hints");
        var many=Enumerable.Range(0,20000).Select(i=>Vector3.right*i).ToArray();Set(tileRecord,"Lines",new[]{many});
        Call("TrafficRoads","WriteCache",cachePath,tileRecord);read=Call("TrafficRoads","ReadCache",cachePath,"fixture");
        Check(((Vector3[])tileType.GetProperty("Hints",All).GetValue(read,null)).Length<=192,"A dense old tile is reduced to at most 192 cached hints");
        bool rejected=false;try{Call("TrafficRoads","ReadCache",cachePath,"different-world");}catch{rejected=true;}
        Check(rejected,"Lazy road files reject another world identity");
        Call("WorldTraffic","ResetForScene");UnityEngine.Object.Destroy(car);TrafficSetting("WorldTrafficEnabled",false);

        var map=Resources.FindObjectsOfTypeAll<MapMagicObject>().First(m=>m.gameObject.scene.IsValid());map.enabled=false;
        var tile=TerrainTile.Construct(map);tile.coord=new Coord(0,0);
        var data=new TileData{area=new Area(tile.coord,(int)map.tileResolution,map.tileMargins,map.tileSize),globals=map.globals,random=map.graph.random};
        TerrainTile.OnBeforeTilePrepare?.Invoke(tile,data);
        var sources=(ulong[])asm.GetType("Apocapatrol.TrafficRoads").GetField("_sources",All).GetValue(null);
        Check(sources.Length==2,"Both actual MyDesert road sources supply coarse cached hints");
        Check(((IDictionary)asm.GetType("Apocapatrol.TrafficRoads").GetField("Tiles",All).GetValue(null)).Count==0,"Activating a world does not synchronously load all its road files");
        string key=(string)asm.GetType("Apocapatrol.TrafficRoads").GetField("_world",All).GetValue(null);
        map.transform.position+=new Vector3(-1250,25,1000);
        Check((string)Call("TrafficRoads","Fingerprint",map)==key,"Road hint cache identity survives kilometre origin shifts");
        map.graph.GetType().GetMethod("RefreshInputHashIds",All).Invoke(map.graph,null);var stop=new StopToken();
        data.heights=new Den.Tools.Matrices.MatrixWorld(data.area.full.rect,data.area.full.worldPos,data.area.full.worldSize,data.globals.height);data.heights.Fill(0.25f);
        var task=Task.Run(()=>{foreach(ulong id in sources)map.graph.GenerateRecursive(map.graph.generators.Single(g=>g.Id==id),data,map.graph.defaults,stop);TerrainTile.OnTileFinalized?.Invoke(tile,data,stop);});
        deadline=Time.realtimeSinceStartup+90f;while(!task.IsCompleted&&Time.realtimeSinceStartup<deadline)yield return null;
        if(!task.IsCompleted){stop.stop=true;throw new Exception("Road generation fixture timed out");}if(task.IsFaulted)throw task.Exception;
        var pending=(IEnumerable)asm.GetType("Apocapatrol.TrafficRoads").GetField("Pending",All).GetValue(null);
        var captured=pending.Cast<object>().Last();var capturedLines=(Vector3[][])Get(captured,"Lines");
        var elevation=Get(captured,"Elevation");
        Check(elevation!=null&&((float[])Get(elevation,"Heights")).Length==65*65&&Mathf.Abs(((float[])Get(elevation,"Heights"))[0]-data.globals.height*0.25f)<0.01f,
            "Tile finalization captures 65x65 world-height samples before MapMagic clears its normalized products");
        Check(capturedLines.Length>0&&capturedLines.Sum(l=>l.Length)>10,"Native generated roads are captured coarsely before MapMagic clears its products");
        data.Clear(true,true);Check(capturedLines.Sum(l=>l.Length)>10,"Coarse hints survive terrain product clearing and unloading");
        File.WriteAllText(Path.Combine(TrailerProbe.Output,"roads-native.txt"),"sources="+sources.Length+"; sampled points="+capturedLines.Sum(l=>l.Length)+"; hints="+((Vector3[])tileType.GetProperty("Hints",All).GetValue(captured,null)).Length);
        Set(captured,"X",99);Set(captured,"Z",99);
        string lazyDirectory=Path.Combine(BepInEx.Paths.ConfigPath,"Apocapatrol.roads",key);Directory.CreateDirectory(lazyDirectory);
        Call("TrafficRoads","WriteCache",Path.Combine(lazyDirectory,"99_99.roads"),captured);
        Call("TrafficRoads","Reset");TerrainTile.OnBeforeTilePrepare?.Invoke(tile,data);
        tiles=(IDictionary)asm.GetType("Apocapatrol.TrafficRoads").GetField("Tiles",All).GetValue(null);
        Check(tiles.Count==0,"World activation leaves an existing road file on disk until a journey requests its tile");
        var distant=new Vector3(map.tileSize.x*99+100,0,map.tileSize.z*99+100);
        Call("TrafficRoads","RequestAlong",distant,distant+Vector3.right*10);
        Check(!tiles.Contains("99_99"),"Lazy route requests queue IO without reading a road file synchronously");
        deadline=Time.realtimeSinceStartup+5f;
        while(!tiles.Contains("99_99")&&Time.realtimeSinceStartup<deadline){Call("TrafficRoads","Tick");yield return null;}
        Check(tiles.Contains("99_99")&&((Vector3[])tileType.GetProperty("Hints",All).GetValue(tiles["99_99"],null)).Length<=192,
            "Requested road files load asynchronously into bounded reusable hints");
        Call("TrafficRoads","Reset");UnityEngine.Object.Destroy(tile.gameObject);
    }
}

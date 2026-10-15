using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Apocapatrol;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

// Actor death aggregation runs with the completed-spawner prerequisite explicitly supplied.
[HarmonyPatch]
public static class CampReadyFixture
{
    public static bool Active;
    static MethodBase TargetMethod()=>typeof(Plugin).Assembly.GetType("Apocapatrol.TrafficCamps").GetMethod("Ready",BindingFlags.Static|BindingFlags.NonPublic);
    static bool Prefix(ref bool __result){if(!Active)return true;__result=true;return false;}
}
public partial class TrailerRunner
{
    IEnumerator UpgradeChecks()
    {
        var patrol=new TrafficJourney{name="Dust Raiders"};var convoy=new TrafficJourney{name="Fuel train",convoy=true};
        TrafficSetting("RealPatrolNames",true);TrafficSetting("HideRouteType",false);
        Check((string)Call("WorldTraffic","MapName",patrol)=="Dust Raiders","Real patrol names defaults to named traffic");
        TrafficSetting("RealPatrolNames",false);
        Check((string)Call("WorldTraffic","MapName",patrol)=="Patrol"&&(string)Call("WorldTraffic","MapName",convoy)=="Convoy","Disabling real names labels both traffic roles generically");
        TrafficSetting("HideRouteType",true);
        Check((string)Call("WorldTraffic","MapName",patrol)=="Distant traffic"&&(string)Call("WorldTraffic","MapName",convoy)=="Distant traffic"
            &&((Color32)Call("WorldTraffic","RouteColor",patrol)).Equals((Color32)Call("WorldTraffic","RouteColor",convoy)),"Hidden route type overrides names and gives both roles the same gray color");
        TrafficSetting("RealPatrolNames",true);TrafficSetting("HideRouteType",false);
        var gridType=asm.GetType("Apocapatrol.ElevationGrid");var grid=Activator.CreateInstance(gridType,true);
        const int size=65;var area=new Rect(-500,-1800,4200,3600);var heights=new float[size*size];
        for(int z=0;z<size;z++)for(int x=0;x<size;x++)
        {float px=area.xMin+area.width*x/(size-1f),pz=area.yMin+area.height*z/(size-1f);heights[z*size+x]=Mathf.Max(0f,400f*(1f-Mathf.Sqrt((px-1600)*(px-1600)+pz*pz)/700f));}
        Set(grid,"Area",area);Set(grid,"Size",size);Set(grid,"Heights",heights);
        var grids=Array.CreateInstance(gridType,1);grids.SetValue(grid,0);
        object[] args={Vector3.zero,Vector3.right*3200,new Vector3[0][],new Bounds[0],350f,123,grids,null};
        var timer=System.Diagnostics.Stopwatch.StartNew();var route=(Vector3[])Call("TerrainWaypoints","Build",args);timer.Stop();
        Check(route.Length<=17&&route.First()==Vector3.zero&&route.Last()==Vector3.right*3200,"Terrain routing retains a bounded sparse waypoint chain and exact endpoints");
        float peak=0f;
        for(int i=1;i<route.Length;i++)for(int j=0;j<=20;j++)
        {object[] sample={Vector3.Lerp(route[i-1],route[i],j/20f),0f,0f};gridType.GetMethods(All).Single(m=>m.Name=="Sample"&&!m.IsStatic).Invoke(grid,sample);peak=Mathf.Max(peak,(float)sample[1]);}
        Check(peak<70f&&route.Any(p=>Mathf.Abs(p.z)>600f),"Cached elevation guides the entire route around a 400 m mountain instead of cutting between low endpoints: peak="+peak);
        lines.Add("Terrain worker benchmark: "+timer.Elapsed.TotalMilliseconds.ToString("F2")+" ms, "+route.Length+" waypoints, no Unity Terrain or Physics reads");
        var tile=Activator.CreateInstance(asm.GetType("Apocapatrol.RoadTile"),true);Set(tile,"World","height-fixture");Set(tile,"Lines",new Vector3[0][]);Set(tile,"Elevation",grid);
        string file=Path.Combine(TrailerProbe.Output,"elevation.roads");Call("TrafficRoads","WriteCache",file,tile);
        var read=Call("TrafficRoads","ReadCache",file,"height-fixture");
        Check(((float[])Get(Get(read,"Elevation"),"Heights")).SequenceEqual(heights),"Lazy road cache round-trips coarse heights without recalculating terrain");
        using(var writer=new BinaryWriter(File.Create(file))){writer.Write(0x41505244);writer.Write(1);writer.Write("height-fixture");writer.Write(0);writer.Write(0);writer.Write(false);writer.Write(0);}
        Check(Get(Call("TrafficRoads","ReadCache",file,"height-fixture"),"Elevation")==null,"Old road caches still load and safely fall back until elevation is captured");

        Call("WorldTraffic","ResetForScene");var known=(IDictionary)asm.GetType("Apocapatrol.TrafficLocations").GetField("Known",All).GetValue(null);
        var root=new GameObject("Camp fixture");var camp=new TrafficBase{id="camp",name="Fixture",center=Vector3.zero};known[camp.id]=camp;
        Call("TrafficCamps","Register",camp.id,root.transform);
        Check((int)Call("TrafficLocations","NearbyCount",Vector3.zero,2500f)==1,"A loaded nearby camp supports traffic");
        root.SetActive(false);Check((int)Call("TrafficLocations","NearbyCount",Vector3.zero,2500f)==0,"Unloaded camps do not increase local traffic population");root.SetActive(true);
        var actor=new GameObject("Resident fixture");var hp=new FsmFloat{Value=100};Call("TrafficCamps","Bind",camp.id,actor,hp);
        hp.Value=0;
        Check(!(bool)Call("TrafficCamps","Check",camp.id),"Dead actors do not clear a camp while its spawn population is unresolved");
        CampReadyFixture.Active=true;
        Check((bool)Call("TrafficCamps","Check",camp.id)&&camp.cleared&&(int)Call("TrafficLocations","NearbyCount",Vector3.zero,2500f)==0,"Confirmed resident deaths clear a completed camp and remove its population contribution");
        var persisted=(TrafficSave)Call("TrafficCodec","Read",(string)Call("TrafficCodec","Write",new TrafficSave{bases=new[]{camp}}));
        Check(persisted.bases[0].cleared,"Cleared base status persists across save/reload");
        var world=(Behaviour)asm.GetType("Apocapatrol.WorldTraffic").GetField("_instance",All).GetValue(null);world.enabled=false;
        var journeys=(IList)Get(world,"_journeys");journeys.Add(new TrafficJourney{id="cleared-virtual",source=camp.id,destination=camp.id,convoy=true});
        TrafficInvoke(world,"ExcludeClearedBases");Check(journeys.Count==0,"Virtual journeys involving a cleared base are cancelled");
        var liveType=world.GetType().GetNestedType("Live",All);var live=Activator.CreateInstance(liveType,true);
        var journey=new TrafficJourney{id="redirect",destination=camp.id,convoy=true,position=Vector3.right*500};journeys.Add(journey);
        ((IDictionary)Get(world,"_live"))[journey.id]=live;
        var replacement=new TrafficBase{id="replacement",apron=Vector3.right*1500};known[replacement.id]=replacement;
        TrafficInvoke(world,"ExcludeClearedBases");
        Check(journey.destination==replacement.id&&journey.route.Last()==replacement.apron&&!journey.arrived,"Live traffic reroutes from a cleared destination to a surviving base");
        replacement.cleared=true;TrafficInvoke(world,"ExcludeClearedBases");
        Check(journey.arrived&&journey.destination==""&&journey.route.Last()==journey.position,"With every base cleared, live traffic holds instead of driving into a cleared camp");
        ((IDictionary)Get(world,"_live")).Clear();journeys.Clear();
        UnityEngine.Object.DestroyImmediate(actor);Call("TrafficCamps","Reset");camp.cleared=false;Call("TrafficCamps","Register",camp.id,root.transform);
        actor=new GameObject("Alive despawn fixture");Call("TrafficCamps","Bind",camp.id,actor,new FsmFloat{Value=100});UnityEngine.Object.DestroyImmediate(actor);
        Check(!camp.cleared&&!(bool)Call("TrafficCamps","Check",camp.id),"An alive resident despawn never marks a camp cleared");
        CampReadyFixture.Active=false;Call("WorldTraffic","ResetForScene");world.enabled=true;UnityEngine.Object.DestroyImmediate(root);
        yield return null;
    }
}

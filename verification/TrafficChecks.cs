using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Apocapatrol;
using HarmonyLib;
using UnityEngine;

[HarmonyPatch]
public static class TrafficPlayFixture
{
    public static bool Active;
    static MethodBase TargetMethod() { return typeof(Plugin).Assembly.GetType("Apocapatrol.Patrol").GetMethod("InGame", BindingFlags.Instance | BindingFlags.NonPublic); }
    static bool Prefix(ref bool __result) { if (!Active) return true; __result = true; return false; }
}

public partial class TrailerRunner
{
    object TrafficInvoke(object instance, string method, params object[] args)
    { return instance.GetType().GetMethod(method, All).Invoke(instance, args); }
    void TrafficSetting(string name, object value)
    {
        var setting = typeof(Plugin).GetField(name, All).GetValue(null);
        setting.GetType().GetProperty("Value").SetValue(setting, value, null);
    }

    IEnumerator TrafficChecks()
    {
        var travel = new TrafficJourney { id = "distance", route = new[] { Vector3.zero, new Vector3(100,0,0), new Vector3(100,0,100) }, speed = 10f };
        travel.GetType().GetMethod("Advance", All).Invoke(travel, new object[]{15f});
        Check(travel.leg == 2 && travel.position == new Vector3(100,0,50) && !travel.arrived,"Virtual travel crosses route legs without losing distance");
        TrafficInvoke(travel,"Advance",10f);
        Check(travel.arrived && travel.position == new Vector3(100,0,100),"Virtual arrival stops at the destination");
        Check(!(bool)TrafficInvoke(travel,"Expire",500f,true,180f) && travel.unseenSeconds == 0f,"A nearby player keeps an arrived group indefinitely");
        Check(!(bool)TrafficInvoke(travel,"Expire",179f,false,180f) && (bool)TrafficInvoke(travel,"Expire",1f,false,180f),"Unattended arrival expires only after its catch-up grace");
        Check((int)Call("TrafficRules","Population",20f,0.2f,24) == 4 && (int)Call("TrafficRules","Population",500f,1f,24) == 24,"Density scales with area and respects population cap");
        Check((int)Call("TrafficRules","Population",100f,0f,24) == 0,"Zero density creates no groups");
        Check((float)Call("TrafficRules","EngineRange",500f)==500f&&(float)Call("TrafficRules","EngineRange",59f)==59f&&(float)Call("TrafficRules","EngineRange",0f)==0f,"Nighttime engine hearing uses horsepower directly as metres");
        Check(!(bool)Call("TrafficRules","Pursue",true,true,5f,300f,true,true),"Convoy truck never pursues even with a detected nearby player");
        Check((bool)Call("TrafficRules","Pursue",true,false,299f,300f,false,false)
            && !(bool)Call("TrafficRules","Pursue",true,false,301f,300f,true,true),"Convoy escorts engage inside 300 m and return outside it");
        Check(!(bool)Call("TrafficRules","Pursue",false,false,10f,300f,false,false)
            && (bool)Call("TrafficRules","Pursue",false,false,10f,300f,false,true),"Patrol requires detection and retains a last-known search");

        var worldType = asm.GetType("Apocapatrol.WorldTraffic");
        var world = (Component)worldType.GetField("_instance",All).GetValue(null);
        ((Behaviour)world).enabled=false;
        Call("WorldTraffic","ResetForScene");
        var originAnchor = new GameObject("Traffic MapMagic frame fixture");
        asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,originAnchor.transform);
        var known = (IDictionary)asm.GetType("Apocapatrol.TrafficLocations").GetField("Known",All).GetValue(null);
        var from = new TrafficBase{id="A",name="Camp_A",center=new Vector3(-200,0,0),apron=new Vector3(-200,0,0),size=Vector3.one*20};
        var to = new TrafficBase{id="B",name="Camp_B",center=new Vector3(200,0,0),apron=new Vector3(200,0,0),size=Vector3.one*20};
        known[from.id]=from;known[to.id]=to;
        var block = new TrafficBase{id="wall",center=Vector3.zero,size=new Vector3(80,20,80)};known[block.id]=block;
        var route=(Vector3[])Call("TrafficLocations","Route",from,to);
        Check(route!=null && route.Length>2 && route.First()==from.apron && route.Last()==to.apron,"Strategic route detours around another known camp footprint");
        Check((bool)Call("TrafficLocations","IsCamp","Camp_12(Clone)") && !(bool)Call("TrafficLocations","IsCamp","Camp_fire"),"Discovery recognizes camp instances without treating campfire props as bases");

        TrafficSetting("WorldTrafficEnabled",true);TrafficSetting("ConvoyEnabled",false);
        TrafficPlayFixture.Active=true;
        var fakePlayer=new GameObject("Traffic test player");
        var playerType=asm.GetType("Apocapatrol.PlayerRef");
        playerType.GetField("_player",All).SetValue(null,fakePlayer.transform);
        var terrainData=new TerrainData {heightmapResolution=33,size=new Vector3(6000,20,6000)};
        terrainData.SetHeights(0,0,new float[33,33]);
        var terrain=Terrain.CreateTerrainGameObject(terrainData);terrain.transform.position=new Vector3(100000,2000,100000);
        var start=new Vector3(101000,2000,101000);
        from.center=from.apron=start;to.center=to.apron=start+new Vector3(1500,0,0);
        var campA=new GameObject("Traffic loaded camp A");var campB=new GameObject("Traffic loaded camp B");
        Call("TrafficCamps","Register","A",campA.transform);Call("TrafficCamps","Register","B",campB.transform);
        TrafficSetting("CampTrafficRange",10000f);
        fakePlayer.transform.position=start+new Vector3(0,2,80);
        Physics.SyncTransforms();
        var fixture=TemplateFile.FromJson(File.ReadAllText(Path.Combine(TrailerProbe.Templates,"Example_PipeRat.json")));
        fixture.driver="Scraffa";fixture.passenger="";fixture.rider=fixture.rider2=fixture.rider3="";fixture.lootPreset="car-empty";
        var template=Call("CarTemplates","Parse",fixture.ToJson(),"Traffic fixture","verification","traffic",false);
        var frozen=(TrafficVehicle)Call("WorldTraffic","Freeze",template);
        var journey=new TrafficJourney{id="native",name="Native patrol",source="A",destination="B",position=start,
            route=new[]{start,start+new Vector3(1500,0,0)},speed=10f,vehicles=new[]{frozen}};
        var fixtureJson=(string)Call("TrafficCodec","Write",new TrafficSave{bases=new[]{from,to},journeys=new[]{journey}});
        File.WriteAllText(Path.Combine(TrailerProbe.Output,"traffic-fixture.json"),fixtureJson);
        Call("WorldTraffic","RestoreJson",fixtureJson);
        var current=(TrafficJourney)Call("WorldTraffic","Find","native");
        Check(current!=null && current.position==start && current.vehicles.Length==1,"Native world records deserialize before materialization: " + (current==null ? "missing" : current.position+" leg="+current.leg+" roster="+current.vehicles.Length));
        Check(Call("WorldTraffic","Thaw",current.vehicles[0])!=null,"Frozen production template resolves on reload");
        object[] groundArgs={start,Vector3.zero};
        Check((bool)Call("Convoy","GroundAt",groundArgs),"Native terrain provides a safe materialization floor");
        Check((bool)Call("TrafficLocations","VehicleClear",null,start+Vector3.up*2,start+Vector3.up*2+Vector3.right*10,1.4f),"Native chassis corridor is clear at the fixture");
        var patrol=world.GetComponent(asm.GetType("Apocapatrol.Patrol"));Set(world,"_patrol",patrol);
        TrafficInvoke(world,"Tick",0.25f);
        float deadline=Time.realtimeSinceStartup+35f;
        Component agent=null;
        while(Time.realtimeSinceStartup<deadline)
        {
            agent=UnityEngine.Object.FindObjectsOfType(worldType.Assembly.GetType("Apocapatrol.TrafficAgent")).Cast<Component>().FirstOrDefault(a=>(string)Get(a,"Journey")=="native");
            var live=(IDictionary)Get(world,"_live");
            if(agent!=null && live.Contains("native") && !(bool)Get(live["native"],"Building"))break;
            yield return null;
        }
        Check(agent!=null,"Nearby virtual group materializes through the production vehicle builder");
        var car=agent.gameObject;var crew=car.GetComponent(asm.GetType("Apocapatrol.Crew"));((Behaviour)crew).enabled=false;
        var marker=car.GetComponent(asm.GetType("Apocapatrol.PatrolMarker"));
        Check((string)Get(marker,"TrafficId")=="native" && (int)Get(marker,"TrafficSlot")==0,"Physical representation retains its world identity and roster slot");
        object[] goalArgs={Vector3.zero,0f,false,false};
        TrafficInvoke(agent,"Goal",goalArgs);
        Check((bool)goalArgs[3],"Native patrol sees a player in daylight with clear line of sight");
        current.convoy=true;Set(agent,"Truck",true);
        TrafficInvoke(agent,"Goal",goalArgs);
        var travelGoal=(Vector3)goalArgs[0];
        var towardEnd=current.route.Last()-car.transform.position;var towardGoal=travelGoal-car.transform.position;towardEnd.y=towardGoal.y=0f;
        Check(!(bool)goalArgs[3] && Vector3.Dot(towardEnd.normalized,towardGoal.normalized)>0f,
            "Native convoy truck follows its route toward the destination during close player contact");
        var pilot=(Component)Call("Pilot","Attach",car);
        // The distant fixture is frozen by the game's DistanceKinematic; enable its body for a production driving step.
        car.GetComponent<Rigidbody>().isKinematic=false;
        TrafficInvoke(pilot,"Step");
        Check(pilot.GetType().GetProperty("State",All).GetValue(pilot,null).ToString()=="Travel",
            "Production truck pilot enters destination driving instead of charging the player");
        Set(agent,"Truck",false);TrafficInvoke(agent,"Goal",goalArgs);
        Check((bool)goalArgs[3],"Native escort switches to engagement near its convoy");
        fakePlayer.transform.position=start+Vector3.right*400;playerType.GetField("_targetStamp",All).SetValue(null,-1f);
        TrafficInvoke(agent,"Goal",goalArgs);
        Check(!(bool)goalArgs[3],"Native escort leaves pursuit beyond guard range");
        var trailerFixture=new GameObject("Traffic guard platform");
        var platform=trailerFixture.AddComponent(asm.GetType("Apocapatrol.TrafficPlatform"));Set(platform,"Owner",agent);
        Check(!(bool)Call("WorldTraffic","CombatAllowed",trailerFixture),"Trailer-mounted guards share convoy engagement limits");
        UnityEngine.Object.DestroyImmediate(trailerFixture);
        current.convoy=false;Set(agent,"SeenUntil",0f);Set(agent,"_nextSense",0f);
        Set(agent,"_detected",false);
        var condition=car.GetComponentsInChildren<PlayMakerFSM>(true).FirstOrDefault(f=>f.FsmName=="Condition" && f.FsmVariables.GetFsmFloat("Condition")!=null);
        Check(condition!=null,"Vehicle fixture exposes native part condition");
        condition.FsmVariables.GetFsmFloat("Condition").Value=37f;
        var driver=(GameObject)crew.GetType().GetProperty("Driver",All).GetValue(crew,null);
        Call("Patrol","SetHealth",driver,45f);
        fakePlayer.transform.position=start+new Vector3(3000,2,0);playerType.GetField("_targetStamp",All).SetValue(null,-1f);
        var before=current.position;
        TrafficInvoke(world,"Tick",0.25f);yield return null;
        Check(car==null && ((IDictionary)Get(world,"_live")).Count==0,"Far real group unloads without deleting its world journey");
        Check(current.vehicles[0].values.Any(v=>v.variable=="Condition" && v.value==37f)
            && current.vehicles[0].crew.driverHealth==45f,"Unload preserves actual part damage and driver health");
        TrafficInvoke(world,"Tick",5f);
        Check(current.position.x>before.x+45f,"Unloaded group continues travelling and can be caught again");
        var saved=(string)Call("WorldTraffic","SaveJson");
        var data=new PatrolSaveData {version=10,saveFile="SaveGame1.es3",worldSeed=42,trafficJson=saved,
            cars=new[]{new PatrolCarData{templateName="fixture",trafficId="native",trafficSlot=0}},convoyCooldown=123f};
        var bytes=(byte[])Call("PatrolPersistence","Serialize",data);
        var decoded=(PatrolSaveData)Call("PatrolPersistence","Deserialize",bytes);
        Check(decoded.trafficJson==saved && decoded.cars[0].trafficId=="native" && decoded.cars[0].trafficSlot==0,"Schema 10 round-trips world traffic and physical membership");
        data.version=9;bytes=(byte[])Call("PatrolPersistence","Serialize",data);decoded=(PatrolSaveData)Call("PatrolPersistence","Deserialize",bytes);
        Check(decoded.trafficJson=="" && decoded.cars[0].trafficId=="" && decoded.cars[0].trafficSlot==-1,"Schema 9 saves remain readable without traffic membership");
        Call("WorldTraffic","ResetForScene");Call("WorldTraffic","RestoreJson",saved);
        Call("TrafficCamps","Register","A",campA.transform);Call("TrafficCamps","Register","B",campB.transform);
        asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,originAnchor.transform);
        current=(TrafficJourney)Call("WorldTraffic","Find","native");
        Check(current!=null && current.vehicles[0].crew.driverHealth==45f && current.vehicles[0].values.Any(v=>v.value==37f),"Explicit JSON retains nested crew/damage data and route progress");
        fakePlayer.transform.position=current.position+new Vector3(0,2,80);playerType.GetField("_targetStamp",All).SetValue(null,-1f);
        TrafficInvoke(world,"Tick",0.25f);
        deadline=Time.realtimeSinceStartup+35f;agent=null;
        while(Time.realtimeSinceStartup<deadline)
        {
            agent=UnityEngine.Object.FindObjectsOfType(asm.GetType("Apocapatrol.TrafficAgent")).Cast<Component>().FirstOrDefault(a=>(string)Get(a,"Journey")=="native");
            var live=(IDictionary)Get(world,"_live");
            if(agent!=null && live.Contains("native") && !(bool)Get(live["native"],"Building"))break;
            yield return null;
        }
        if(agent==null)
        {
            File.WriteAllLines(Path.Combine(TrailerProbe.Output,"catchup-diagnostic.txt"),Resources.FindObjectsOfTypeAll(asm.GetType("Apocapatrol.TrafficAgent")).Cast<Component>()
                .Select(a=>a.name+" active="+a.gameObject.activeInHierarchy+" journey="+Get(a,"Journey"))
                .Concat(((IDictionary)Get(world,"_live")).Values.Cast<object>().Select(l=>"LIVE building="+Get(l,"Building")+" cars="+((IDictionary)Get(l,"Cars")).Count))
                .Concat(((IDictionary)Get(world,"_live")).Values.Cast<object>().SelectMany(l=>((IDictionary)Get(l,"Cars")).Values.Cast<GameObject>().Select(c=>c==null?"CAR DESTROYED":c.name+" active="+c.activeInHierarchy+" marker="+Get(c.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")),"TrafficId")+" components="+string.Join(",",c.GetComponents<Component>().Where(o=>o!=null).Select(o=>o.GetType().Name))))));
        }
        Check(agent!=null,"Catching up materializes the same saved journey again: timeScale="+Time.timeScale+" live="+((IDictionary)Get(world,"_live")).Count+" retired="+current.vehicles[0].retired);
        car=agent.gameObject;crew=car.GetComponent(asm.GetType("Apocapatrol.Crew"));((Behaviour)crew).enabled=false;
        Call("WorldTraffic","CaptureState",car,current.vehicles[0]);
        Check(current.vehicles[0].crew.driverHealth==45f && current.vehicles[0].values.Any(v=>v.variable=="Condition" && Mathf.Abs(v.value-37f)<0.001f),
            "Second materialization applies retained crew health and part condition within float serialization precision");
        OriginChecks(world,current,agent,fakePlayer,originAnchor);
        var part=car.GetComponentsInChildren<Transform>(true).First(t=>t.CompareTag("vehPart"));
        UnityEngine.Object.DestroyImmediate(part.gameObject);
        Check(!(bool)Call("WorldTraffic","CanVirtualize",car,current.vehicles[0]),"Stripped vehicles cannot virtualize into repaired replacements");
        current.arrived=true;current.unseenSeconds=0f;
        TrafficInvoke(world,"Tick",500f);
        Check(Call("WorldTraffic","Find","native")!=null,"Live arrived group stays when the player is around");
        fakePlayer.transform.position=(Vector3)Call("TrafficSpace","Scene",start+new Vector3(4000,2,0));
        TrafficInvoke(world,"Tick",181f);yield return null;
        Check(Call("WorldTraffic","Find","native")==null,"Arrived unattended group retires after grace");
        Call("WorldTraffic","ResetForScene");Call("PlayerRef","Reset");TrafficPlayFixture.Active=false;
        TrafficSetting("WorldTrafficEnabled",false);TrafficSetting("ConvoyEnabled",true);((Behaviour)world).enabled=true;
        UnityEngine.Object.Destroy(fakePlayer);UnityEngine.Object.Destroy(terrain);UnityEngine.Object.Destroy(terrainData);
        UnityEngine.Object.Destroy(originAnchor);
        UnityEngine.Object.Destroy(campA);UnityEngine.Object.Destroy(campB);TrafficSetting("CampTrafficRange",2500f);
    }
}

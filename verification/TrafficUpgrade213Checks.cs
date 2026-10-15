using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Apocapatrol;
using UnityEngine;
public partial class TrailerRunner
{
    IEnumerator TrafficUpgrade213Checks()
    {
        Check((float)Call("TrafficRules","VisibilityRange",false,false)==500f&&(float)Call("TrafficRules","VisibilityRange",true,true)==500f
            &&(float)Call("TrafficRules","VisibilityRange",true,false)==0f,"Patrol visibility is 500 m by day or with lights, zero at night without lights");
        Check((string)Call("Rider","Display","Gunnar")=="Gunnar"&&(bool)Call("Rider","IsGunner","Gunnar")&&!(bool)Call("Rider","IsWarboy","Gunnar"),"Gunnar keeps his name and gunner role instead of becoming a Warboy");
        foreach(var faction in new[]{"Coyotes","Scrapyard"})
        {
            var list=(Array)Call("TemplateMenu","VisibleTemplates",false,faction);var names=list.Cast<object>().Select(t=>(string)Get(t,"Name")).ToArray();
            Check(names.SequenceEqual(names.OrderBy(n=>n,StringComparer.OrdinalIgnoreCase))&&list.Cast<object>().All(t=>(bool)Call("CrewFactions","Compatible",t,faction)),faction+" template list contains only compatible crew and sorts by name");
        }
        var trader=Call("CarTemplate","Find","Coyote_Trader");var mixed=trader.GetType().GetMethod("CloneForSpawn",All).Invoke(trader,null);Set(mixed,"Passenger","Scraffa");
        Check(!(bool)Call("CrewFactions","Compatible",mixed,"Coyotes")&&!(bool)Call("CrewFactions","Compatible",mixed,"Scrapyard"),"Mixed passenger/driver factions cannot enter either roster");
        Check((bool)Call("CrewFactions","Allowed","Gunnar","Coyotes")&&!(bool)Call("CrewFactions","Allowed","Gunnar","Scrapyard")&&!(bool)Call("CrewFactions","Allowed","Warboy","Coyotes"),"Crew dropdown filters respect both factions including Warboy");
        Check((bool)Call("TrafficLocations","PoiName","Building_2(Clone)")&&(bool)Call("TrafficLocations","PoiName","Camp_10(Clone)")&&!(bool)Call("TrafficLocations","PoiName","Camp_1(Clone)"),"Coyote destinations recognize buildings and Coyote towns while excluding raider camps");
        var world=(Behaviour)asm.GetType("Apocapatrol.WorldTraffic").GetField("_instance",All).GetValue(null);world.enabled=false;Call("WorldTraffic","ResetForScene");
        TrafficSetting("MaxPatrols",1);TrafficSetting("MaxConvoys",0);
        var points=(IDictionary)asm.GetType("Apocapatrol.TrafficLocations").GetField("Points",All).GetValue(null);
        points["poi"]=new TrafficPoi{id="poi",name="Building_2",position=Vector3.right*800f};
        TrafficInvoke(world,"PopulateRoaming",Vector3.zero);
        float deadline=Time.realtimeSinceStartup+10f;var pending=Get(world,"_roamingDeparture");
        Check(pending!=null,"Coyote departure plans without any loaded bases");
        while(pending!=null&&Time.realtimeSinceStartup<deadline){TrafficInvoke(world,"PopulateRoaming",Vector3.zero);pending=Get(world,"_roamingDeparture");yield return null;}
        var journeys=(IList)Get(world,"_journeys");Check(journeys.Count==1,"Coyote patrol departs without a Coyote location or Scrapyard camp");
        var journey=(TrafficJourney)journeys[0];
        Check(journey.roaming&&journey.faction=="Coyotes"&&journey.destination=="poi"&&journey.route.Contains(Vector3.right*800f)
            &&Vector3.Distance(journey.route.Last(),Vector3.right*800f)>PluginDistance("TrafficUnloadRange"),"Incoming Coyote route visits a real POI and then exits beyond the unload range");
        TrafficInvoke(world,"PopulateRoaming",Vector3.zero);Check(journeys.Count==1&&Get(world,"_roamingDeparture")==null,"Incoming Coyotes share the configured total patrol cap");
        var origin=new GameObject("Coyote coordinate fixture");asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,origin.transform);
        var offset=new Vector3(101000,2000,101000);for(int i=0;i<journey.route.Length;i++)journey.route[i]+=offset;journey.position=journey.route[0];
        var player=new GameObject("Coyote materialization player");player.transform.position=journey.position+Vector3.up*2;asm.GetType("Apocapatrol.PlayerRef").GetField("_player",All).SetValue(null,player.transform);
        var terrainData=new TerrainData{heightmapResolution=33,size=new Vector3(6000,20,6000)};terrainData.SetHeights(0,0,new float[33,33]);var terrain=Terrain.CreateTerrainGameObject(terrainData);terrain.transform.position=offset-new Vector3(3000,0,3000);Physics.SyncTransforms();
        TrafficPlayFixture.Active=true;TrafficSetting("WorldTrafficEnabled",true);TrafficSetting("ConvoyEnabled",false);Set(world,"_patrol",world.GetComponent(asm.GetType("Apocapatrol.Patrol")));
        TrafficInvoke(world,"Tick",.25f);deadline=Time.realtimeSinceStartup+40f;var live=(IDictionary)Get(world,"_live");while(Time.realtimeSinceStartup<deadline&&(!live.Contains(journey.id)||(bool)Get(live[journey.id],"Building")))yield return null;
        Check(live.Contains(journey.id)&&((IDictionary)Get(live[journey.id],"Cars")).Count==2,"Passing Coyote patrol materializes both physical cars near the player with zero loaded camps");
        Check(((IDictionary)Get(live[journey.id],"Cars")).Values.Cast<GameObject>().All(c=>TrafficFactionsFixtureDriver(c).CompareTag("Coyotes")),"Passing cars spawn actual Coyote drivers");
        TrafficInvoke(world,"Unload",journey,live[journey.id]);yield return null;
        UnityEngine.Object.Destroy(player);UnityEngine.Object.Destroy(terrain);UnityEngine.Object.Destroy(terrainData);Call("PlayerRef","Reset");TrafficPlayFixture.Active=false;TrafficSetting("WorldTrafficEnabled",false);TrafficSetting("ConvoyEnabled",true);
        var saved=(string)Call("WorldTraffic","SaveJson");Call("WorldTraffic","ResetForScene");Call("WorldTraffic","RestoreJson",saved);
        Check(((TrafficJourney)((IList)Get(world,"_journeys"))[0]).roaming&&points.Contains("poi"),"Passing route and remembered POI survive world save/reload");
        Call("WorldTraffic","ResetForScene");world.enabled=true;TrafficSetting("MaxPatrols",12);TrafficSetting("MaxConvoys",8);
        UnityEngine.Object.Destroy(origin);
    }
    float PluginDistance(string name){var setting=typeof(Plugin).GetField(name,All).GetValue(null);return (float)setting.GetType().GetProperty("Value").GetValue(setting,null);}
}

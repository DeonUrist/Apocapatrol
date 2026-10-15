using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apocapatrol;
using HutongGames.PlayMaker;
using Micosmo.SensorToolkit.PlayMaker;
using UnityEngine;
public partial class TrailerRunner
{
    IEnumerator FactionChecks()
    {
        var action=new SensorGetLineOfSightResult{targetObject=new FsmGameObject(),storeVisibility=new FsmFloat{Value=1},storeIsVisible=new FsmBool{Value=true},everyFrame=true};
        Check(!(bool)Call("SeatedLosGuard","Before",action)&&action.storeVisibility.Value==0&&!action.storeIsVisible.Value,"A missing seated-NPC LOS target returns not visible without entering the dictionary lookup");
        var target=new GameObject("Valid LOS fixture");action.targetObject.Value=target;
        Check((bool)Call("SeatedLosGuard","Before",action),"Valid LOS targets retain the normal sensor path");UnityEngine.Object.DestroyImmediate(target);
        var defaults=EditorData.FromJson(File.ReadAllText(Path.Combine(Directory.GetParent(TrailerProbe.Templates).FullName,"EditorDefaults.json")));
        Check(defaults.Patrols.Any(g=>g.Faction=="Coyotes")&&defaults.Convoys.All(g=>g.Faction=="Scrapyard"),"Defaults include Coyote passing patrols and retain Scrapyard convoys");
        TrafficSetting("HideRouteType",false);
        var map=new TrafficJourney{faction="Scrapyard"};Check(((Color32)Call("WorldTraffic","RouteColor",map)).r==255,"Scrapyard patrol map color is red");
        map.convoy=true;var orange=(Color32)Call("WorldTraffic","RouteColor",map);Check(orange.r==255&&orange.g==150&&orange.b==40,"Scrapyard convoy map color is orange, leaving yellow unused");
        map.faction="Coyotes";var blue=(Color32)Call("WorldTraffic","RouteColor",map);Check(blue.b==255&&blue.g==210,"Coyote convoy map color is light blue");
        map.convoy=false;var green=(Color32)Call("WorldTraffic","RouteColor",map);Check(green.g==220&&green.g>green.r,"Coyote patrol map color is green");
        TrafficSetting("HideRouteType",true);var gray=(Color32)Call("WorldTraffic","RouteColor",map);Check(gray.r==gray.g&&gray.g==gray.b,"Hide route type overrides faction colors with gray");TrafficSetting("HideRouteType",false);
        defaults.Patrols[0].Faction="Coyotes";Check(EditorData.FromJson(defaults.ToJson()).Patrols[0].Faction=="Coyotes","Faction choices round-trip through the patrol editor store");
        foreach(var file in Directory.GetFiles(TrailerProbe.Templates,"*.json"))
        {var t=TemplateFile.FromJson(File.ReadAllText(file));Check(t.explodeChance==(t.name=="War_rig"||t.name=="Truck_War_Rig"||t.name=="Coyote_Trader"?0f:50f),"Shipped explosion chance: "+t.name);}
        var carType=asm.GetType("Apocapatrol.CarTemplate");
        var bike=Activator.CreateInstance(carType,true);Set(bike,"Body","Bike");Set(bike,"Kinds",new[]{"motorcycle"});
        var small=Activator.CreateInstance(carType,true);Set(small,"Body","PipeRat");Set(small,"Kinds",new[]{"small"});
        var semi=Activator.CreateInstance(carType,true);Set(semi,"Body","Halfbreed");Set(semi,"Kinds",new[]{"small","motorcycle"});
        var big=Activator.CreateInstance(carType,true);Set(big,"Body","Junker");Set(big,"Kinds",new[]{"medium"});
        Check((int)Call("TrafficBattles","Strength",bike)==1&&(int)Call("TrafficBattles","Strength",small)==2&&(int)Call("TrafficBattles","Strength",semi)==2&&(int)Call("TrafficBattles","Strength",big)==6,"One big car equals three small cars, and a small car/semibike equals two bikes");
        var shares=(int[])Call("TrafficBattles","Shares",8,4,6,5);Check(shares[0]==4&&shares[1]==2,"Two encounter sides divide six cars in their original eight-to-four roster proportion");
        shares=(int[])Call("TrafficBattles","Shares",50,1,6,5);Check(shares.Sum()<=6&&shares[0]<=5&&shares[1]==1,"The weaker faction still appears within double-event and individual event caps");
        var battle=new TrafficBattle{sideA=new[]{new TrafficVehicle{strength=6},new TrafficVehicle{strength=2}},sideB=new[]{new TrafficVehicle{strength=2},new TrafficVehicle{strength=2}},deadA=new bool[2],deadB=new bool[2]};
        Call("TrafficBattles","Resolve",battle);
        Check(battle.resolved&&battle.deadB.All(d=>d)&&battle.deadA.Any(d=>!d)&&battle.remaining==300,"Offscreen resolution eliminates the weaker faction and retains a five-minute aftermath");
        var equal=new TrafficBattle{sideA=new[]{new TrafficVehicle{strength=2}},sideB=new[]{new TrafficVehicle{strength=1},new TrafficVehicle{strength=1}},deadA=new bool[1],deadB=new bool[2]};Call("TrafficBattles","Resolve",equal);
        Check(equal.deadA.All(d=>d)&&equal.deadB.All(d=>d),"Equal weighted strengths resolve as mutual losses");
        var go=new GameObject("Faction relation fixture");var relation=go.AddComponent<PlayMakerFSM>();relation.FsmName="FactionRelation_Coyotes";
        relation.enabled=false;var flag=new FsmInt("OnOff"){Value=0};relation.FsmVariables.IntVariables=new[]{flag};relation.FsmVariables.Init();typeof(Fsm).GetField("initialized",All).SetValue(relation.Fsm,true);
        var factions=asm.GetType("Apocapatrol.TrafficFactions");factions.GetField("_relation",All).SetValue(null,relation);
        Check(!(bool)Call("TrafficFactions","PlayerHostile","Coyotes")&&(bool)Call("TrafficFactions","PlayerHostile","Scrapyard"),"Friendly Coyotes leave the player alone while Scrapyard remains hostile");
        flag.Value=1;Check((bool)Call("TrafficFactions","PlayerHostile","Coyotes"),"Coyote traffic reads the game's real faction OnOff hostility flag");
        factions.GetField("_relation",All).SetValue(null,null);UnityEngine.Object.DestroyImmediate(go);
        yield return null;
    }
    IEnumerator NativeBattleChecks()
    {
        var world=(Behaviour)asm.GetType("Apocapatrol.WorldTraffic").GetField("_instance",All).GetValue(null);world.enabled=false;
        foreach(var marker in ((IEnumerable)asm.GetType("Apocapatrol.PatrolMarker").GetField("All",All).GetValue(null)).Cast<object>().ToArray())if((UnityEngine.Object)marker!=null)Call("Cleanup","Remove",marker,"native battle fixture isolation");
        yield return null;Call("WorldTraffic","ResetForScene");
        var anchor=new GameObject("Battle stable origin fixture");asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,anchor.transform);
        TrafficSetting("WorldTrafficEnabled",true);TrafficSetting("ConvoyEnabled",false);TrafficSetting("CarSpawnLimit",true);TrafficSetting("MaxCarsInEvent",5);TrafficSetting("MaxCarsInDoubleEvent",6);
        TrafficPlayFixture.Active=true;var player=new GameObject("Native battle player");var start=new Vector3(181000,2000,181000);player.transform.position=start+Vector3.forward*80;
        asm.GetType("Apocapatrol.PlayerRef").GetField("_player",All).SetValue(null,player.transform);
        var terrainData=new TerrainData{heightmapResolution=33,size=new Vector3(6000,20,6000)};terrainData.SetHeights(0,0,new float[33,33]);
        var terrain=Terrain.CreateTerrainGameObject(terrainData);terrain.transform.position=start-new Vector3(1000,0,1000);Physics.SyncTransforms();
        Check((bool)Call("Convoy","GroundAt",new object[]{start,Vector3.zero}),"Battle fixture provides safe terrain in its stable coordinate frame");
        var file=TemplateFile.FromJson(File.ReadAllText(Path.Combine(TrailerProbe.Templates,"Example_PipeRat.json")));file.driver="Scraffa";file.passenger="";file.rider=file.rider2=file.rider3="";file.lootPreset="car-empty";
        var template=Call("CarTemplates","Parse",file.ToJson(),"Battle fixture","verification","battle",false);
        var recordsA=Enumerable.Range(0,4).Select(i=>(TrafficVehicle)Call("WorldTraffic","Freeze",template)).ToArray();Set(template,"Faction","Coyotes");
        Set(template,"Driver","Pistoleer");
        var recordsB=Enumerable.Range(0,2).Select(i=>(TrafficVehicle)Call("WorldTraffic","Freeze",template)).ToArray();
        var a=new TrafficJourney{id="battle-A",faction="Scrapyard",position=start,route=new[]{start,start+Vector3.right*2000},speed=10,vehicles=recordsA};
        var b=new TrafficJourney{id="battle-B",faction="Coyotes",position=start+Vector3.right*30,route=new[]{start,start-Vector3.right*2000},speed=10,vehicles=recordsB};
        var journeys=(IList)Get(world,"_journeys");journeys.Add(a);journeys.Add(b);Set(world,"_patrol",world.GetComponent(asm.GetType("Apocapatrol.Patrol")));
        TrafficInvoke(world,"BeginBattle",a,b);var battles=(IList)Get(world,"_battles");var site=(TrafficBattle)battles[0];
        Check(a.battle==site.id&&b.battle==site.id&&!site.resolved,"An opposed road encounter pauses both groups in one battle record");
        TrafficInvoke(world,"BattleTick",0.25f,player.transform);
        float deadline=Time.realtimeSinceStartup+60;var live=(IDictionary)Get(world,"_battleLive");
        while(Time.realtimeSinceStartup<deadline&&(!live.Contains(site.id)||(bool)Get(live[site.id],"Building")))yield return null;
        Check(live.Contains(site.id)&&!(bool)Get(live[site.id],"Building"),"Both battle factions materialize through the production vehicle builder");
        var cars=(IDictionary)Get(live[site.id],"Cars");Check(cars.Count==6&&cars.Keys.Cast<string>().Count(k=>k.StartsWith("0:"))==4&&cars.Keys.Cast<string>().Count(k=>k.StartsWith("1:"))==2,"Native skirmish builds six cars with four Scrapyard and two Coyote slots; actual="+string.Join(",",cars.Keys.Cast<string>()));
        var crew=TrafficFactionsFixtureDriver(cars.Values.Cast<GameObject>().First());
        var coyoteCar=cars.Keys.Cast<string>().Where(k=>k.StartsWith("1:")).Select(k=>(GameObject)cars[k]).First();
        var coyoteDriver=TrafficFactionsFixtureDriver(coyoteCar);Check(coyoteDriver!=null&&coyoteDriver.CompareTag("Coyotes"),"Native crew receives its chosen faction tag");
        Check((GameObject)Call("TrafficFactions","Target","Coyotes",coyoteCar.transform.position)!=player,"Friendly Coyote crews target the opposing raiders instead of the player");
        var saved=(string)Call("WorldTraffic","SaveJson");var decoded=(TrafficSave)Call("TrafficCodec","Read",saved);
        Check(decoded.battles.Length==1&&decoded.battles[0].sideA.Length==4&&decoded.battles[0].sideB.Length==2,"Battle rosters and paused journeys survive world serialization");
        TrafficInvoke(world,"UnloadBattle",site,live[site.id]);yield return null;
        player.transform.position=start+Vector3.right*4000;TrafficInvoke(world,"BattleTick",21f,player.transform);
        Check(site.resolved&&site.deadB.All(v=>v)&&journeys.Count==1&&((TrafficJourney)journeys[0]).faction=="Scrapyard"&&((TrafficJourney)journeys[0]).battle=="","Only weighted survivors resume the destination journey after offscreen resolution");
        player.transform.position=start+Vector3.forward*80;TrafficInvoke(world,"BattleTick",0.25f,player.transform);
        deadline=Time.realtimeSinceStartup+60;
        while(Time.realtimeSinceStartup<deadline&&(!live.Contains(site.id)||(bool)Get(live[site.id],"Building")))yield return null;
        Check(live.Contains(site.id)&&((IDictionary)Get(live[site.id],"Cars")).Count>0&&((IList)Get(live[site.id],"Corpses")).Count>0,"Revisiting a resolved battle lazily constructs casualty vehicles and actual corpse prefabs");
        TrafficInvoke(world,"BattleTick",301f,player.transform);yield return null;
        Check(battles.Count==0&&live.Count==0,"The skull record and its physical aftermath expire after five minutes");
        Call("WorldTraffic","ResetForScene");Call("PlayerRef","Reset");TrafficPlayFixture.Active=false;TrafficSetting("WorldTrafficEnabled",false);TrafficSetting("ConvoyEnabled",true);world.enabled=true;
        UnityEngine.Object.Destroy(player);UnityEngine.Object.Destroy(terrain);UnityEngine.Object.Destroy(terrainData);UnityEngine.Object.Destroy(anchor);
        yield return null;
    }
    GameObject TrafficFactionsFixtureDriver(GameObject car)
    {var marker=car.GetComponent(asm.GetType("Apocapatrol.PatrolMarker"));return (GameObject)marker.GetType().GetProperty("Driver",All).GetValue(marker,null);}
}

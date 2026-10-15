using System;
using System.Collections;
using System.Linq;
using Apocapatrol;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

public partial class TrailerRunner
{
    IEnumerator TrafficMerchantChecks()
    {
        var world=(Behaviour)asm.GetType("Apocapatrol.WorldTraffic").GetField("_instance",All).GetValue(null);
        world.enabled=false;Call("WorldTraffic","ResetForScene");TrafficSetting("WorldTrafficEnabled",true);TrafficSetting("ConvoyEnabled",false);
        TrafficSetting("MerchantSpawnChance",100f);TrafficSetting("MerchantCheckMinutes",5f);
        Check((int)Call("WorldTraffic","MerchantPopulation",3,100f,.999f)==3&&(int)Call("WorldTraffic","MerchantPopulation",3,0f,0f)==0,"Merchant population guarantees the full cap at 100 percent and zero at zero percent");
        Check(Mathf.Abs((float)Enumerable.Range(0,1000).Average(i=>(int)Call("WorldTraffic","MerchantPopulation",3,50f,(i+.5f)/1000f))-1.5f)<.00001f,"Fifty percent of three merchants averages exactly one and a half routes");
        Check(Mathf.Abs((float)Enumerable.Range(0,1000).Average(i=>(int)Call("WorldTraffic","MerchantPopulation",3,11f,(i+.5f)/1000f))-.33f)<.00001f,"Eleven percent of three merchants rolls one route with 33 percent probability");
        var source=Call("CarTemplate","Find","Coyote_Trader");var originalTurrets=((string[])Get(source,"Turrets")).ToArray();((string[])Get(source,"Turrets"))[0]="Gunnar";
        TrafficSetting("MaxPatrols",0);TrafficSetting("MaxConvoys",0);TrafficSetting("MaxMerchants",2);
        var player=new GameObject("Merchant route player");player.transform.position=new Vector3(101000,2002,101000);
        asm.GetType("Apocapatrol.PlayerRef").GetField("_player",All).SetValue(null,player.transform);
        var anchor=new GameObject("Merchant origin fixture");asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,anchor.transform);
        var mapObject=new GameObject("Merchant visible map fixture");var map=mapObject.AddComponent<Camera>();map.enabled=false;map.orthographic=true;map.orthographicSize=7500f;map.aspect=1.2f;map.transform.position=player.transform.position+Vector3.up*5000f;map.transform.rotation=Quaternion.Euler(90,0,0);Set(world,"_gameMap",map);
        var groups=new[]{"general","mechanic","organic"}.Select(type=>new SpawnGroup{Id="route-"+type,Name="Coyote "+type,Faction="Coyotes",Chance=10,Templates=new System.Collections.Generic.List<string>{"Coyote_Trader"},MerchantTypes=new System.Collections.Generic.List<string>{type}}).ToArray();
        var catalog=(EditorData)asm.GetType("Apocapatrol.EditorStore").GetField("Data",All).GetValue(null);var previousGroups=catalog.Merchants;catalog.Merchants=groups.ToList();TrafficSetting("MaxMerchants",3);
        Set(world,"_nextMerchant",Time.time-1f);TrafficInvoke(world,"PopulateMerchants");var scheduled=(IList)Get(world,"_nearDepartures");
        Check(scheduled.Count==3,"One 100-percent population check queues all three distinct shop routes");
        scheduled.RemoveAt(0);TrafficInvoke(world,"PopulateMerchants");Check(scheduled.Count==2,"A departed merchant is not replaced before the next interval");
        Set(world,"_nextMerchant",Time.time-1f);TrafficInvoke(world,"PopulateMerchants");Check(scheduled.Count==3,"The next scheduled check refills every missing slot at 100 percent");
        TrafficSetting("MerchantSpawnChance",0f);Set(world,"_nextMerchant",Time.time-1f);TrafficInvoke(world,"PopulateMerchants");Check(scheduled.Count==0,"Zero chance cancels pending routes at the population check");
        var population=(IList)Get(world,"_journeys");population.Add(new TrafficJourney{id="zero-chance-existing",merchant=true});Set(world,"_nextMerchant",Time.time-1f);TrafficInvoke(world,"PopulateMerchants");
        Check(population.Count==0&&!(bool)Call("WorldTraffic","DebugDeparture",groups[0],2),"Zero chance removes excess virtual routes and prevents merchant SPAWN");
        Call("WorldTraffic","ResetForScene");asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,anchor.transform);TrafficSetting("MerchantSpawnChance",100f);TrafficSetting("MaxMerchants",2);
        Check(!(bool)Call("WorldTraffic","DebugDeparture",groups[0],0)&&!(bool)Call("WorldTraffic","DebugDeparture",groups[0],1),"Group SPAWN honors zero patrol and convoy caps");
        Check((bool)Call("WorldTraffic","DebugDeparture",groups[0],2),"Merchant SPAWN queues a journey with no landmarks");
        Check(!(bool)Call("WorldTraffic","DebugDeparture",groups[0],2),"A pending merchant prevents a duplicate of its type");
        Check((bool)Call("WorldTraffic","DebugDeparture",groups[1],2)&&!(bool)Call("WorldTraffic","DebugDeparture",groups[2],2),"Pending departures count toward max merchants");
        var pending=(IList)Get(world,"_nearDepartures");float deadline=Time.realtimeSinceStartup+15f;
        while(pending.Count>0&&Time.realtimeSinceStartup<deadline){TrafficInvoke(world,"PumpNearbyRoutes");yield return null;}
        var journeys=(IList)Get(world,"_journeys");Check(journeys.Count==2,"Both merchant routes finish lazy planning without camps");
        var journey=(TrafficJourney)journeys[0];var other=(TrafficJourney)journeys[1];
        Check(journey.merchant&&journey.freeRoute&&journey.route.Length>2&&journey.vehicles.Length==1,"Merchant route is a sparse passing journey with one bus");
        float edge=PluginDistance("TrafficUnloadRange");
        Check(Vector3.Distance(journey.route.First(),player.transform.position)>edge&&Vector3.Distance(journey.route.Last(),player.transform.position)>edge&&journey.route.Any(p=>Vector3.Distance(p,player.transform.position)<125f),"Merchant travels from beyond unload range through the player's vicinity and out again");
        foreach(var crossing in journeys.Cast<TrafficJourney>())
        {
            var delta=crossing.route.Last()-crossing.route.First();Check((Mathf.Abs(delta.x)<.01f||Mathf.Abs(delta.z)<.01f)&&Mathf.Abs(delta.magnitude-(Mathf.Abs(delta.x)<.01f?15200f:18200f))<1f,"Merchant route crosses north-south or east-west with only 100 m beyond each map edge");
            Check(new[]{crossing.route.First(),crossing.route.Last()}.All(p=>{var v=map.WorldToViewportPoint(p);return v.x<0f||v.x>1f||v.y<0f||v.y>1f;}),"Both merchant route endpoints lie beyond the native map viewport");
        }
        Check((bool)Call("WorldTraffic","MerchantTypeInUse","general")&&!(bool)Call("WorldTraffic","DebugDeparture",groups[0],2),"A simulated merchant also prevents duplicate departures");
        TrafficInvoke(world,"NextMerchant");float next=(float)Get(world,"_nextMerchant")-Time.time;
        Check(next>=299f&&next<=301f,"Merchant checks use the default five-minute interval");TrafficSetting("MerchantCheckMinutes",10f);TrafficInvoke(world,"NextMerchant");Check(Mathf.Abs((float)Get(world,"_nextMerchant")-Time.time-600f)<1f,"Changing the merchant check interval changes the scheduled deadline");TrafficSetting("MerchantCheckMinutes",5f);
        var raider=new TrafficJourney{id="merchant-raider",faction="Scrapyard",position=journey.position,route=journey.route,vehicles=journey.vehicles};journeys.Clear();journeys.Add(journey);journeys.Add(raider);
        Set(world,"_nextBattleScan",0f);TrafficInvoke(world,"BattleTick",21f,player.transform);
        Check(((IList)Get(world,"_battles")).Count==0&&string.IsNullOrEmpty(journey.battle),"Crossing merchant and raider routes never start simulated combat");
        journeys.Remove(raider);journeys.Add(other);var json=(string)Call("WorldTraffic","SaveJson");Call("WorldTraffic","ResetForScene");Call("WorldTraffic","RestoreJson",json);
        asm.GetType("Apocapatrol.TrafficSpace").GetField("_anchor",All).SetValue(null,anchor.transform);
        journey=(TrafficJourney)journeys[0];Check(journey.merchant&&journey.freeRoute&&journey.vehicles[0].merchantTypes.SequenceEqual(new[]{"general"}),"Merchant route and shop type survive world save/reload");journeys.RemoveAt(1);
        var terrainData=new TerrainData{heightmapResolution=33,size=new Vector3(30000,20,30000)};terrainData.SetHeights(0,0,new float[33,33]);
        var terrain=Terrain.CreateTerrainGameObject(terrainData);terrain.transform.position=new Vector3(86000,2000,86000);Physics.SyncTransforms();
        TrafficPlayFixture.Active=true;Set(world,"_patrol",world.GetComponent(asm.GetType("Apocapatrol.Patrol")));player.transform.position=journey.position+Vector3.up*2f;
        var live=(IDictionary)Get(world,"_live");var representation=Activator.CreateInstance(world.GetType().GetNestedType("Live",All),true);live[journey.id]=representation;
        yield return (IEnumerator)TrafficInvoke(world,"Materialize",journey,representation);
        var cars=(IDictionary)Get(representation,"Cars");Check(cars.Count==1,"Simulated merchant materializes a real bus without loaded camps");
        var bus=(GameObject)cars[0];var host=bus.GetComponent(asm.GetType("Apocapatrol.MerchantHost"));deadline=Time.realtimeSinceStartup+35f;
        while(!(bool)Get(host,"Ready")&&Time.realtimeSinceStartup<deadline)yield return null;
        Check((bool)Get(host,"Ready")&&((MerchantSave)Get(host,"Data")).travelling,"Loaded passing merchant has a working native shop and travel mode");
        Check((bool)Call("EventBudget","IsConvoy",bus),"Merchant retains the never-bail convoy recovery policy");
        var agent=bus.GetComponent(asm.GetType("Apocapatrol.TrafficAgent"));object[] goal={Vector3.zero,0f,false,true};
        TrafficInvoke(agent,"Goal",goal);Check(!(bool)goal[2]&&!(bool)goal[3],"Merchant driver follows the route without pursuing the player");
        var enemy=new GameObject("Loaded merchant opponent");enemy.transform.position=bus.transform.position+Vector3.forward*10f;Call("TrafficFactions","Actor",enemy,"Scrapyard");Set(bus.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")),"FactionCheck",0f);
        Check((bool)agent.GetType().GetProperty("CanFight",All).GetValue(agent,null),"Merchant guards can engage a physically loaded opposing faction");TrafficInvoke(agent,"Goal",goal);Check(!(bool)goal[3],"A loaded raider does not divert the merchant driver");UnityEngine.Object.Destroy(enemy);yield return null;
        player.transform.position=bus.transform.position+Vector3.right*31f;journey.position=bus.transform.position;
        var keyboard=InputSystem.AddDevice<Keyboard>();InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.LeftAlt,Key.Q));InputSystem.Update();TrafficInvoke(world,"MerchantSignals");
        Check(journey.waitSeconds==0f,"Alt Q outside thirty metres does not stop the merchant");
        InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();yield return null;player.transform.position=bus.transform.position+Vector3.right*29f;
        InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.LeftAlt,Key.Q));InputSystem.Update();TrafficInvoke(world,"MerchantSignals");
        Check(keyboard.qKey.wasPressedThisFrame&&keyboard.leftAltKey.isPressed,"Shout fixture delivers a fresh Alt Q key event");
        Check(journey.waitSeconds==300f,"Actual Alt Q input within thirty metres starts a five-minute stop");InputSystem.RemoveDevice(keyboard);
        TrafficInvoke(agent,"Goal",goal);Check((bool)goal[2]&&!(bool)goal[3],"Stopped merchant driver brakes while retaining its route");
        journey.waitSeconds=0f;var hornCar=new GameObject("Merchant horn fixture");var horn=hornCar.AddComponent<PlayMakerFSM>();horn.FsmName="INPUT_Horn";
        horn.Fsm.States=new[]{new HutongGames.PlayMaker.FsmState(horn.Fsm){Name="off"},new HutongGames.PlayMaker.FsmState(horn.Fsm){Name="on"}};horn.Fsm.InitData();horn.Fsm.SetState("on");
        var drive=hornCar.AddComponent<PlayMakerFSM>();drive.FsmName="Drive";drive.Fsm.States=new[]{new HutongGames.PlayMaker.FsmState(drive.Fsm){Name="inCar"}};drive.Fsm.InitData();drive.Fsm.SetState("inCar");
        Set(world,"_hornCar",null);asm.GetType("Apocapatrol.PlayerRef").GetField("_playerCar",All).SetValue(null,hornCar);asm.GetType("Apocapatrol.PlayerRef").GetField("_playerCarDrive",All).SetValue(null,drive);
        player.transform.position=bus.transform.position+Vector3.right*101f;TrafficInvoke(world,"MerchantSignals");Check(journey.waitSeconds==0f,"Car horn outside one hundred metres does not stop the merchant");
        horn.Fsm.SetState("off");TrafficInvoke(world,"MerchantSignals");player.transform.position=bus.transform.position+Vector3.right*99f;horn.Fsm.SetState("on");TrafficInvoke(world,"MerchantSignals");
        Check(journey.waitSeconds==300f,"Actual horn FSM within one hundred metres starts a five-minute stop");Call("PlayerRef","Reset");asm.GetType("Apocapatrol.PlayerRef").GetField("_player",All).SetValue(null,player.transform);
        journey.waitSeconds=.5f;TrafficInvoke(world,"Tick",.5f);TrafficInvoke(agent,"Goal",goal);Check(journey.waitSeconds==0f&&!(bool)goal[2],"Merchant resumes its route when the stop timer expires");journey.waitSeconds=300f;
        var rider=((GameObject[])Get(bus.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")),"Riders")).First(r=>r!=null);float wounded=(float)Call("Patrol","GetHealth",rider)*.55f;Call("Patrol","SetHealth",rider,wounded);
        TrafficInvoke(world,"Unload",journey,representation);yield return null;
        Check(!live.Contains(journey.id)&&journey.waitSeconds==0f&&journey.vehicles[0].crew.merchantJson.Contains("turrets"),"Unloading ends the stop and preserves merchant stock and turret state");
        var departure=journey.position;player.transform.position=departure+Vector3.right*5000f;TrafficInvoke(world,"Tick",1f);Check(Vector3.Distance(departure,journey.position)>1f,"Unloaded merchant continues travelling on its map route");player.transform.position=journey.position+Vector3.up*2f;
        representation=Activator.CreateInstance(world.GetType().GetNestedType("Live",All),true);live[journey.id]=representation;yield return (IEnumerator)TrafficInvoke(world,"Materialize",journey,representation);bus=(GameObject)((IDictionary)Get(representation,"Cars"))[0];host=bus.GetComponent(asm.GetType("Apocapatrol.MerchantHost"));deadline=Time.realtimeSinceStartup+35f;
        while(!(bool)Get(host,"Ready")&&Time.realtimeSinceStartup<deadline)yield return null;yield return null;yield return null;yield return null;
        rider=((GameObject[])Get(bus.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")),"Riders")).First(r=>r!=null);
        Check(Mathf.Abs((float)Call("Patrol","GetHealth",rider)-wounded)<.01f,"Catching up to an unloaded merchant retains wounded turret HP");
        UnityEngine.Object.Destroy(bus);UnityEngine.Object.Destroy(player);UnityEngine.Object.Destroy(anchor);UnityEngine.Object.Destroy(hornCar);UnityEngine.Object.Destroy(terrain);UnityEngine.Object.Destroy(terrainData);UnityEngine.Object.Destroy(mapObject);catalog.Merchants=previousGroups;yield return null;
        Set(source,"Turrets",originalTurrets);Call("WorldTraffic","ResetForScene");Call("PlayerRef","Reset");TrafficPlayFixture.Active=false;TrafficSetting("WorldTrafficEnabled",false);TrafficSetting("ConvoyEnabled",true);TrafficSetting("MaxPatrols",12);TrafficSetting("MaxConvoys",8);TrafficSetting("MaxMerchants",0);world.enabled=true;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Apocapatrol;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

[HarmonyPatch]
public static class SimpleBanditNavFixture
{
    public static bool Active;public static int Calls;
    static bool Prepare(){return AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="NPCAI");}
    static MethodBase TargetMethod(){return AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="NPCAI").GetType("NPCAI.Nav").GetMethod("Next",BindingFlags.Static|BindingFlags.NonPublic);}
    static bool Prefix(Vector3 pos,Vector3 goal,ref Vector3 next,ref float pathLeft,ref bool __result)
    {if(!Active)return true;Calls++;next=pos+Vector3.right*4;pathLeft=10;__result=true;return false;}
}
public partial class TrailerRunner
{
    IEnumerator ExperimentalChecks()
    {
        TrafficSetting("CarSpawnLimit",true);TrafficSetting("MaxCarsInEvent",5);TrafficSetting("MaxCarsInDoubleEvent",6);
        for(int i=0;i<5;i++)Check((bool)Call("EventBudget","Reserve","A"),"Single encounter admits reserved car "+(i+1));
        Check(!(bool)Call("EventBudget","Reserve","A"),"One event cannot reserve a sixth car");
        Check((bool)Call("EventBudget","Reserve","B")&&!(bool)Call("EventBudget","Reserve","B"),"Overlapping events share six cars including builds in progress");
        for(int i=0;i<5;i++)Call("EventBudget","Release","A");Call("EventBudget","Release","B");
        Check((int)Call("EventBudget","Available","C")==5,"Cancelled build reservations release their budget");
        var car=new GameObject("Experimental convoy recovery fixture");car.AddComponent<Rigidbody>();
        var encounter=asm.GetType("Apocapatrol.EncounterCar");encounter.GetMethod("Attach",All).Invoke(null,new object[]{car,"A",true,false});
        Check((int)Call("EventBudget","Available","A")==4&&(int)Call("EventBudget","Available","B")==5,"Live cars and overlapping group reservations use the same budget");
        Call("EventBudget","Reserve","A");Set(car.GetComponent(encounter),"Building",true);
        Check((int)Call("EventBudget","Available","A")==4,"A building car is counted once through its reservation, not twice through its scene tag");
        Call("EventBudget","Release","A");Set(car.GetComponent(encounter),"Building",false);
        var crew=car.AddComponent(asm.GetType("Apocapatrol.Crew"));Set(crew,"_car",car);((Behaviour)crew).enabled=false;
        Check(!(bool)TrafficInvoke(crew,"OnStuck")&&!(bool)Get(crew,"_done"),"Convoy crew never bails or marks itself abandoned when stuck");
        TrafficInvoke(crew,"CheckDry");Check(!(bool)Get(crew,"_outOfFuel"),"A convoy is not abandoned by the dry-tank bailout path");
        var pilot=(Component)Call("Pilot","Attach",car);Set(pilot,"_crew",crew);
        for(int i=0;i<20;i++)TrafficInvoke(pilot,"StartRecover","rock fixture",1f);
        Check(pilot.GetType().GetProperty("State",All).GetValue(pilot,null).ToString()=="Recover"&&(int)Get(pilot,"_recoverCount")<=4,
            "Repeated rock recovery remains active, bounded and never enters permanent bailout/wait");
        Check(!(bool)Get(crew,"_vacated"),"Rock recovery never schedules the convoy's vacated-car explosion");
        Set(pilot,"_nextDecision",Time.fixedTime+1f);Set(pilot,"_stateTime",0f);TrafficInvoke(pilot,"Step");
        Check((float)Get(pilot,"_stateTime")==0f,"Driving skips planning before its next decision and retains existing controls");
        var save=new PatrolSaveData{version=11,cars=new[]{new PatrolCarData{encounterId="same convoy",convoyMember=true}}};
        var round=(PatrolSaveData)Call("PatrolPersistence","Deserialize",(byte[])Call("PatrolPersistence","Serialize",save));
        Check(round.cars[0].encounterId=="same convoy"&&round.cars[0].convoyMember,"Encounter identity and convoy persistence survive save/reload in schema 11");
        save.version=10;round=(PatrolSaveData)Call("PatrolPersistence","Deserialize",(byte[])Call("PatrolPersistence","Serialize",save));
        Check(round.cars[0].encounterId==""&&!round.cars[0].convoyMember,"Schema 10 saves remain readable with safe encounter defaults");
        var park=((IEnumerable)asm.GetType("Apocapatrol.CarTemplate").GetField("Park",All).GetValue(null)).Cast<object>().ToArray();
        string normal=(string)Get(park.First(t=>!(bool)t.GetType().GetProperty("IsTruck",All).GetValue(t,null)),"Name");
        string truck=(string)Get(park.First(t=>(bool)t.GetType().GetProperty("IsTruck",All).GetValue(t,null)),"Name");
        var definition=new SpawnGroup{Name="Budget fixture",Templates=Enumerable.Repeat(normal,8).Concat(new[]{truck}).ToList()};
        var roster=((IEnumerable)Call("EditorStore","Compose",definition,true,(Func<double>)(()=>0.5))).Cast<object>().ToArray();
        Check(roster.Length<=5&&(bool)roster[0].GetType().GetProperty("IsTruck",All).GetValue(roster[0],null),"New convoy rosters cap at five and preserve cargo truck priority");
        TrafficSetting("CarSpawnLimit",false);Check((int)Call("EventBudget","Available","A")==int.MaxValue,"Disabling car spawn limit restores the existing spawn budget");

        var worldType=asm.GetType("Apocapatrol.WorldTraffic");var world=(Behaviour)worldType.GetField("_instance",All).GetValue(null);world.enabled=false;
        Call("WorldTraffic","ResetForScene");var known=(IDictionary)asm.GetType("Apocapatrol.TrafficLocations").GetField("Known",All).GetValue(null);
        known["A"]=new TrafficBase{id="A",center=Vector3.zero};known["B"]=new TrafficBase{id="B",center=Vector3.right*10000};
        var journeys=(IList)Get(world,"_journeys");journeys.Add(new TrafficJourney{id="cap1"});journeys.Add(new TrafficJourney{id="cap2",arrived=true});
        TrafficInvoke(world,"Populate",false,2);
        Check(((IDictionary)Get(world,"_departures")).Count==0,"Fixed patrol cap counts arrived groups and does not scale with camp distances");
        TrafficInvoke(world,"Populate",true,0);Check(((IDictionary)Get(world,"_departures")).Count==0,"Zero max convoys disables convoy replenishment");
        var capPlayer=new GameObject("Population cap player fixture");capPlayer.transform.position=Vector3.right*20000;
        TrafficInvoke(world,"TrimPopulation",capPlayer.transform);
        Check(journeys.Count==2,"Lazy discovery startup preserves saved journeys until camp availability has been established");
        asm.GetType("Apocapatrol.TrafficLocations").GetField("_discoveryReady",All).SetValue(null,true);
        TrafficSetting("MaxPatrols",1);TrafficInvoke(world,"TrimPopulation",capPlayer.transform);
        Check(journeys.Count==0,"No loaded nearby camps removes virtual traffic even when the configured maximum is positive");
        TrafficSetting("MaxPatrols",3);UnityEngine.Object.Destroy(capPlayer);
        Check(typeof(Plugin).GetField("PatrolDensity",All)==null&&typeof(Plugin).GetField("ConvoyDensity",All)==null,"Density controls have been replaced by fixed max patrols/convoys");
        var plugin=(Plugin)BepInEx.Bootstrap.Chainloader.PluginInfos[Plugin.GUID].Instance;
        Check(plugin.Config.Keys.First().Section=="Experimental","Experimental options are bound first for the settings menu");

        var npcAssembly=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="NPCAI");
        var memberType=npcAssembly.GetType("NPCAI.SimpleBandits");var npc=new GameObject("Simple bandit fixture");
        TrafficSetting("SimpleConvoyBanditsAI",true);Call("SimpleBanditBridge","Mark",npc);
        Check((bool)memberType.GetMethod("Has",All).Invoke(null,new object[]{npc}),"Optional NPC Overhaul adapter marks this mod's raiders for simple AI");
        TrafficSetting("SimpleConvoyBanditsAI",false);Check(!(bool)memberType.GetMethod("Has",All).Invoke(null,new object[]{npc}),"Turning simple convoy bandits AI off restores ordinary NPC Overhaul behaviour");
        TrafficSetting("SimpleConvoyBanditsAI",true);
        var senses=npcAssembly.GetType("NPCAI.Senses");var agentType=senses.GetNestedType("Agent",All);var agent=Activator.CreateInstance(agentType,true);
        Set(agent,"Owner",npc);Set(agent,"T",npc.transform);Set(agent,"Hostile",new HashSet<string>{"Player"});Set(agent,"DetectedVar",new FsmGameObject());
        object[] assignment={agent,null,Enum.ToObject(senses.GetNestedType("Src",All),0),Time.time};
        Check(!(bool)senses.GetMethod("Assign",All).Invoke(null,assignment),"Simple bandits ignore sound/engine/gunshot investigation assignments");
        var brain=npcAssembly.GetType("NPCAI.Brain");var n=Activator.CreateInstance(brain.GetNestedType("Npc",All),true);
        Set(n,"Owner",npc);Set(n,"T",npc.transform);Set(n,"Rb",npc.AddComponent<Rigidbody>());
        var target=new GameObject("Simple target fixture");target.transform.position=Vector3.forward*20;
        senses.GetField("_player",All).SetValue(null,target);
        senses.GetMethod("SimpleLook",All).Invoke(null,new object[]{agent,Time.time});
        Check(Get(agent,"Target")==null,"Seated/simple crew has no autonomous target acquisition before a combat dismount");
        Call("SimpleBanditBridge","Fight",npc);
        senses.GetMethod("SimpleLook",All).Invoke(null,new object[]{agent,Time.time});
        Check((GameObject)Get(agent,"Target")==target&&((FsmGameObject)Get(agent,"DetectedVar")).Value==target,"Combat dismount immediately targets the real player without a sight acquisition roll");
        target.transform.position=Vector3.forward*1500;
        senses.GetMethod("SimpleLook",All).Invoke(null,new object[]{agent,Time.time+600f});
        Check((GameObject)Get(agent,"Target")==target&&Get(agent,"Ghost")==null,"Dismounted bandits never forget a distant target or replace it with a ghost");
        npc.transform.SetParent(car.transform);
        Check(!(bool)memberType.GetMethod("Fighting",All).Invoke(null,new object[]{npc}),"A bandit seated again stops its autonomous combat AI");
        npc.transform.SetParent(null);target.transform.position=Vector3.forward*20;
        Set(n,"Target",new FsmGameObject{Value=target});
        SimpleBanditNavFixture.Active=true;SimpleBanditNavFixture.Calls=0;
        brain.GetMethod("SimpleThink",All).Invoke(null,new object[]{n,Time.time});SimpleBanditNavFixture.Active=false;
        Check(SimpleBanditNavFixture.Calls==1&&(bool)Get(n,"OnNav")&&(bool)Get(n,"HasHeading")&&Mathf.Abs((float)Get(n,"Heading")-90f)<0.1f,
            "Simple bandits still request NPC Overhaul baked-map waypoints and steer to them");
        Check((float)Get(n,"NextTick")>=Time.time+0.5f,"Simple AI defers its next thought instead of running the full tactical loop");
        var enemy=new GameObject("Opposing faction target fixture");enemy.transform.position=Vector3.right*30;
        Call("SimpleBanditBridge","Target",npc,enemy);senses.GetMethod("SimpleLook",All).Invoke(null,new object[]{agent,Time.time+601f});
        Check((GameObject)Get(agent,"Target")==enemy&&((FsmGameObject)Get(agent,"DetectedVar")).Value==enemy,"Simple combat handoff pursues an explicit opposing faction target");
        Call("SimpleBanditBridge","Target",npc,null);senses.GetMethod("SimpleLook",All).Invoke(null,new object[]{agent,Time.time+602f});
        Check((GameObject)Get(agent,"Target")==null&&((FsmGameObject)Get(agent,"DetectedVar")).Value==null,"A friendly faction's null target clears simple combat instead of falling back to the player");UnityEngine.Object.Destroy(enemy);
        senses.GetField("_player",All).SetValue(null,null);
        UnityEngine.Object.Destroy(npc);UnityEngine.Object.Destroy(target);UnityEngine.Object.Destroy(car);Call("WorldTraffic","ResetForScene");
        world.enabled=true;
        yield return null;
    }
}

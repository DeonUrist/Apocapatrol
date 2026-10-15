using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace Apocapatrol
{
    [Serializable] public sealed class TrafficBattle
    {
        public string id,a,b,factionA="Scrapyard",factionB="Coyotes";
        public Vector3 position;
        public TrafficVehicle[] sideA=new TrafficVehicle[0],sideB=new TrafficVehicle[0];
        public int[] slotsA=new int[0],slotsB=new int[0];
        public bool[] deadA=new bool[0],deadB=new bool[0],wreckA=new bool[0],wreckB=new bool[0];
        public bool[] claimedA,claimedB;
        public bool resolved;
        public float elapsed,remaining=300f;
    }
    internal static class TrafficBattles
    {
        internal static int Strength(CarTemplate car)=>car.IsTruck||car.IsJunker||(car.Body??"").StartsWith("Rust",StringComparison.OrdinalIgnoreCase)?6:car.IsMotorcycle&&!car.IsSmall?1:2;
        internal static int Power(TrafficVehicle[] cars,bool[] dead)=>cars.Select((c,i)=>dead[i]?0:Math.Max(1,c.strength)).Sum();
        internal static int[] Shares(int a,int b,int cap,int perSide)
        {
            cap=Math.Max(0,Math.Min(cap,a+b));if(cap==0)return new[]{0,0};
            int left=Math.Min(a,Math.Min(perSide,(int)Math.Round((double)cap*a/Math.Max(1,a+b))));
            int right=Math.Min(b,Math.Min(perSide,cap-left));
            if(cap>=2&&a>0&&b>0){if(left==0){left=1;right=Math.Min(right,cap-1);}if(right==0){right=1;left=Math.Min(left,cap-1);}}
            while(left+right<cap){if(left<a&&left<perSide)left++;else if(right<b&&right<perSide)right++;else break;}
            return new[]{left,right};
        }
        internal static void Resolve(TrafficBattle battle)
        {
            int a=Power(battle.sideA,battle.deadA),b=Power(battle.sideB,battle.deadB);
            if(a==b){for(int i=0;i<battle.deadA.Length;i++)battle.deadA[i]=true;for(int i=0;i<battle.deadB.Length;i++)battle.deadB[i]=true;}
            else
            {
                var winner=a>b?battle.sideA:battle.sideB;var surviving=a>b?battle.deadA:battle.deadB;var losing=a>b?battle.deadB:battle.deadA;
                int strength=Math.Abs(a-b),kept=0;
                var alive=Enumerable.Range(0,winner.Length).Where(i=>!surviving[i]).OrderByDescending(i=>winner[i].strength).ToArray();
                foreach(int i in alive)
                {if(winner[i].strength<=strength){strength-=winner[i].strength;kept++;}else surviving[i]=true;}
                if(kept==0&&alive.Length>0)surviving[alive[alive.Length-1]]=false;
                for(int i=0;i<losing.Length;i++)losing[i]=true;
            }
            battle.resolved=true;battle.remaining=300f;
        }
    }
    internal sealed class BattleCar : MonoBehaviour
    {
        internal string Battle;internal int Side,Slot;
        internal bool Goal(out Vector3 goal,out float speed,out bool stop,out bool hunt)
        {
            var battle=WorldTraffic.Battle(Battle);var journey=battle!=null?WorldTraffic.Find(Side==0?battle.a:battle.b):null;
            if(journey!=null&&journey.convoy&&GetComponent<PatrolMarker>()?.BodyPrefab.StartsWith("Rust",StringComparison.OrdinalIgnoreCase)==true)
            {journey.Follow(TrafficSpace.World(transform.position),22f);goal=TrafficSpace.Scene(RoadPath.Target(journey.route,journey.leg,TrafficSpace.World(transform.position),8f));speed=journey.speed;stop=journey.arrived;hunt=false;return true;}
            var marker=GetComponent<PatrolMarker>();var target=marker!=null?TrafficFactions.Target(marker.Faction,transform.position):null;
            goal=target!=null?target.transform.position:transform.position;speed=8f;stop=target==null;hunt=target!=null;return true;
        }
    }
    internal sealed partial class WorldTraffic
    {
        private sealed class BattleLive
        {
            internal readonly Dictionary<string,GameObject> Cars=new Dictionary<string,GameObject>();
            internal readonly List<GameObject> Corpses=new List<GameObject>();
            internal bool Building,Ready;
            internal float RetryAt;
            internal int Reserved;
        }
        private readonly List<TrafficBattle> _battles=new List<TrafficBattle>();
        private readonly Dictionary<string,BattleLive> _battleLive=new Dictionary<string,BattleLive>();
        private float _nextBattleScan;
        internal static TrafficBattle Battle(string id)=>_instance?._battles.FirstOrDefault(b=>b.id==id);
        private static bool TrafficBattleValid(TrafficBattle b)
        {
            if(b==null||string.IsNullOrEmpty(b.id)||b.sideA==null||b.sideB==null||b.sideA.Length>100||b.sideB.Length>100||b.remaining<=0f||b.remaining>300f)return false;
            if(b.deadA==null||b.deadB==null||b.wreckA==null||b.wreckB==null||b.slotsA==null||b.slotsB==null)return false;
            if(float.IsNaN(b.remaining)||float.IsNaN(b.elapsed)||float.IsInfinity(b.elapsed))return false;
            if(b.claimedA==null)b.claimedA=new bool[b.sideA.Length];if(b.claimedB==null)b.claimedB=new bool[b.sideB.Length];
            return b.claimedA.Length==b.sideA.Length&&b.claimedB.Length==b.sideB.Length&&b.deadA.Length==b.sideA.Length&&b.deadB.Length==b.sideB.Length&&b.wreckA.Length==b.sideA.Length&&b.wreckB.Length==b.sideB.Length&&b.slotsA.Length==b.sideA.Length&&b.slotsB.Length==b.sideB.Length&&b.sideA.All(c=>c!=null)&&b.sideB.All(c=>c!=null);
        }
        internal static bool IsBattleCar(GameObject car)=>car!=null&&car.GetComponent<BattleCar>()!=null;
        private void BattleTick(float seconds,Transform player)
        {
            var position=TrafficSpace.World(player.position);
            foreach(var battle in _battles.ToArray())
            {
                float distance=TrafficLocations.FlatDistance(position,battle.position);
                _battleLive.TryGetValue(battle.id,out var live);
                bool near=distance<=Plugin.TrafficLoadRange.Value;
                if(live!=null&&!live.Building&&!battle.resolved)
                {
                    foreach(var pair in live.Cars)
                    {
                        var car=pair.Value;if(car==null)continue;
                        var member=car.GetComponent<BattleCar>();if(member==null)continue;
                        var crew=car.GetComponent<Crew>();
                        if(crew==null||!crew.CrewAlive||car.GetComponent<PatrolMarker>()?.Exploded==true)
                        {var dead=member.Side==0?battle.deadA:battle.deadB;if(!dead[member.Slot]){CaptureState(car,(member.Side==0?battle.sideA:battle.sideB)[member.Slot]);dead[member.Slot]=true;}}
                    }
                    bool lostA=live.Ready&&!LivingBattleCars(live,battle,0)&&!LivingBattleFeet(battle,0);
                    bool lostB=live.Ready&&!LivingBattleCars(live,battle,1)&&!LivingBattleFeet(battle,1);
                    if(lostA||lostB){ScaleBattleCasualties(battle,live,0,lostA);ScaleBattleCasualties(battle,live,1,lostB);FinishBattle(battle,false);}
                }
                if(live!=null&&!live.Building&&distance>Plugin.TrafficUnloadRange.Value)
                {UnloadBattle(battle,live);live=null;}
                if(!battle.resolved)
                {battle.elapsed+=seconds;if(live==null&&!near&&battle.elapsed>=20f)FinishBattle(battle,true);}
                else
                {battle.remaining-=seconds;if(battle.remaining<=0f){if(live!=null&&!live.Building)UnloadBattle(battle,live);if(live==null||!live.Building)_battles.Remove(battle);continue;}}
                if(near&&(live==null||(!live.Building&&!live.Ready&&Time.time>=live.RetryAt))&&!Convoy.AutosaveSoon(30f))
                {if(live==null)_battleLive[battle.id]=live=new BattleLive();StartCoroutine(MaterializeBattle(battle,live));}
            }
            if(Time.time<_nextBattleScan)return;_nextBattleScan=Time.time+1f;
            for(int a=0;a<_journeys.Count;a++)for(int b=a+1;b<_journeys.Count;b++)
            {
                var left=_journeys[a];var right=_journeys[b];
                if(left.merchant||right.merchant||left.arrived||right.arrived||!string.IsNullOrEmpty(left.battle)||!string.IsNullOrEmpty(right.battle)||!TrafficFactions.Opposed(left.faction,right.faction)
                    ||TrafficLocations.FlatDistance(left.position,right.position)>120f)continue;
                if((_live.TryGetValue(left.id,out var ll)&&ll.Building)||(_live.TryGetValue(right.id,out var rl)&&rl.Building))continue;
                BeginBattle(left,right);return;
            }
        }
        private static bool LivingBattleFeet(TrafficBattle battle,int side)
        {foreach(var actor in TrafficFactions.Actors)if(actor!=null&&actor.Battle==battle.id&&actor.Side==side&&actor.transform.parent==null&&(actor.Health==null||actor.Health.Value>0f))return true;return false;}
        private static bool LivingBattleCars(BattleLive live,TrafficBattle battle,int side)
        {foreach(var pair in live.Cars)if(pair.Key.StartsWith(side+":",StringComparison.Ordinal)&&pair.Value!=null){var tag=pair.Value.GetComponent<BattleCar>();if(tag!=null&&!(side==0?battle.deadA:battle.deadB)[tag.Slot])return true;}return false;}
        private static void ScaleBattleCasualties(TrafficBattle battle,BattleLive live,int side,bool lost)
        {
            var dead=side==0?battle.deadA:battle.deadB;
            if(lost){for(int i=0;i<dead.Length;i++)dead[i]=true;return;}
            var represented=live.Cars.Keys.Where(k=>k.StartsWith(side+":",StringComparison.Ordinal)).Select(k=>int.Parse(k.Substring(k.IndexOf(':')+1))).ToArray();
            if(represented.Length==0)return;
            int kept=represented.Count(i=>!dead[i]);int virtualKeep=Math.Max(0,(int)Math.Round((double)dead.Length*kept/represented.Length)-kept);
            for(int i=0;i<dead.Length;i++)if(!represented.Contains(i)){dead[i]=virtualKeep<=0;if(virtualKeep>0)virtualKeep--;}
        }
        private void BeginBattle(TrafficJourney a,TrafficJourney b)
        {
            if(_battles.Count>=64)return;
            var battle=new TrafficBattle{id=Guid.NewGuid().ToString("N"),a=a.id,b=b.id,factionA=a.faction,factionB=b.faction,position=(a.position+b.position)*0.5f,
                slotsA=Enumerable.Range(0,a.vehicles.Length).Where(i=>!a.vehicles[i].retired).ToArray(),slotsB=Enumerable.Range(0,b.vehicles.Length).Where(i=>!b.vehicles[i].retired).ToArray()};
            battle.sideA=battle.slotsA.Select(i=>a.vehicles[i]).ToArray();battle.sideB=battle.slotsB.Select(i=>b.vehicles[i]).ToArray();
            if(battle.sideA.Length==0||battle.sideB.Length==0)return;
            battle.deadA=new bool[battle.sideA.Length];battle.deadB=new bool[battle.sideB.Length];battle.wreckA=new bool[battle.sideA.Length];battle.wreckB=new bool[battle.sideB.Length];
            battle.claimedA=new bool[battle.sideA.Length];battle.claimedB=new bool[battle.sideB.Length];
            a.battle=b.battle=battle.id;_battles.Add(battle);
            var live=new BattleLive();
            for(int side=0;side<2;side++)
            {
                var journey=side==0?a:b;var slots=side==0?battle.slotsA:battle.slotsB;
                if(!_live.TryGetValue(journey.id,out var current))continue;
                foreach(var pair in current.Cars)if(pair.Value!=null)
                {int slot=Array.IndexOf(slots,pair.Key);if(slot<0)continue;CaptureState(pair.Value,journey.vehicles[pair.Key]);BattleAttach(pair.Value,battle,side,slot);live.Cars[side+":"+slot]=pair.Value;}
                _live.Remove(journey.id);
            }
            if(live.Cars.Count>0)_battleLive[battle.id]=live;
        }
        private void FinishBattle(TrafficBattle battle,bool auto)
        {
            if(auto)TrafficBattles.Resolve(battle);else{battle.resolved=true;battle.remaining=300f;}
            if(_battleLive.TryGetValue(battle.id,out var representation)){representation.Ready=false;representation.RetryAt=Time.time+2f;}
            for(int side=0;side<2;side++)
            {
                var records=side==0?battle.sideA:battle.sideB;var dead=side==0?battle.deadA:battle.deadB;var wreck=side==0?battle.wreckA:battle.wreckB;
                var journey=Find(side==0?battle.a:battle.b);var slots=side==0?battle.slotsA:battle.slotsB;
                for(int i=0;i<records.Length;i++)
                {
                    var record=records[i];
                    if(dead[i]){float chance=record.crew!=null?record.crew.explodeChance:!string.IsNullOrEmpty(record.definition)?TemplateFile.FromJson(record.definition).explodeChance:Explode.DefaultChance(record.template);wreck[i]=record.crew?.driverBailed!=true&&Explode.Roll(chance,Plugin.SelfDestruct.Value,UnityEngine.Random.value);}
                    if(journey!=null&&i<slots.Length&&slots[i]<journey.vehicles.Length){record.retired=dead[i];journey.vehicles[slots[i]]=record;}
                    if(_battleLive.TryGetValue(battle.id,out var live)&&live.Cars.TryGetValue(side+":"+i,out var car)&&car!=null&&!dead[i]&&journey!=null)
                    {live.Cars.Remove(side+":"+i);var member=car.GetComponent<BattleCar>();member.Battle="";Destroy(member);Attach(car,journey,slots[i]);if(!_live.TryGetValue(journey.id,out var regular))_live[journey.id]=regular=new Live();regular.Cars[slots[i]]=car;}
                }
                if(journey!=null){journey.battle="";if(journey.vehicles.All(v=>v.retired))Remove(journey);}
            }
        }
        private static void BattleAttach(GameObject car,TrafficBattle battle,int side,int slot)
        {
            var tag=car.GetComponent<BattleCar>()??car.AddComponent<BattleCar>();tag.Battle=battle.id;tag.Side=side;tag.Slot=slot;
            var marker=car.GetComponent<PatrolMarker>();marker.TrafficId="";marker.TrafficSlot=-1;marker.Faction=side==0?battle.factionA:battle.factionB;
            var agent=car.GetComponent<TrafficAgent>()??car.AddComponent<TrafficAgent>();agent.Journey="";
            EncounterCar.Attach(car,"battle:"+battle.id+":"+side,Find(side==0?battle.a:battle.b)?.convoy==true);TrafficFactions.Crew(car,marker);
        }
        private IEnumerator MaterializeBattle(TrafficBattle battle,BattleLive live)
        {
            live.Building=true;
            int countA=battle.resolved?battle.deadA.Count(v=>v):battle.sideA.Length,countB=battle.resolved?battle.deadB.Count(v=>v):battle.sideB.Length;
            int cap=Plugin.CarSpawnLimit.Value?Plugin.MaxCarsInDoubleEvent.Value:Math.Min(48,countA+countB);
            var shares=TrafficBattles.Shares(battle.sideA.Length,battle.sideB.Length,Math.Min(cap,countA+countB),Plugin.CarSpawnLimit.Value?Plugin.MaxCarsInEvent.Value:48);
            // Transfer encounters into the same proportional cap before constructing missing slots.
            if(!battle.resolved)for(int side=0;side<2;side++)
            {int kept=0;foreach(var key in live.Cars.Keys.Where(k=>k.StartsWith(side+":",StringComparison.Ordinal)).ToArray())
                {if(kept++<shares[side])continue;var car=live.Cars[key];if(car!=null){var tag=car.GetComponent<BattleCar>();CaptureState(car,(side==0?battle.sideA:battle.sideB)[tag.Slot]);Cleanup.Remove(car.GetComponent<PatrolMarker>(),"battle proportional cap");}live.Cars.Remove(key);}}
            int generation=Patrol.SceneGen;
            for(int turn=0;turn<shares.Max();turn++)for(int side=0;side<2;side++)
            {
                if(turn>=shares[side])continue;
                var records=side==0?battle.sideA:battle.sideB;var dead=side==0?battle.deadA:battle.deadB;var wreck=side==0?battle.wreckA:battle.wreckB;
                var claimed=side==0?battle.claimedA:battle.claimedB;
                if(live.Cars.Keys.Count(k=>k.StartsWith(side+":",StringComparison.Ordinal))>=shares[side])continue;
                var slots=Enumerable.Range(0,records.Length).Where(i=>(!battle.resolved||dead[i])&&(claimed==null||!claimed[i])&&!live.Cars.ContainsKey(side+":"+i)).ToArray();
                if(slots.Length==0)continue;
                int index=slots[Math.Min(slots.Length-1,(int)((double)turn*slots.Length/shares[side]))];
                string key=side+":"+index;if(live.Cars.ContainsKey(key))continue;
                if(RealCars>=Plugin.TrafficMaxRealCars.Value)continue;
                var template=Thaw(records[index]);if(template==null)continue;
                var original=!string.IsNullOrEmpty(records[index].definition)?CarTemplates.Parse(records[index].definition,"Battle casualties","world","battle",false):template.CloneForSpawn();template.Faction=side==0?battle.factionA:battle.factionB;
                template.SpawnEvent="battle:"+battle.id+":"+side;template.ConvoyMember=false;template.TrafficId="";template.TrafficSlot=-1;
                if(battle.resolved){template.Driver=template.Passenger="";for(int i=0;i<template.Turrets.Length;i++)template.Turrets[i]="";if(template.Trailer!=null){template.Trailer=template.Trailer.CloneForSpawn();for(int i=0;i<template.Trailer.Turrets.Length;i++)template.Trailer.Turrets[i]="";}}
                var world=battle.position+new Vector3((side==0?-1:1)*(18f+turn*7f),0f,turn*22f);
                if(!Convoy.GroundAt(TrafficSpace.Scene(world),out var ground))continue;
                yield return BuildBattleSlot(battle,live,side,index,template,original,ground,generation);
                if(generation!=Patrol.SceneGen)yield break;
                yield return new WaitForSeconds(0.7f);
            }
            live.Building=false;live.RetryAt=Time.time+15f;
            live.Ready=Enumerable.Range(0,2).All(side=>live.Cars.Keys.Count(k=>k.StartsWith(side+":",StringComparison.Ordinal))>=Math.Min(shares[side],Enumerable.Range(0,side==0?battle.sideA.Length:battle.sideB.Length).Count(i=>!battle.resolved||(side==0?battle.deadA:battle.deadB)[i])));
        }
        private IEnumerator BuildBattleSlot(TrafficBattle battle,BattleLive live,int side,int index,CarTemplate template,CarTemplate original,Vector3 ground,int generation)
        {
            bool pending=true;live.Reserved=1;
            _patrol.SpawnAt(template,ground+Vector3.up,Quaternion.Euler(0f,side==0?90f:-90f,0f),true,car=>
            {
                try
                {
                    if(car==null){Plugin.Verbose("Battle: failed car "+side+":"+index);return;}
                    if(generation!=Patrol.SceneGen||!Plugin.WorldTrafficEnabled.Value||!_battles.Contains(battle)){Cleanup.Remove(car.GetComponent<PatrolMarker>(),"cancelled battle build");return;}
                    live.Cars[side+":"+index]=car;BattleAttach(car,battle,side,index);
                    var record=(side==0?battle.sideA:battle.sideB)[index];ApplyState(car,record);
                    if(battle.resolved){Nwh.StopEngine(car);Nwh.SetHandbrake(car,1f);Patrol.Handbrake(car,true);if((side==0?battle.wreckA:battle.wreckB)[index])Explode.BattleWreck(car);SpawnBattleCorpses(original??template,car,live);}
                    else car.GetComponent<Crew>()?.Release();
                    Plugin.Verbose("Battle: registered "+side+":"+index+" "+car.name);
                }
                finally{pending=false;live.Reserved=0;}
            });
            while(pending&&generation==Patrol.SceneGen)yield return null;
        }
        private static void SpawnBattleCorpses(CarTemplate template,GameObject car,BattleLive live)
        {
            var names=new List<string>{template.Driver,template.Passenger};names.AddRange(template.Turrets);
            if(template.Trailer!=null)names.AddRange(template.Trailer.Turrets);
            int slot=0;foreach(var name in names)
            {
                if(string.IsNullOrEmpty(name))continue;var prefab=Prefabs.FindAny(name.Replace("_Dead","")+"_Dead")??Prefabs.FindAny("Scraffa_Dead");if(prefab==null)continue;
                var corpse=Instantiate(prefab,car.transform.position+car.transform.right*(3f+slot%2)+car.transform.forward*(slot/2f),Quaternion.identity);corpse.SetActive(true);live.Corpses.Add(corpse);slot++;
            }
        }
        private void UnloadBattle(TrafficBattle battle,BattleLive live)
        {
            foreach(var pair in live.Cars)
            {
                var car=pair.Value;if(car==null)continue;var tag=car.GetComponent<BattleCar>();var marker=car.GetComponent<PatrolMarker>();
                if(tag==null)continue;var record=(tag.Side==0?battle.sideA:battle.sideB)[tag.Slot];
                CaptureState(car,record);
                if(marker!=null&&(marker.PlayerEntered||!CanVirtualize(car,record)))
                {var claimed=tag.Side==0?battle.claimedA:battle.claimedB;if(claimed!=null)claimed[tag.Slot]=true;tag.Battle="";Destroy(tag);Detach(car);continue;}
                if(marker!=null)Cleanup.Remove(marker,"battle representation unloaded");
            }
            foreach(var corpse in live.Corpses)if(corpse!=null)Destroy(corpse);
            foreach(var actor in TrafficFactions.Actors.ToArray())if(actor!=null&&actor.Battle==battle.id&&actor.transform.parent==null)Destroy(actor.gameObject);
            _battleLive.Remove(battle.id);
        }
        internal static void BindBattle(PatrolMarker marker,PatrolCarData data)
        {
            if(_instance==null||string.IsNullOrEmpty(data.battleId))return;
            var battle=_instance._battles.FirstOrDefault(b=>b.id==data.battleId);if(battle==null)return;
            var records=data.battleSide==0?battle.sideA:battle.sideB;if(data.battleSlot<0||data.battleSlot>=records.Length)return;
            if(!_instance._battleLive.TryGetValue(battle.id,out var live))_instance._battleLive[battle.id]=live=new BattleLive();
            BattleAttach(marker.gameObject,battle,data.battleSide,data.battleSlot);live.Cars[data.battleSide+":"+data.battleSlot]=marker.gameObject;
        }
    }
}

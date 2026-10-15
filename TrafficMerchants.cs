using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using HutongGames.PlayMaker;
namespace Apocapatrol
{
    internal sealed partial class WorldTraffic
    {
        private sealed class NearbyDeparture
        {internal int Role;internal string Name,Faction,Type;internal RoadJob Incoming,Outgoing;internal TrafficVehicle[] Vehicles;}
        private readonly List<NearbyDeparture> _nearDepartures=new List<NearbyDeparture>();
        private float _nextMerchant;private PlayMakerFSM _hornFsm;private GameObject _hornCar;private bool _hornOn;
        private int RoleCount(int role)=>_journeys.Count(j=>(j.merchant?2:j.convoy?1:0)==role)+_nearDepartures.Count(d=>d.Role==role)+(role==0&&_roamingDeparture!=null?1:0)+(role<2&&_departures.ContainsKey(role==1)?1:0);
        internal static bool MerchantTypeInUse(string type)
        {return MerchantHost.All.Any(h=>h!=null&&h.Data!=null&&MerchantCatalog.Selected(h.Data.types)==type)||(_instance!=null&&(_instance._journeys.Any(j=>j.merchant&&j.vehicles.Any(v=>MerchantCatalog.Selected(v.merchantTypes)==type))||_instance._nearDepartures.Any(d=>d.Role==2&&d.Type==type)));}
        internal static bool DebugDeparture(SpawnGroup group,int role)
        {if(_instance==null||group==null||role<0||role>2||!Plugin.WorldTrafficEnabled.Value){Plugin.Log.LogWarning("Enable world traffic and select a group before creating a route.");return false;}return _instance.QueueNearby(group,role);}
        private bool QueueNearby(SpawnGroup group,int role)
        {
            if(role==2&&Plugin.MerchantSpawnChance.Value<=0f){Plugin.Log.LogWarning("Route not created: merchant spawn chance is zero.");return false;}
            int cap=role==2?Plugin.MaxMerchants.Value:role==1?Plugin.MaxConvoys.Value:Plugin.MaxPatrols.Value;
            int manual=role==2?MerchantHost.All.Count(h=>h!=null&&!Owns(h.GetComponent<PatrolMarker>())):0;
            if(RoleCount(role)+manual>=cap||RoleCount(0)+RoleCount(1)+RoleCount(2)>=Plugin.TrafficMaxGroups.Value){Plugin.Log.LogWarning("Route not created: maximum population reached.");return false;}
            var player=PlayerRef.Player;if(player==null)return false;
            var type=MerchantCatalog.Selected(group.MerchantTypes);
            if(role==2&&MerchantTypeInUse(type)){Plugin.Log.LogWarning("A "+MerchantCatalog.Label(type)+" merchant already exists.");return false;}
            var roster=role==2?group.Templates.Select(CarTemplate.Find).Where(t=>t!=null&&t.Available&&CrewFactions.Compatible(t,group.Faction)).OrderBy(t=>UnityEngine.Random.value).Take(1).Select(t=>t.CloneForSpawn()).ToList():EditorStore.ComposeFull(group,role==1,()=>UnityEngine.Random.value);
            if(roster.Count==0)return false;
            if(role==2)foreach(var car in roster){MerchantRuntime.Configure(car,group);car.MerchantTravelling=true;car.ReleaseHandbrake=true;}
            var center=TrafficSpace.World(player.position);float angle=UnityEngine.Random.Range(0f,Mathf.PI*2f);
            var direction=new Vector3(Mathf.Cos(angle),0f,Mathf.Sin(angle));var side=Vector3.Cross(direction,Vector3.up)*UnityEngine.Random.Range(-120f,120f);
            float edge=Mathf.Max(Plugin.TrafficUnloadRange.Value,Plugin.TrafficLoadRange.Value)+250f;
            if(role==2){direction=UnityEngine.Random.value<.5f?Vector3.forward:Vector3.right;if(UnityEngine.Random.value<.5f)direction=-direction;side=Vector3.Cross(direction,Vector3.up)*UnityEngine.Random.Range(-120f,120f);}
            var entry=new TrafficBase{apron=center-direction*edge+side};var middle=new TrafficBase{apron=center+side};var exit=new TrafficBase{apron=center+direction*edge+side};
            if(role==2||(role==0&&TrafficFactions.Normalize(group.Faction)=="Coyotes"))
            {entry.apron=OffscreenTrafficPoint(middle.apron,-direction);exit.apron=OffscreenTrafficPoint(middle.apron,direction);}
            _nearDepartures.Add(new NearbyDeparture{Role=role,Name=group.Name,Faction=group.Faction,Type=type,Vehicles=roster.Select(Freeze).ToArray(),Incoming=RoadPlanner.RequestRoute(entry,middle,false),Outgoing=RoadPlanner.RequestRoute(middle,exit,false)});
            Plugin.Log.LogInfo("Planning nearby route for "+group.Name);return true;
        }
        private void PumpNearbyRoutes()
        {
            foreach(var departure in _nearDepartures.ToArray())
            {
                if(!departure.Incoming.Complete||!departure.Outgoing.Complete)continue;_nearDepartures.Remove(departure);
                int cap=departure.Role==2?Plugin.MaxMerchants.Value:departure.Role==1?Plugin.MaxConvoys.Value:Plugin.MaxPatrols.Value;
                if(departure.Role==2&&Plugin.MerchantSpawnChance.Value<=0f)continue;
                if(RoleCount(departure.Role)>=cap||_journeys.Count>=Plugin.TrafficMaxGroups.Value)continue;
                if(departure.Incoming.Route==null||departure.Outgoing.Route==null)continue;
                var route=departure.Incoming.Route.Concat(departure.Outgoing.Route.Skip(1)).ToArray();
                _journeys.Add(new TrafficJourney{id=Guid.NewGuid().ToString("N"),name=departure.Name,faction=departure.Faction,source="",destination="",merchant=departure.Role==2,convoy=departure.Role==1,freeRoute=true,
                    route=route,roadLegs=departure.Incoming.Roads.Concat(departure.Outgoing.Roads.Skip(1)).ToArray(),TerrainRoute=departure.Incoming.Elevation||departure.Outgoing.Elevation,position=route[0],vehicles=departure.Vehicles,speed=(departure.Role==0?Plugin.TrafficPatrolSpeed.Value:Plugin.TrafficConvoySpeed.Value)/3.6f});
            }
        }
        private void PopulateMerchants()
        {
            if(_nextMerchant<=0f){NextMerchant();return;}if(Time.time<_nextMerchant)return;
            NextMerchant();
            int desired=MerchantPopulation(Plugin.MaxMerchants.Value,Plugin.MerchantSpawnChance.Value,UnityEngine.Random.value);
            int manual=MerchantHost.All.Count(h=>h!=null&&!Owns(h.GetComponent<PatrolMarker>()));
            foreach(var pending in _nearDepartures.Where(d=>d.Role==2).Reverse().ToArray())
            {if(RoleCount(2)+manual<=desired)break;_nearDepartures.Remove(pending);}
            // Removing excess virtual routes is necessary for the fractional roll to average correctly.
            // A physically loaded bus retains its stock and visit until the player leaves.
            foreach(var journey in _journeys.Where(j=>j.merchant).Reverse().ToArray())
            {if(RoleCount(2)+manual<=desired)break;if(!_live.ContainsKey(journey.id))Remove(journey);}
            var groups=EditorStore.Data.Merchants.Where(g=>g.Chance>0f).ToList();
            while(RoleCount(2)+manual<desired)
            {
                groups.RemoveAll(g=>MerchantTypeInUse(MerchantCatalog.Selected(g.MerchantTypes)));if(groups.Count==0)break;
                float roll=UnityEngine.Random.value*groups.Sum(g=>g.Chance);var group=groups[groups.Count-1];
                foreach(var candidate in groups){roll-=candidate.Chance;if(roll<=0f){group=candidate;break;}}
                groups.Remove(group);QueueNearby(group,2);
            }
        }
        internal static int MerchantPopulation(int maximum,float chance,float roll)
        {float expected=Mathf.Clamp(maximum,0,3)*Mathf.Clamp01(chance/100f);int whole=Mathf.FloorToInt(expected);return whole+(roll<expected-whole?1:0);}
        private void NextMerchant(){_nextMerchant=Time.time+Plugin.MerchantCheckMinutes.Value*60f;}
        internal void StopMerchants(Vector3 player,float range)
        {foreach(var journey in _journeys)if(journey.merchant&&_live.ContainsKey(journey.id)&&TrafficLocations.FlatDistance(journey.position,player)<=range&&journey.waitSeconds<=0f)journey.waitSeconds=300f;}
        private void MerchantSignals()
        {
            var player=PlayerRef.Player;if(player==null)return;var keyboard=Keyboard.current;
            if(keyboard!=null&&keyboard.qKey.wasPressedThisFrame&&(keyboard.leftAltKey.isPressed||keyboard.rightAltKey.isPressed))StopMerchants(TrafficSpace.World(player.position),30f);
            var car=PlayerRef.PlayerCar;
            if(car!=_hornCar){_hornCar=car;_hornFsm=car!=null?car.GetComponentsInChildren<PlayMakerFSM>(true).FirstOrDefault(f=>f.FsmName=="INPUT_Horn"):null;_hornOn=false;}
            bool horn=_hornFsm!=null&&_hornFsm.ActiveStateName=="on";
            if(horn&&!_hornOn)StopMerchants(TrafficSpace.World(player.position),100f);_hornOn=horn;
        }
    }
}

using System;
using System.Linq;
using UnityEngine;
namespace Apocapatrol
{
    internal sealed partial class WorldTraffic
    {
        private sealed class RoamingDeparture
        {internal RoadJob Incoming,Outgoing;internal TrafficPoi Point;internal SpawnGroup Group;internal TrafficVehicle[] Vehicles;}
        private RoamingDeparture _roamingDeparture;
        private int RoamingCap=>Convoy.HasTrafficFaction("Coyotes")?Mathf.Min(2,Mathf.CeilToInt(Plugin.MaxPatrols.Value/2f)):0;
        private void PopulateRoaming(Vector3 player)
        {
            if(_journeys.Count(j=>j.roaming)>=RoamingCap||_journeys.Count(j=>!j.merchant&&!j.convoy)+_nearDepartures.Count(d=>d.Role==0)>=Plugin.MaxPatrols.Value||_journeys.Count>=Plugin.TrafficMaxGroups.Value)
            {_roamingDeparture=null;return;}
            if(_roamingDeparture!=null)
            {
                var pending=_roamingDeparture;if(!pending.Incoming.Complete||!pending.Outgoing.Complete)return;_roamingDeparture=null;
                if(pending.Incoming.Route==null||pending.Outgoing.Route==null)return;
                var route=pending.Incoming.Route.Concat(pending.Outgoing.Route.Skip(1)).ToArray();
                var roads=pending.Incoming.Roads.Concat(pending.Outgoing.Roads.Skip(1)).ToArray();
                _journeys.Add(new TrafficJourney{id=Guid.NewGuid().ToString("N"),name=pending.Group.Name,faction="Coyotes",source="",destination=pending.Point.id,roaming=true,
                    route=route,roadLegs=roads,TerrainRoute=pending.Incoming.Elevation||pending.Outgoing.Elevation,position=route[0],vehicles=pending.Vehicles,speed=Plugin.TrafficPatrolSpeed.Value/3.6f});
                return;
            }
            var group=Convoy.TrafficGroup(false,"Coyotes");if(group==null)return;
            var roster=EditorStore.ComposeFull(group,false,()=>UnityEngine.Random.value);if(roster.Count==0)return;
            var points=TrafficLocations.Points.Values.Where(p=>TrafficLocations.FlatDistance(p.position,player)<=4000f&&TrafficLocations.FlatDistance(p.position,player)>100f
                &&!TrafficLocations.Known.Values.Any(c=>c.faction!="Coyotes"&&Mathf.Abs(p.position.x-c.center.x)<=c.size.x/2+40f&&Mathf.Abs(p.position.z-c.center.z)<=c.size.z/2+40f)).ToArray();
            if(points.Length==0)return;
            var poi=points[UnityEngine.Random.Range(0,points.Length)];
            var direction=poi.position-player;direction.y=0f;direction.Normalize();var side=Vector3.Cross(direction,Vector3.up)*(UnityEngine.Random.value>.5f?180f:-180f);
            var entry=OffscreenTrafficPoint(player+side,-direction);var exit=OffscreenTrafficPoint(poi.position+side,direction);
            var a=new TrafficBase{apron=entry};var b=new TrafficBase{apron=poi.position};var c=new TrafficBase{apron=exit};
            _roamingDeparture=new RoamingDeparture{Incoming=RoadPlanner.RequestRoute(a,b,false),Outgoing=RoadPlanner.RequestRoute(b,c,false),Point=poi,Group=group,Vehicles=roster.Select(Freeze).ToArray()};
        }
    }
}

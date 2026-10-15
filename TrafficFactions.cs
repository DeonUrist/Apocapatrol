using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
namespace Apocapatrol
{
    internal sealed class TrafficActor : MonoBehaviour
    {
        internal string Faction;
        internal string Battle;
        internal int Side;
        internal FsmFloat Health;
        private readonly List<KeyValuePair<Behaviour,System.Reflection.PropertyInfo>> _sensors=new List<KeyValuePair<Behaviour,System.Reflection.PropertyInfo>>();
        private bool _relationSet,_hostile;
        private float _next;
        private void Start()
        {
            foreach(var f in GetComponents<PlayMakerFSM>())if(f.FsmName=="Health")Health=f.FsmVariables.GetFsmFloat("Health");
            foreach(var sensor in GetComponentsInChildren<Behaviour>(true))if(sensor!=null&&(sensor.GetType().Name=="RangeSensor"||sensor.GetType().Name=="RaySensor"))
            {var property=sensor.GetType().GetProperty("AllowedTags");if(property!=null&&property.CanWrite&&property.PropertyType==typeof(string[]))_sensors.Add(new KeyValuePair<Behaviour,System.Reflection.PropertyInfo>(sensor,property));}
            Relations();
        }
        private void Relations()
        {
            bool hostile=TrafficFactions.PlayerHostile(Faction);if(_relationSet&&hostile==_hostile)return;_relationSet=true;_hostile=hostile;
            var tags=Faction=="Coyotes"?new List<string>{"Scrapyard","Mutant","Carnivore"}:new List<string>{"Coyotes","Mutant","Carnivore"};if(hostile)tags.Add("Player");var values=tags.ToArray();
            foreach(var sensor in _sensors)if(sensor.Key!=null)sensor.Value.SetValue(sensor.Key,values,null);
        }
        private void OnEnable(){if(!TrafficFactions.Actors.Contains(this))TrafficFactions.Actors.Add(this);}
        private void OnDisable(){TrafficFactions.Actors.Remove(this);}
        private void Update()
        {
            if(Time.time<_next)return;_next=Time.time+0.6f;Relations();if(transform.parent!=null)return;
            if(Health==null)foreach(var f in GetComponents<PlayMakerFSM>())if(f.FsmName=="Health")Health=f.FsmVariables.GetFsmFloat("Health");
            if(Health!=null&&Health.Value<=0f)return;
            SimpleBanditBridge.Target(gameObject,TrafficFactions.Target(Faction,transform.position));
        }
    }
    internal static class TrafficFactions
    {
        private static PlayMakerFSM _relation;private static float _findRelation;
        internal static readonly List<TrafficActor> Actors=new List<TrafficActor>();
        internal static string Normalize(string faction)=>string.Equals(faction,"Coyotes",StringComparison.OrdinalIgnoreCase)?"Coyotes":"Scrapyard";
        internal static bool Opposed(string a,string b)=>Normalize(a)!=Normalize(b);
        internal static bool PlayerHostile(string faction)
        {
            if(Normalize(faction)!="Coyotes")return true;
            if(_relation==null&&Time.time>=_findRelation)
            {_findRelation=Time.time+2f;var manager=GameObject.Find("__GameManager__");if(manager!=null)foreach(var f in manager.GetComponents<PlayMakerFSM>())if(f.FsmName=="FactionRelation_Coyotes"){_relation=f;break;}}
            return _relation!=null&&_relation.Fsm.Initialized&&(_relation.ActiveStateName=="PlayerEnemy"||_relation.FsmVariables.FindFsmInt("OnOff")?.Value>0);
        }
        internal static void Actor(GameObject npc,string faction)
        {
            if(npc==null)return;faction=Normalize(faction);npc.tag=faction;
            var actor=npc.GetComponent<TrafficActor>()??npc.AddComponent<TrafficActor>();actor.Faction=faction;
            if(npc.transform.parent==null)SimpleBanditBridge.Target(npc,Target(faction,npc.transform.position));
        }
        internal static void Crew(GameObject car,PatrolMarker marker)
        {
            Actor(marker.Driver,marker.Faction);Actor(marker.Passenger,marker.Faction);
            foreach(var rider in marker.Riders)Actor(rider,marker.Faction);
            var battle=car.GetComponent<BattleCar>();if(battle!=null)foreach(var actor in car.GetComponentsInChildren<TrafficActor>(true)){actor.Battle=battle.Battle;actor.Side=battle.Side;}
        }
        internal static void Dismount(GameObject npc,GameObject car)
        {
            Actor(npc,car!=null?car.GetComponent<PatrolMarker>()?.Faction:"Scrapyard");
            var battle=car!=null?car.GetComponent<BattleCar>():null;var actor=npc!=null?npc.GetComponent<TrafficActor>():null;
            if(battle!=null&&actor!=null){actor.Battle=battle.Battle;actor.Side=battle.Side;}
        }
        internal static GameObject Target(string faction,Vector3 position)
        {
            GameObject best=null;float distance=350f*350f;
            foreach(var actor in Actors)
            {
                if(actor==null||!Opposed(faction,actor.Faction)||(actor.Health!=null&&actor.Health.Value<=0f))continue;
                float d=(actor.transform.position-position).sqrMagnitude;if(d>=distance)continue;
                distance=d;best=actor.gameObject;
            }
            if(best!=null)return best;
            return PlayerHostile(faction)&&!PlayerRef.GodMode&&PlayerRef.Player!=null?PlayerRef.Player.gameObject:null;
        }
        internal static bool DriveTarget(GameObject car,out Vector3 pos,out Vector3 vel,out GameObject targetCar)
        {
            pos=vel=Vector3.zero;targetCar=null;
            var marker=car!=null?car.GetComponent<PatrolMarker>():null;
            var target=marker!=null?ForCar(car):null;
            if(target==null)return false;
            if(PlayerRef.Player!=null&&target==PlayerRef.Player.gameObject)return PlayerRef.Target(out pos,out vel,out targetCar);
            pos=target.transform.position;var body=target.GetComponentInParent<Rigidbody>();if(body!=null){vel=body.velocity;targetCar=body.gameObject;}return true;
        }
        internal static GameObject ForCar(GameObject car)
        {
            var marker=car!=null?car.GetComponent<PatrolMarker>():null;if(marker==null)return null;
            if(Time.time>=marker.FactionCheck){marker.FactionCheck=Time.time+0.4f;marker.FactionTarget=Target(marker.Faction,car.transform.position);}return marker.FactionTarget;
        }
    }
}

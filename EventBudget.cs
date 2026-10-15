using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class EncounterCar : MonoBehaviour
    {
        internal string Event;
        internal bool Convoy;
        private void OnDestroy(){EventBudget.Cars.Remove(this);}
        internal bool Building;
        internal static void Attach(GameObject car,string id,bool convoy,bool building=false)
        {var tag=car.GetComponent<EncounterCar>()??car.AddComponent<EncounterCar>();tag.Event=id;tag.Convoy=convoy;tag.Building=building;if(!EventBudget.Cars.Contains(tag))EventBudget.Cars.Add(tag);}
    }
    internal static class EventBudget
    {
        internal static readonly List<EncounterCar> Cars=new List<EncounterCar>();
        private static readonly Dictionary<string,int> Pending=new Dictionary<string,int>();
        internal static void Reset(){Pending.Clear();}
        internal static bool IsConvoy(GameObject car)
        {if(car==null)return false;var a=WorldTraffic.AgentOf(car);var journey=a!=null?WorldTraffic.Find(a.Journey):null;return journey!=null?journey.convoy||journey.merchant:car.GetComponent<EncounterCar>()?.Convoy==true;}
        internal static int Available(string id)
        {
            if(!Plugin.CarSpawnLimit.Value)return int.MaxValue;
            int total=0,own=0;bool other=false;var player=PlayerRef.Player;
            foreach(var tag in Cars)
            {
                if(tag==null||tag.Building)continue;
                var marker=tag.GetComponent<PatrolMarker>();if(marker!=null&&(marker.PlayerEntered||marker.Exploded))continue;
                if(player!=null&&(tag.transform.position-player.position).sqrMagnitude>Plugin.TrafficUnloadRange.Value*Plugin.TrafficUnloadRange.Value)continue;
                total++;if(tag.Event==id)own++;else other=true;
            }
            foreach(var pair in Pending){total+=pair.Value;if(pair.Key==id)own+=pair.Value;else if(pair.Value>0)other=true;}
            return Math.Min(Math.Max(0,Plugin.MaxCarsInEvent.Value-own),Math.Max(0,(other?Plugin.MaxCarsInDoubleEvent.Value:Plugin.MaxCarsInEvent.Value)-total));
        }
        internal static bool Reserve(string id)
        {if(Available(id)<=0)return false;Pending.TryGetValue(id,out int n);Pending[id]=n+1;return true;}
        internal static void Release(string id)
        {if(!Pending.TryGetValue(id,out int n))return;if(n<=1)Pending.Remove(id);else Pending[id]=n-1;}
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class SimpleBanditTag : MonoBehaviour
    {
        internal bool Combat;
        private void Start(){SimpleBanditBridge.Apply(gameObject);}
        private void OnDestroy(){SimpleBanditBridge.Forget(gameObject);SimpleBanditBridge.Tags.Remove(this);}
    }
    internal static class SimpleBanditBridge
    {
        internal static readonly List<SimpleBanditTag> Tags=new List<SimpleBanditTag>();
        private static MethodInfo _set,_state,_target;private static bool _resolved;
        internal static void Mark(GameObject who)
        {if(who==null)return;var tag=who.GetComponent<SimpleBanditTag>()??who.AddComponent<SimpleBanditTag>();if(!Tags.Contains(tag))Tags.Add(tag);Apply(who);}
        internal static void Fight(GameObject who)
        {if(who==null)return;Mark(who);who.GetComponent<SimpleBanditTag>().Combat=true;Apply(who);}
        internal static void Apply(GameObject who)
        {
            if(!_resolved)
            {
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {var api=assembly.GetType("NPCAI.Api");if(api==null)continue;_set=api.GetMethod("SetSimpleConvoyBandit",BindingFlags.Public|BindingFlags.Static);_state=api.GetMethod("SetSimpleConvoyBanditState",BindingFlags.Public|BindingFlags.Static);_target=api.GetMethod("SetSimpleConvoyBanditTarget",BindingFlags.Public|BindingFlags.Static);_resolved=true;break;}
            }
            if(_set==null||who==null)return;
            try{if(_state!=null)_state.Invoke(null,new object[]{who,Plugin.SimpleConvoyBanditsAI.Value,who.GetComponent<SimpleBanditTag>()?.Combat==true});
                else _set.Invoke(null,new object[]{who,Plugin.SimpleConvoyBanditsAI.Value});
                var actor=who.GetComponent<TrafficActor>();if(actor!=null)Target(who,TrafficFactions.Target(actor.Faction,who.transform.position));}catch(Exception e){Plugin.Verbose("Simple bandit adapter: "+e.Message);}
        }
        internal static void RefreshAll(){foreach(var tag in Tags)if(tag!=null)Apply(tag.gameObject);}
        internal static void Target(GameObject npc,GameObject target)
        {if(_target==null||npc==null)return;try{_target.Invoke(null,new object[]{npc,target});}catch(Exception e){Plugin.Verbose("Faction target: "+e.Message);}}
        internal static void Forget(GameObject who)
        {if(_set==null||ReferenceEquals(who,null))return;try{_set.Invoke(null,new object[]{who,false});}catch(Exception e){Plugin.Verbose("Simple bandit release: "+e.Message);}}
    }
}

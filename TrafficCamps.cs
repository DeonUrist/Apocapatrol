using System;
using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class CampResident : MonoBehaviour
    {
        internal string Camp;
        internal FsmFloat Health;
        internal bool Dead,Lost,WasAlive;
        private void Start(){if(Health!=null&&Health.Value>0f)WasAlive=true;}
        private void OnDestroy(){Dead=WasAlive&&Health!=null&&Health.Value<=0f;Lost=!Dead;TrafficCamps.Check(Camp);}
    }
    internal static class TrafficCamps
    {
        private sealed class Site
        {
            internal Transform Root;
            internal PlayMakerFSM[] Spawners;
            internal readonly List<CampResident> Residents=new List<CampResident>();
        }
        private static readonly Dictionary<string,Site> Sites=new Dictionary<string,Site>();
        private static readonly Dictionary<GameObject,FsmFloat> Candidates=new Dictionary<GameObject,FsmFloat>();
        private static readonly List<PlayMakerFSM> Fs=new List<PlayMakerFSM>();
        private static readonly List<GameObject> Expired=new List<GameObject>();
        private static float _next;
        private static int _cursor;
        private static bool _installed;
        internal static void Install(){if(_installed)return;_installed=true;new Harmony(Plugin.GUID+".camp-status").PatchAll(typeof(SpawnHook));}
        internal static void Reset(){Sites.Clear();Candidates.Clear();_next=0f;_cursor=0;}
        internal static void Register(string id,Transform root)
        {
            if(Sites.TryGetValue(id,out var old)&&old.Root==root)return;
            var spawners=new List<PlayMakerFSM>();
            foreach(var f in root.GetComponentsInChildren<PlayMakerFSM>(true))
                if(f.FsmName=="ItemSpawner"&&f.gameObject.name.StartsWith("EnemySpawn_",StringComparison.Ordinal))spawners.Add(f);
            Sites[id]=new Site{Root=root,Spawners=spawners.ToArray()};
        }
        internal static bool Loaded(string id)
        {return Sites.TryGetValue(id,out var site)&&site.Root!=null&&site.Root.gameObject.activeInHierarchy;}
        internal static bool Ready(string id)
        {
            if(!Sites.TryGetValue(id,out var site)||site.Root==null||!site.Root.gameObject.activeInHierarchy||site.Spawners.Length==0)return false;
            foreach(var spawner in site.Spawners)
            {
                if(spawner==null)continue;
                if(!spawner.Fsm.Initialized)return false;
                if(spawner.ActiveStateName=="spawned")continue;
                var consumed=spawner.FsmVariables.GetFsmInt("OnOff");
                if(consumed==null||consumed.Value<=0)return false;
            }
            return true;
        }
        internal static void ObserveRoot(Transform root)
        {
            if(root.parent!=null||Candidates.Count>=512||root.GetComponent<SimpleBanditTag>()!=null)return;
            Fs.Clear();root.GetComponents(Fs);
            FsmFloat health=null;bool npc=false;
            foreach(var f in Fs)if(f!=null){if(f.FsmName=="Health")health=f.FsmVariables.GetFsmFloat("Health");if(f.FsmName=="Detection")npc=true;}
            if(npc&&health!=null&&health.Value>0f)Candidates[root.gameObject]=health;
        }
        internal static void Tick()
        {
            if(Time.unscaledTime<_next)return;_next=Time.unscaledTime+1f;
            Expired.Clear();foreach(var pair in Candidates)if(pair.Key==null||pair.Value.Value<=0f)Expired.Add(pair.Key);
            foreach(var expired in Expired)Candidates.Remove(expired);
            if(Sites.Count==0)return;
            var ids=new List<string>(Sites.Keys);string id=ids[_cursor++%ids.Count];
            if(!TrafficLocations.Known.TryGetValue(id,out var camp)||camp.cleared||!Loaded(id))return;
            foreach(var pair in Candidates)
            {
                if(pair.Key==null||pair.Value.Value<=0f||pair.Key.GetComponent<CampResident>()!=null)continue;
                var p=TrafficSpace.World(pair.Key.transform.position);
                if(Mathf.Abs(p.x-camp.center.x)>camp.size.x*0.5f+35f||Mathf.Abs(p.z-camp.center.z)>camp.size.z*0.5f+35f)continue;
                Bind(id,pair.Key,pair.Value);
            }
            Check(id);
        }
        internal static void Bind(string id,GameObject npc,FsmFloat health=null)
        {
            if(npc==null||!Sites.TryGetValue(id,out var site)||npc.GetComponent<CampResident>()!=null)return;
            if(health==null)
            {Fs.Clear();npc.GetComponents(Fs);foreach(var f in Fs)if(f!=null&&f.FsmName=="Health"){health=f.FsmVariables.GetFsmFloat("Health");break;}}
            if(health==null)return;
            var tag=npc.AddComponent<CampResident>();tag.Camp=id;tag.Health=health;tag.WasAlive=health.Value>0f;site.Residents.Add(tag);
        }
        internal static bool Check(string id)
        {
            if(id==null||!TrafficLocations.Known.TryGetValue(id,out var camp)||camp.cleared||!Loaded(id)||!Ready(id))return false;
            var residents=Sites[id].Residents;if(residents.Count==0)return false;
            foreach(var resident in residents)
            {
                // Managed fields survive Unity destruction; a missing actor with positive health is a despawn, not a kill.
                if(ReferenceEquals(resident,null)||resident.Lost)return false;
                if(resident.Health!=null&&resident.Health.Value>0f)resident.WasAlive=true;
                if(!resident.WasAlive)return false;
                if(!resident.Dead&&(resident.Health==null||resident.Health.Value>0f))return false;
            }
            camp.cleared=true;TrafficLocations.Changed();Plugin.Verbose("Camp cleared: "+camp.name+" "+camp.id);return true;
        }
        [HarmonyPatch(typeof(CreateObject),"OnEnter")]
        private static class SpawnHook
        {
            private static void Postfix(CreateObject __instance)
            {
                try
                {
                    var owner=__instance.Fsm?.GameObject;var npc=__instance.storeObject?.Value;
                    if(owner==null||npc==null||!owner.name.StartsWith("EnemySpawn_",StringComparison.Ordinal))return;
                    var root=owner.transform;while(root!=null&&!TrafficLocations.IsCamp(root.name))root=root.parent;
                    if(root==null)return;
                    string id=TrafficLocations.Identity(root);Register(id,root);Bind(id,npc);
                }
                catch(Exception e){Plugin.Verbose("Camp resident hook: "+e.Message);}
            }
        }
    }
}

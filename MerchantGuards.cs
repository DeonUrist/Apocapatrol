using System.Collections;
using System.Linq;
using UnityEngine;
namespace Apocapatrol
{
    [ES3NonSerializable]
    internal sealed class MerchantGuardHealth : MonoBehaviour
    {
        internal MerchantTurret Saved;internal float Maximum;
        private IEnumerator Start()
        {
            yield return null;yield return null;
            var health=Patrol.GetHealth(gameObject);if(health<=0f)yield break;
            Maximum=Saved!=null&&Saved.maxHealth>0f?Saved.maxHealth:health*4f;
            var value=Saved!=null?Saved.health:Maximum;
            Patrol.SetHealth(gameObject,value);
            GetComponent<RiderGuard>()?.MaxHealth(Maximum);
        }
    }
    internal static class MerchantGuards
    {
        internal static void Attach(GameObject car,GameObject person,int slot)
        {
            var host=car.GetComponent<MerchantHost>();if(host?.Data==null)return;
            var health=person.GetComponent<MerchantGuardHealth>()??person.AddComponent<MerchantGuardHealth>();
            health.Saved=host.Data.turrets?.FirstOrDefault(t=>t.slot==slot);
        }
        internal static void Capture(MerchantHost host)
        {
            var marker=host.GetComponent<PatrolMarker>();if(marker==null)return;
            // A null record means construction has not finished yet. Do not overwrite saved guards
            // while a returning bus is still assembling its occupants.
            if(!host.Ready)return;
            host.Data.turrets=Enumerable.Range(0,marker.Riders.Length).Select(slot=>
            {
                var person=marker.Riders[slot];var anchor=marker.RiderAnchors[slot];var guard=person!=null?person.GetComponent<MerchantGuardHealth>():null;
                return new MerchantTurret{slot=slot,prefab=person!=null?TemplateExporter.PrefabName(person.name.Replace("(Rider)","")):"",health=person!=null?Mathf.Max(0f,Patrol.GetHealth(person)):0f,
                    maxHealth=guard!=null?guard.Maximum:0f,position=anchor!=null?anchor.localPosition:Vector3.zero,standing=person!=null&&person.GetComponent<RiderGuard>()?.StandingPose==true};
            }).ToArray();
        }
        internal static void Restore(MerchantHost host)
        {
            if(host.Data.turrets==null)return;var marker=host.GetComponent<PatrolMarker>();if(marker==null)return;
            foreach(var person in host.GetComponentsInChildren<Transform>(true).Where(t=>t.name.EndsWith("(Rider)")&&t.GetComponent<RiderGuard>()==null).ToArray()){Register.RemoveTree(person.gameObject);Object.Destroy(person.gameObject);}
            foreach(var saved in host.Data.turrets)
            {
                if(saved==null||saved.slot<0||saved.slot>=marker.Riders.Length||saved.health<=0f||string.IsNullOrEmpty(saved.prefab)||marker.Riders[saved.slot]!=null)continue;
                var platform=saved.slot<3?null:TrailerSupport.Attached(host.gameObject);if(saved.slot>=3&&platform==null)continue;
                Rider.Seat(host.gameObject,marker,saved.slot,saved.prefab,new[]{saved.position.x,saved.position.y,saved.position.z},platform,saved.standing);
            }
        }
    }
}

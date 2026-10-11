using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class TrailerRunner
{
    void RiderWakeChecks(GameObject tow, GameObject trailer, object marker, string label)
    {
        var anchors=(Transform[])Get(marker,"RiderAnchors"); var people=(GameObject[])Get(marker,"Riders");
        var positions=anchors.Select(a=>a!=null?a.localPosition:Vector3.zero).ToArray();
        var originalTow=tow.transform.position; var originalTrailer=trailer.transform.position;
        var temporaryParents=new List<GameObject>();
        try
        {
            tow.GetComponent<Rigidbody>().Sleep(); trailer.GetComponent<Rigidbody>().Sleep();
            tow.SetActive(false); trailer.SetActive(false);
            tow.transform.position+=new Vector3(350,40,20); trailer.transform.position+=new Vector3(350,40,20);
            tow.SetActive(true); trailer.SetActive(true);
            tow.GetComponent<Rigidbody>().WakeUp(); trailer.GetComponent<Rigidbody>().WakeUp();
            for(int i=0;i<people.Length;i++)
            {
                var person=people[i]; var anchor=anchors[i]; if(person==null || anchor==null)continue;
                var guard=person.GetComponent(asm.GetType("Apocapatrol.RiderAnchorGuard"));
                var rider=person.GetComponent(asm.GetType("Apocapatrol.RiderGuard"));
                bool gunner=(bool)Get(rider,"_gunner");
                var animator=person.GetComponentInChildren<Animator>();
                Check(animator==null || animator.cullingMode==AnimatorCullingMode.AlwaysAnimate,label+" turret "+(i+1)+" animation remains evaluated offscreen");
                var temporary=new GameObject("Wrong turret parent"); temporaryParents.Add(temporary);
                temporary.transform.SetParent(anchor.parent,false);
                person.transform.SetParent(temporary.transform,true); person.transform.position+=Vector3.up*25;
                person.GetComponent<Rigidbody>().isKinematic=false;
                var pose=person.GetComponent(asm.GetType("Apocapatrol.Pose"));
                if(pose!=null)
                {
                    Set(pose,"_lodFrame",-1); // exercise a refreshed pose, regardless of the test world's camera distance
                    pose.GetType().GetMethod("LateUpdate",All).Invoke(pose,null);
                }
                guard.GetType().GetMethod("LateUpdate",All).Invoke(guard,null);
                Check(person.transform.parent==anchor,label+" turret "+(i+1)+" returns to its designated anchor after reactivation");
                if(!gunner)Check(Vector3.Distance(person.transform.position,anchor.position)<.001f,label+" Warboy "+(i+1)+" feet return from a floating position to the current spot");
                else
                {
                    var hips=(Transform)Get(pose,"_hips"); var body=Get(pose,"_body");
                    var hipSetting=asm.GetType("Apocapatrol.Plugin").GetField("TurretHipHeight",All).GetValue(null);
                    float hip=(float)hipSetting.GetType().GetProperty("Value").GetValue(hipSetting,null);
                    Check(body!=null?Vector3.Distance(person.transform.position,anchor.position)<.001f:hips!=null && Vector3.Distance(hips.position,anchor.position+anchor.up*hip)<.001f,label+" gunner "+(i+1)+" retains its crouch height at the current spot");
                }
                var rb=person.GetComponent<Rigidbody>();
                Check(rb.isKinematic && Vector3.Distance(rb.position,person.transform.position)<.001f,label+" turret "+(i+1)+" physics pose follows its moved platform");
            }
            Check(Enumerable.Range(0,anchors.Length).All(i=>anchors[i]==null || Vector3.Distance(anchors[i].localPosition,positions[i])<.001f),label+" reactivation preserves every custom spot coordinate");
        }
        finally
        {
            foreach(var temporary in temporaryParents) UnityEngine.Object.DestroyImmediate(temporary);
            tow.transform.position=originalTow; trailer.transform.position=originalTrailer; Physics.SyncTransforms();
        }
    }
}

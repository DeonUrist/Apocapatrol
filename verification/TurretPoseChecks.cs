using System;
using System.Collections;
using System.Linq;
using Apocapatrol;
using UnityEngine;

public partial class TrailerRunner
{
    bool BodyFlag(object body,string name)
    {var field=body.GetType().GetField(name,All);return field!=null?(bool)field.GetValue(body):(bool)body.GetType().GetProperty(name,All).GetValue(body,null);}
    void RefreshTurretPose(Component pose){Set(pose,"_lodFrame",-1);TrafficInvoke(pose,"LateUpdate");}
    IEnumerator TurretPoseChecks()
    {
        var animations=asm.GetType("Apocapatrol.PlayerAnims");float deadline=Time.realtimeSinceStartup+30f;
        while(!(bool)animations.GetProperty("Available",All).GetValue(null,null)&&Time.realtimeSinceStartup<deadline)yield return null;
        Check((bool)animations.GetProperty("Available",All).GetValue(null,null),"Pose regression fixture uses the installed Apocaplayer animation API");
        TrafficSetting("CarSpawnLimit",false);TrafficSetting("MaxMerchants",0);TrafficPlayFixture.Active=true;
        var source=Call("CarTemplate","Find","Junker_Warboy");var template=TrafficInvoke(source,"CloneForSpawn");Set(template,"SetupOnly",true);
        ((string[])Get(template,"Turrets"))[1]="Flexa";((string[])Get(template,"Turrets"))[2]="Scrud";
        ((float[][])Get(template,"TurretPos"))[1]=new[]{-.5f,1.121f,-1f};((float[][])Get(template,"TurretPos"))[2]=new[]{-.5f,1.121f,0f};
        Set(template,"TurretStanding",new[]{false,false,true,false});
        var builder=new GameObject("Roof crouch fixture");var patrol=builder.AddComponent(asm.GetType("Apocapatrol.Patrol"));((Behaviour)patrol).enabled=false;
        GameObject car=null;yield return (IEnumerator)TrafficInvoke(patrol,"Build",template,(Vector3?)new Vector3(100000,2000,100000),Quaternion.identity,false,true,(Action<GameObject>)(g=>car=g));
        Check(car!=null,"Production builder creates the Junker Warboy setup template");yield return null;yield return null;
        var marker=car.GetComponent(asm.GetType("Apocapatrol.PatrolMarker"));var people=(GameObject[])Get(marker,"Riders");var anchors=(Transform[])Get(marker,"RiderAnchors");
        var warboy=people[0].GetComponent(asm.GetType("Apocapatrol.RiderGuard"));var pose=people[0].GetComponent(asm.GetType("Apocapatrol.Pose"));var body=Get(warboy,"_body");
        Check(body!=null&&BodyFlag(body,"Crouched"),"Unchecked Warboy starts in the native crouch animation even with setup AI disabled");
        var leg=(Transform)Get(pose,"_lLeg");var animatedLeg=leg.rotation;RefreshTurretPose(pose);
        Check(Quaternion.Angle(animatedLeg,leg.rotation)<.01f&&Vector3.Distance(people[0].transform.position,anchors[0].position)<.001f,"Warboy crouch animation is preserved without a second procedural bend or seat offset");
        TrafficInvoke(warboy,"Blend",1,.15f);Check(BodyFlag(body,"Crouched"),"Warboy keeps crouched legs during the lance throw blend");
        Set(warboy,"_standing",true);TrafficInvoke(warboy,"Blend",0,.15f);RefreshTurretPose(pose);Check(!BodyFlag(body,"Crouched")&&Vector3.Distance(people[0].transform.position,anchors[0].position)<.001f,"Explicit standing still places the Warboy's feet at the configured spot");
        Set(warboy,"_standing",false);Set(warboy,"_bike",true);Set(warboy,"_standAt",Time.time);TrafficInvoke(warboy,"Blend",3,.15f);Check(!BodyFlag(body,"Crouched"),"Motorcycle Warboy can still stand before his leap");
        TrafficInvoke(warboy,"Blend",2,.15f);Check(!BodyFlag(body,"Crouched")&&BodyFlag(body,"Airborne"),"Jump blend leaves the crouch and retains airborne animation");
        Set(warboy,"_bike",false);Set(warboy,"_standAt",-1f);TrafficInvoke(warboy,"Blend",0,.15f);
        var gunnerPose=people[1].GetComponent(asm.GetType("Apocapatrol.Pose"));var standingPose=people[2].GetComponent(asm.GetType("Apocapatrol.Pose"));RefreshTurretPose(gunnerPose);RefreshTurretPose(standingPose);
        Check(Get(gunnerPose,"_body")!=null&&BodyFlag(Get(gunnerPose,"_body"),"Crouched"),"Unchecked roof gunner uses the original crouched aiming animation");
        Check(Get(standingPose,"_body")!=null&&!BodyFlag(Get(standingPose,"_body"),"Crouched"),"Standing roof gunner keeps the standing animation");
        Check(!((bool)Call("WorldTraffic","CombatAllowed",car))&&people.All(p=>p==null||p.GetComponents<PlayMakerFSM>().All(f=>f.FsmName!="Attack"||!f.enabled)),"Restoring poses does not reactivate setup NPC combat");
        Call("PlayerAnims","Dispose",body);Set(warboy,"_body",null);TrafficInvoke(gunnerPose,"ReleaseBody");yield return null;
        foreach(int slot in new[]{0,1})
        {
            var fallback=people[slot].GetComponent(asm.GetType("Apocapatrol.Pose"));var knee=(Transform)Get(fallback,"_lLeg");var original=knee.localRotation;RefreshTurretPose(fallback);
            var hips=(Transform)Get(fallback,"_hips");
            Check(Quaternion.Angle(original,knee.localRotation)>100f&&Vector3.Distance(hips.position,anchors[slot].position+anchors[slot].up*PluginDistance("TurretHipHeight"))<.001f,"Fallback "+(slot==0?"Warboy":"gunner")+" uses squat knees and roof crouch height instead of the passenger seat pose");
        }
        UnityEngine.Object.Destroy(car);UnityEngine.Object.Destroy(builder);TrafficPlayFixture.Active=false;
    }
}

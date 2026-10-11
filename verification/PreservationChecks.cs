using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Apocapatrol;

public partial class TrailerRunner
{
    bool Preserved(GameObject car, object marker){return (bool)Call("Explode","Preserved",car,marker);}
    IEnumerator PreservationChecks()
    {
        var plugin=asm.GetType("Apocapatrol.Plugin");
        var setting=plugin.GetField("PreservedTemplates",All).GetValue(null);
        var value=setting.GetType().GetProperty("Value"); var original=value.GetValue(setting,null);
        var self=plugin.GetField("SelfDestruct",All).GetValue(null);var selfValue=self.GetType().GetProperty("Value");var originalSelf=selfValue.GetValue(self,null);
        Check((string)original=="War_rig, Truck_War_Rig","Preservation defaults contain the two exact War Rig template names");
        var car=new GameObject("Preservation rule fixture");car.AddComponent<Rigidbody>().isKinematic=true;
        var marker=Call("PatrolMarker","Attach",car,"Rustliner","",null,"",null,Call("CarTemplate","ParseRams","Pedestrians"));
        var trailer=new GameObject("WarRig(Clone)123");
        try
        {
            selfValue.SetValue(self,true,null);
            Set(marker,"TemplateName","War_rig");Check(Preserved(car,marker),"War Rig bus is exempt");
            Set(marker,"_bodyPrefab","Rustchief");Set(marker,"TemplateName","Truck_War_Rig");Set(marker,"Trailer",trailer);
            Check(Preserved(car,marker),"War Rig truck/tanker template is exempt");
            Set(marker,"TemplateName","Ordinary_Truck");Check(!Preserved(car,marker),"Known ordinary template is not exempt merely because it has the same chassis/trailer");
            Set(marker,"TemplateName","");Check(Preserved(car,marker),"Legacy truck identity is recovered from its WarRig trailer");
            Set(marker,"Trailer",null);Set(marker,"_bodyPrefab","Rustliner");Check(Preserved(car,marker),"Legacy bus identity remains supported");
            value.SetValue(setting," , MY_Custom, war_RIG , ,",null);Set(marker,"TemplateName","My_Custom");Check(Preserved(car,marker),"Player list ignores case, whitespace and empty entries");
            Set(marker,"TemplateName","My_Custom_Extended");Check(!Preserved(car,marker),"Player entries match complete template names");
            Set(marker,"TemplateName","Truck_War_Rig");Check(!Preserved(car,marker),"Removing a rig name removes its exemption");
            value.SetValue(setting,"",null);Set(marker,"TemplateName","War_rig");Check(!Preserved(car,marker),"Empty list disables all preservation");
            var routineType=asm.GetType("Apocapatrol.Explode");
            var pending=(IEnumerator)routineType.GetMethod("Routine",All).Invoke(null,new object[]{car,marker,"pending preservation test"});
            Check(pending.MoveNext(),"Self-destruct enters its normal delay");
            value.SetValue(setting,"War_rig",null);
            Check(!pending.MoveNext() && !(bool)Get(marker,"Exploded"),"A newly added exemption cancels an already pending explosion");
            value.SetValue(setting,"",null);
            var normal=(IEnumerator)routineType.GetMethod("Routine",All).Invoke(null,new object[]{car,marker,"ordinary self-destruct test"});normal.MoveNext();normal.MoveNext();
            Check((bool)Get(marker,"Exploded"),"Unlisted vehicle still enters normal self-destruct");
            (normal as IDisposable)?.Dispose();
            Set(marker,"Exploded",false);value.SetValue(setting,"War_rig",null);
            var snap=(PatrolCarData)marker.GetType().GetMethod("Snapshot",All).Invoke(marker,null);
            Check(snap.templateName=="War_rig","Vehicle snapshot records template identity");
            var data=new PatrolSaveData{version=9,saveFile="verification.es3",worldSeed=123,convoyCooldown=12,lastGroup="g",cars=new[]{snap,new PatrolCarData{carName="c2",bodyPrefab="Junker",templateName="My_Custom"}}};
            var bytes=(byte[])Call("PatrolPersistence","Serialize",data);
            var restored=(PatrolSaveData)Call("PatrolPersistence","Deserialize",bytes);
            Check(restored.cars.Length==2 && restored.cars[0].templateName=="War_rig" && restored.cars[1].templateName=="My_Custom" && restored.lastGroup=="g","Schema 9 preserves distinct template names and following fields");
            data.version=8;bytes=(byte[])Call("PatrolPersistence","Serialize",data);restored=(PatrolSaveData)Call("PatrolPersistence","Deserialize",bytes);
            Check(restored.cars.Length==2 && restored.cars.All(c=>c.templateName=="") && restored.convoyCooldown==12 && restored.lastGroup=="g","Schema 8 saves remain readable without template-name bytes");
            var loadedCar=new GameObject("Restored preservation fixture");loadedCar.AddComponent<Rigidbody>().isKinematic=true;
            try
            {
                Call("PatrolPersistence","RestoreCar",loadedCar,new PatrolCarData{bodyPrefab="Rustliner",templateName="War_rig",driverPhase="Released"});
                var loadedMarker=loadedCar.GetComponent(asm.GetType("Apocapatrol.PatrolMarker"));
                Check((string)Get(loadedMarker,"TemplateName")=="War_rig" && Preserved(loadedCar,loadedMarker),"Restored vehicle retains its template exemption");
            }finally{UnityEngine.Object.DestroyImmediate(loadedCar);}
        }
        finally
        {
            value.SetValue(setting,original,null);selfValue.SetValue(self,originalSelf,null);
            UnityEngine.Object.DestroyImmediate(car);UnityEngine.Object.DestroyImmediate(trailer);
        }
        yield break;
    }
}

using System;
using System.Collections;
using System.Linq;
using Apocapatrol;
using UnityEngine;
public partial class TrailerRunner
{
    IEnumerator PreservationChecks()
    {
        TrafficSetting("SelfDestruct",false);
        Check(!(bool)typeof(Plugin).GetField("SelfDestruct",All).GetValue(null).GetType().GetProperty("Value").GetValue(typeof(Plugin).GetField("SelfDestruct",All).GetValue(null),null),"Always explode cars is off by default");
        Check((float)Call("Explode","DefaultChance","War_rig")==0f&&(float)Call("Explode","DefaultChance","Truck_War_Rig")==0f
            &&(float)Call("Explode","DefaultChance","Ordinary")==50f,"War Rig bus/truck default to zero; other templates default to fifty percent");
        Check(!(bool)Call("Explode","Roll",50f,false,0.51f)&&(bool)Call("Explode","Roll",50f,false,0.49f),"Template probability rolls at the configured percentage boundary");
        Check((bool)Call("Explode","Roll",0f,true,0.999f),"Always explode overrides zero chance without changing eligibility");
        var car=new GameObject("Explosion eligibility fixture");car.AddComponent<Rigidbody>().isKinematic=true;
        var marker=Call("PatrolMarker","Attach",car,"PipeRat","",null,"",null,Call("CarTemplate","ParseRams","Pedestrians"));
        Check(!(bool)Call("Explode","Eligible",marker),"An empty car with no observed driver death never explodes");
        Set(marker,"DriverDied",true);Check((bool)Call("Explode","Eligible",marker),"Driver death permits the explosion check");
        Set(marker,"DriverBailed",true);Check(!(bool)Call("Explode","Eligible",marker),"An alive driver bailout cancels explosion even if a prior driver died");
        Set(marker,"DriverBailed",false);Set(marker,"PlayerEntered",true);Check(!(bool)Call("Explode","Eligible",marker),"Player-owned cars remain protected");
        Set(marker,"PlayerEntered",false);
        var living=new GameObject("Turret survival fixture");var hp=living.AddComponent<PlayMakerFSM>();hp.FsmName="Health";hp.FsmVariables.FloatVariables=new[]{new HutongGames.PlayMaker.FsmFloat("Health"){Value=100}};
        hp.FsmVariables.Init();
        ((GameObject[])Get(marker,"Riders"))[0]=living;
        Check((bool)Call("Explode","LivingOccupants",marker),"A living turret prevents the explosion until it vacates");
        ((GameObject[])Get(marker,"Riders"))[0]=null;
        Check(!(bool)Call("Explode","LivingOccupants",marker),"Empty seats and turret slots release the final occupant gate");
        var data=new PatrolSaveData{version=12,cars=new[]{new PatrolCarData{faction="Coyotes",explodeChance=31,driverBailed=true,explodeRolled=true,explodeSelected=false,battleId="saved-battle",battleSide=1,battleSlot=4}}};
        var restored=(PatrolSaveData)Call("PatrolPersistence","Deserialize",(byte[])Call("PatrolPersistence","Serialize",data));
        Check(restored.cars[0].faction=="Coyotes"&&restored.cars[0].explodeChance==31&&restored.cars[0].driverBailed&&restored.cars[0].explodeRolled
            &&restored.cars[0].battleId=="saved-battle"&&restored.cars[0].battleSlot==4,"Schema twelve retains faction, explosion roll and battle membership");
        data.version=11;restored=(PatrolSaveData)Call("PatrolPersistence","Deserialize",(byte[])Call("PatrolPersistence","Serialize",data));
        Check(restored.cars[0].faction=="Scrapyard"&&restored.cars[0].explodeChance==50&&restored.cars[0].battleId=="","Earlier car saves remain readable with safe faction and chance defaults");
        UnityEngine.Object.DestroyImmediate(car);UnityEngine.Object.DestroyImmediate(living);yield break;
    }
}

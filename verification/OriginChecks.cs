using System;
using System.Collections;
using System.Linq;
using Apocapatrol;
using HarmonyLib;
using UnityEngine;
using NwhOriginFixture = NWH.Common.FloatingOrigin.FloatingOrigin;

public partial class TrailerRunner
{
    void OriginChecks(Component world, TrafficJourney real, Component agent, GameObject player, GameObject anchor)
    {
        var originObject = new GameObject("Native floating-origin fixture");
        var origin = originObject.AddComponent<NwhOriginFixture>();
        origin.enabled = false; // explicit jumps only; the game's production Jump body runs below
        var method = typeof(NwhOriginFixture).GetMethod("Jump",All);
        var cameraPosition = typeof(NwhOriginFixture).GetField("_cameraPosition",All);
        Check(Harmony.GetPatchInfo(method).Postfixes.Any(p=>p.owner==Plugin.GUID+".traffic-origin"),
            "Origin synchronization patches the active NWH FloatingOrigin Jump method");
        foreach(var liveOrigin in UnityEngine.Object.FindObjectsOfType<NwhOriginFixture>()) liveOrigin.enabled=false;
        var virtualRecord = new TrafficJourney{id="origin-virtual",name="Origin virtual patrol",source="A",destination="B",
            position=real.position+Vector3.right*2500f, route=new[]{real.position+Vector3.right*2500f,real.position+Vector3.right*4000f},
            speed=10f,vehicles=new[]{new TrafficVehicle{template="fixture"}}};
        var records=(IList)Get(world,"_journeys");records.Add(virtualRecord);
        var initialReal=real.position;var initialVirtual=virtualRecord.position;
        var realRoute=real.route.ToArray();var virtualRoute=virtualRecord.route.ToArray();
        var known=(IDictionary)asm.GetType("Apocapatrol.TrafficLocations").GetField("Known",All).GetValue(null);
        var firstCamp=(TrafficBase)known["A"];var campPosition=firstCamp.center;
        var campRoot=new GameObject("Camp_12(Clone)");campRoot.transform.SetParent(anchor.transform,false);campRoot.transform.localPosition=new Vector3(200,0,400);
        var locationType=asm.GetType("Apocapatrol.TrafficLocations");
        string identity=(string)locationType.GetMethod("Identity",All).Invoke(null,new object[]{campRoot.transform});
        var pilot=(Component)Call("Pilot","Attach",agent.gameObject);
        Set(pilot,"_aim",agent.transform.position+Vector3.forward*50f);
        Set(pilot,"_runStart",agent.transform.position-Vector3.forward*30f);
        var aim=(Vector3)Get(pilot,"_aim");var run=(Vector3)Get(pilot,"_runStart");
        Set(agent,"LastKnown",real.position+new Vector3(20,0,40));var memory=(Vector3)Get(agent,"LastKnown");
        var playerType=asm.GetType("Apocapatrol.PlayerRef");
        playerType.GetField("_lastPos",All).SetValue(null,player.transform.position);
        var last=(Vector3)playerType.GetField("_lastPos",All).GetValue(null);
        var debugCameraObject=new GameObject("Native debug-map camera fixture");debugCameraObject.transform.SetParent(anchor.transform,false);
        debugCameraObject.transform.position=real.position+new Vector3(0,100,0);debugCameraObject.transform.rotation=Quaternion.Euler(90,0,0);
        var debugCamera=debugCameraObject.AddComponent<Camera>();debugCamera.orthographic=true;debugCamera.orthographicSize=200f;
        Set(world,"_gameMap",debugCamera);var targetPixels=(Vector2)TrafficInvoke(world,"MapPoint",real.position,new Rect(0,0,400,400));
        Check(world.GetType().GetMethod("FitMap",All)==null,"Standalone minimap and its Fit camps controls have been removed");
        var stablePlayer=(Vector3)Call("TrafficSpace","World",player.transform.position);
        var sceneCar=agent.transform.position;
        var shifts=new[]{new Vector3(1250,25,-950),new Vector3(-1150,-10,1450)};
        var total=Vector3.zero;
        foreach(var jump in shifts)
        {
            cameraPosition.SetValue(origin,jump);method.Invoke(origin,null);total-=jump;
            Check((Vector3)Call("TrafficSpace","World",player.transform.position)==stablePlayer,
                "Native kilometre-scale NWH jump preserves stable player coordinates: "+jump);
            Check(real.position==initialReal && virtualRecord.position==initialVirtual && real.route.SequenceEqual(realRoute)
                && virtualRecord.route.SequenceEqual(virtualRoute) && firstCamp.center==campPosition,
                "Native origin jump preserves real/simulated route records and camp positions");
            Check((string)locationType.GetMethod("Identity",All).Invoke(null,new object[]{campRoot.transform})==identity,
                "Camp identity survives origin rebasing without duplicate discovery");
            Check(Vector2.Distance((Vector2)TrafficInvoke(world,"MapPoint",real.position,new Rect(0,0,400,400)),targetPixels)<0.01f,
                "Origin jump leaves a stationary patrol at the same map pixel");
        }
        Check(agent.transform.position==sceneCar+total && (Vector3)Call("TrafficSpace","World",agent.transform.position)==initialReal,
            "Physical traffic stays aligned with its stable route after repeated real game shifts");
        Check((Vector3)Get(pilot,"_aim")==aim+total && (Vector3)Get(pilot,"_runStart")==run+total,
            "Committed attack aim and run-out start translate with the scene");
        Check((Vector3)Get(agent,"LastKnown")==memory && (Vector3)playerType.GetField("_lastPos",All).GetValue(null)==last+total,
            "Patrol detection memory stays stable and player velocity history translates");
        var beforeDistance=(initialVirtual-stablePlayer).magnitude;
        Check(Mathf.Abs(((Vector3)Call("TrafficSpace","Scene",initialVirtual)-player.transform.position).magnitude-beforeDistance)<0.05f,
            "Load/unload distance is unchanged by origin rebasing");
        object[] goal={Vector3.zero,0f,false,false};real.convoy=true;Set(agent,"Truck",true);
        TrafficInvoke(agent,"Goal",goal);
        var roadTarget=(Vector3)Call("RoadPath","Target",real.route,real.leg,(Vector3)Call("TrafficSpace","World",agent.transform.position),agent.GetComponent<Rigidbody>().velocity.magnitude);
        Check((Vector3)goal[0]==(Vector3)Call("TrafficSpace","Scene",roadTarget),
            "Convoy driver receives the route lookahead in the current shifted scene frame");
        real.convoy=false;Set(agent,"Truck",false);
        TrafficInvoke(virtualRecord,"Advance",5f);
        Check(virtualRecord.position.x==initialVirtual.x+50f,"Virtual travel advances normally after multiple coordinate shifts");
        var saved=(string)Call("WorldTraffic","SaveJson");
        var decoded=(TrafficSave)Call("TrafficCodec","Read",saved);
        Check(decoded.version==2 && decoded.journeys.Single(j=>j.id==real.id).position==initialReal,
            "Traffic saves persist MapMagic-local positions instead of shifted scene coordinates");
        // Simulate another restored MapMagic offset with a fresh NWH TotalOffset; records must still mean the same world positions.
        var oldAnchor=anchor.transform.position;
        anchor.transform.position=new Vector3(-6000,120,3500);
        var restored=(TrafficSave)Call("TrafficCodec","Read",saved);
        Check((Vector3)Call("TrafficSpace","Scene",restored.journeys.Single(j=>j.id==real.id).position)==initialReal+anchor.transform.position,
            "Save reload uses restored MapMagic offset even when NWH accumulated offset is unrelated");
        var legacy=new TrafficSave{version=1,bases=new[]{new TrafficBase{id="Camp_1:-5800:3900",name="Camp_1",center=new Vector3(-5800,120,3900),apron=new Vector3(-5790,120,3900)}},
            journeys=new[]{new TrafficJourney{id="old",source="Camp_1:-5800:3900",position=new Vector3(-5700,120,3900),route=new[]{new Vector3(-5800,120,3900),new Vector3(-5500,120,3900)},speed=10f}}};
        Call("WorldTraffic","MigrateSceneCoordinates",legacy);
        Check(legacy.version==2 && legacy.bases[0].id=="Camp_1:200:400" && legacy.journeys[0].position==new Vector3(300,0,400)
            && legacy.journeys[0].source==legacy.bases[0].id,
            "Original prototype saves migrate coordinates and camp membership once");
        anchor.transform.position=oldAnchor;
        records.Remove(virtualRecord);
        player.transform.position=agent.transform.position+new Vector3(0,2,80);playerType.GetField("_targetStamp",All).SetValue(null,-1f);
        UnityEngine.Object.DestroyImmediate(campRoot);UnityEngine.Object.DestroyImmediate(originObject);
    }
}

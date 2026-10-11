using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Apocapatrol;
using System.Collections.Generic;
using ES3Internal;

[BepInPlugin("com.denis.apocapatrol.trailer-verifier", "Trailer Verifier", "0.1.0")]
[BepInDependency(Apocapatrol.Plugin.GUID)]
public class TrailerProbe : BaseUnityPlugin
{
    public static string Output, Templates;
    void Awake() { var arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-trailer-output=")); if(arg == null) return; Output = arg.Substring(arg.IndexOf('=') + 1); Directory.CreateDirectory(Output); Templates = Environment.GetCommandLineArgs().First(a => a.StartsWith("-trailer-templates=")).Substring(19); Application.runInBackground = true; new Harmony("trailer.verifier").PatchAll(typeof(TrailerProbe).Assembly); var h = new GameObject("TrailerVerifier") { hideFlags = HideFlags.HideAndDontSave }; DontDestroyOnLoad(h); h.AddComponent<TrailerRunner>().StartCoroutine("Run"); }
    [HarmonyPatch(typeof(PlayMakerFSM), "OnEnable")]
    class BlockWorld { static bool Prefix(PlayMakerFSM __instance) { return false; } }
    [HarmonyPatch]
    class NoActiveSave { static MethodBase TargetMethod() { return typeof(Apocapatrol.Plugin).Assembly.GetType("Apocapatrol.Patrol").GetMethod("SaveBusy",BindingFlags.Instance|BindingFlags.NonPublic); } static bool Prefix(ref bool __result) { __result=false; return false; } }
}
public partial class TrailerRunner : MonoBehaviour
{
    void Awake() { File.WriteAllText(Path.Combine(TrailerProbe.Output,"progress.txt"),"Runner awake"); }
    void Update() { Application.runInBackground = true; }
    IEnumerator Run()
    {
        File.AppendAllText(Path.Combine(TrailerProbe.Output,"progress.txt")," before wait"); yield return new WaitForSecondsRealtime(2); File.AppendAllText(Path.Combine(TrailerProbe.Output,"progress.txt")," after wait");
        yield return SceneManager.LoadSceneAsync(1);
        File.AppendAllText(Path.Combine(TrailerProbe.Output,"progress.txt")," before wait"); yield return new WaitForSecondsRealtime(2); File.AppendAllText(Path.Combine(TrailerProbe.Output,"progress.txt")," after wait");
        var s = new StringBuilder();
        foreach(var go in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g != null && !g.scene.IsValid() && g.transform.parent == null && (g.name == "Poloska" || g.name == "PipeRat" || g.name == "Rustchief" || g.GetComponents<PlayMakerFSM>().Any(f=>f.FsmName=="TrailerAttached") || g.name.ToLowerInvariant().Contains("trailer"))))
        {
            s.AppendLine("PREFAB " + go.name + " tag=" + go.tag); foreach(var f in go.GetComponents<PlayMakerFSM>()) s.AppendLine("ROOT FSM " + f.FsmName + " bools=" + string.Join(",",f.FsmVariables.BoolVariables.Select(v=>v.Name+"="+v.Value).ToArray()));
            foreach(var t in go.GetComponentsInChildren<Transform>(true)) s.AppendLine("  " + t.name + " " + t.localPosition + " " + string.Join(",",t.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name).ToArray()));
            foreach(var f in go.GetComponentsInChildren<PlayMakerFSM>(true).Where(f=>f.FsmName.ToLowerInvariant().Contains("trailer")))
            {
                s.AppendLine("FSM " + f.gameObject.name + " / " + f.FsmName);
                foreach(var st in f.Fsm.States) { s.AppendLine(" STATE " + st.Name); foreach(var a in st.Actions) { s.AppendLine("  ACTION " + a.GetType().Name); foreach(var field in a.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance)) { var v=field.GetValue(a); s.AppendLine("    "+field.Name+"="+v); if(v is HutongGames.PlayMaker.FsmProperty) {foreach(var p in v.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance))s.AppendLine("      "+p.Name+"="+p.GetValue(v));} } } }
            }
        }
        File.WriteAllText(Path.Combine(TrailerProbe.Output,"prefabs.txt"),s.ToString());
        var test = Checks();
        var stack = new Stack<IEnumerator>(); stack.Push(test);
        while (stack.Count > 0)
        {
            object next = null; Exception failure = null;
            try { if (!stack.Peek().MoveNext()) { stack.Pop(); continue; } next = stack.Peek().Current; }
            catch (Exception e) { failure = e; }
            if (failure != null) { lines.Add("FAIL: " + failure); break; }
            if (next is IEnumerator) stack.Push((IEnumerator)next); else yield return next;
        }
        File.WriteAllLines(Path.Combine(TrailerProbe.Output,"report.txt"), lines);
        Application.Quit();
    }

    static readonly Assembly asm = typeof(Apocapatrol.Plugin).Assembly;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    readonly List<string> lines = new List<string>();
    Component ui; MethodInfo uiDraw; bool drawUi;
    void OnGUI()
    {
        if(!drawUi) return;
        var old=(GUISkin)Call("LedgerSkin","Begin");
        try { GUI.Window(80127,new Rect(16,16,1028,660),id=>uiDraw.Invoke(ui,new object[]{id}),"",GUIStyle.none); }
        finally { GUI.skin=old; }
    }
    object Call(string type, string method, params object[] args) { return asm.GetType("Apocapatrol." + type).GetMethod(method,All).Invoke(null,args); }
    object Get(object o,string name) { return o.GetType().GetField(name,All).GetValue(o); }
    void Set(object o,string name,object value) { o.GetType().GetField(name,All).SetValue(o,value); }
    void Check(bool yes,string text) { if(!yes) throw new Exception(text); lines.Add("PASS: "+text); }
    GameObject Asset(string name) { return (GameObject)Call("Prefabs","FindAny",name); }
    Transform Find(GameObject go,string name) { return (Transform)Call("Patrol","FindChild",go.transform,name); }
    IEnumerator Checks()
    {
        Time.timeScale = 1;
        // These fixtures spawn above the world and test construction/serialization, not driving.
        // Keep their geometry stable for the native 20 cm hitch raycast and optional UI screenshots.
        Physics.autoSimulation = false;
        yield return PreservationChecks();
        yield return ModVehicleChecks();
        // All shipped JSON stays compatible, including nested attachments and the three older turret spots.
        foreach(var path in Directory.GetFiles(TrailerProbe.Templates,"*.json"))
        {
            var f=TemplateFile.FromJson(File.ReadAllText(path)); var r=TemplateFile.FromJson(f.ToJson());
            Check(r.ToJson()==f.ToJson(), "Shipped template round trip including trailer data: " + f.name);
            bool missingWarRig = f.trailer!=null && f.trailer.body=="WarRig" && !AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="WarRig");
            Check(missingWarRig?!Available(Parse(f)):Available(Parse(f)), "Shipped template availability follows loaded mods: " + f.name);
        }
        var captured=TemplateFile.FromJson(File.ReadAllText(Path.Combine(TrailerProbe.Templates,"Truck_War_Rig.json")));
        var defaultRig=Call("CarTemplate","Find","Truck_War_Rig");
        Check(defaultRig!=null && (bool)Get(defaultRig,"IsDefault") && (bool)Get(defaultRig,"Favorite"),"Captured Truck_War_Rig is an embedded protected default");
        var defaultTrailer=Get(defaultRig,"Trailer");
        var positions=(float[][])Get(defaultTrailer,"TurretPos"); var capturedCrew=(string[])Get(defaultTrailer,"Turrets");
        Check(Enumerable.Range(0,4).All(i=>positions[i].SequenceEqual(captured.trailer.TurretPos(i)) && capturedCrew[i]==captured.trailer.Turret(i)),"Embedded default preserves all four saved turret coordinates and crew");
        var editorDefaults=Call("EditorStore","Defaults");
        var rigGroup=((IEnumerable)Get(editorDefaults,"Patrols")).Cast<object>().Single(g=>(string)Get(g,"Name")=="Truck War Rig");
        Check(((IEnumerable)Get(rigGroup,"Templates")).Cast<string>().SequenceEqual(new[]{"Weak_Warboy","Weak_Warboy","Weak_Warboy","Truck_War_Rig"}) && (float)Get(rigGroup,"MinKm")==60 && (int)Get(rigGroup,"MinBosses")==5 && (float)Get(rigGroup,"Chance")==10,"Embedded Truck War Rig patrol retains the captured roster and unlock/chance settings");
        var fixture=TemplateFile.FromJson(File.ReadAllText(Path.Combine(TrailerProbe.Templates,"Example_PipeRat.json")));
        fixture.driver=fixture.passenger=""; fixture.bumpers=new string[0]; fixture.lootPreset="car-empty";
        fixture.rider="Warboy"; fixture.rider2="Flexa"; fixture.rider3="Scrud";
        foreach(string size in new[]{"Trailer_Small","Trailer_Big","Scrapwagon"})
        {
            bool chief=size=="Scrapwagon";
            fixture.body=chief?"Rustchief":"PipeRat";fixture.kind=chief?"truck":"small";
            fixture.name="Verifier_"+size; fixture.schema=3;
            fixture.trailer=new TemplateFile{body=size,parts=new[]{new TemplatePart{hinge="hinge_wheel_L",prefab="small_wheel_1"},new TemplatePart{hinge="hinge_wheel_R",prefab="small_wheel_1"}}};
            string[] people={"Warboy","Flexa","Scrud","Boltjaw"};
            for(int i=0;i<4;i++){fixture.trailer.SetTurret(i,people[i]);fixture.trailer.SetTurretPos(i,new[]{(i%2==0?-.4f:.4f),.8f,(i<2?.5f:-.5f)});}
            var f=TemplateFile.FromJson(fixture.ToJson());
            Check(f.trailer.parts.Length==2 && Enumerable.Range(0,4).All(i=>f.trailer.Turret(i)==people[i] && f.trailer.TurretPos(i).SequenceEqual(fixture.trailer.TurretPos(i))),size+" JSON retains all four turret names and spots");
            var tpl=Call("CarTemplates","Parse",f.ToJson(),f.name,"verification",f.name,false);
            Check(tpl!=null && Get(tpl,"Trailer")!=null,size+" loader accepts trailer data");
            var host=new GameObject("Build verifier"); var patrol=host.AddComponent(asm.GetType("Apocapatrol.Patrol")); ((Behaviour)patrol).enabled=false;
            GameObject tow=null;
            var build=(IEnumerator)patrol.GetType().GetMethod("Build",All).Invoke(patrol,new object[]{tpl,(Vector3?)new Vector3(20,20,0),Quaternion.identity,false,true,(Action<GameObject>)(g=>tow=g)});
            // Full production build; the test world has no live save/menu/ignition FSMs.
            yield return build;
            Check(tow!=null,size+" full production coroutine builds towing vehicle");
            var trailer=(GameObject)Call("TrailerSupport","Attached",tow);
            Check(trailer!=null && trailer.name.StartsWith(size),size+" full build connects correct trailer prefab");
            Check(Find(trailer,"hinge_wheel_L").GetComponentsInChildren<Transform>(true).Any(t=>t.CompareTag("vehPart")) && Find(trailer,"hinge_wheel_R").GetComponentsInChildren<Transform>(true).Any(t=>t.CompareTag("vehPart")),size+" full build fits both trailer wheels");
            var joint=trailer.GetComponent<ConfigurableJoint>();
            Check(joint!=null && joint.connectedBody==tow.GetComponent<Rigidbody>() && joint.xMotion==ConfigurableJointMotion.Locked && joint.angularYMotion==ConfigurableJointMotion.Free,size+" native joint motions and connected body");
            Check(Vector3.Distance(trailer.transform.TransformPoint(joint.anchor),tow.transform.TransformPoint(joint.connectedAnchor))<.2f,size+" hitch and attacher anchors align");
            Check(tow.GetComponents<PlayMakerFSM>().Where(p=>p.FsmName=="TrailerAttach").All(p=>p.FsmVariables.GetFsmBool("trailerAttached").Value && p.FsmVariables.GetFsmGameObject("Trailer").Value==trailer),size+" native attach variables are populated");
            Check(!Find(trailer,"leg").gameObject.activeSelf && (Find(tow,"hinge_bumper_rear")==null || !Find(tow,"hinge_bumper_rear").gameObject.activeSelf),size+" native support leg and rear bumper are hidden");
            var marker=tow.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")); var riders=(GameObject[])Get(marker,"Riders"); var anchors=(Transform[])Get(marker,"RiderAnchors");
            Check(riders.Length==7 && riders.Skip(3).All(r=>r!=null) && riders.Take(3).All(r=>chief?r==null:r!=null),size+" vehicle turret limits and all four trailer turrets spawn independently");
            for(int i=0;i<4;i++) Check(anchors[i+3].parent==trailer.transform && Vector3.Distance(anchors[i+3].localPosition,new Vector3(f.trailer.TurretPos(i)[0],f.trailer.TurretPos(i)[1],f.trailer.TurretPos(i)[2]))<.001f,size+" turret "+(i+1)+" uses trailer-local saved spot");
            Check(!(bool)Call("Register","Pending",tow) && !(bool)Call("Register","Pending",trailer),size+" both complete vehicles leave deferred save registration");
            var registry=GameObject.Find("NewGO_ArrayList");
            var registered=registry.GetComponents<PlayMakerArrayListProxy>().SelectMany(p=>p.arrayList.Cast<object>()).ToArray();
            Check(registered.Contains(tow) && registered.Contains(trailer),size+" both frames enter native save registry");
            // Record the trailer with the same exporter walker used when saving a player vehicle.
            var exporter=host.AddComponent(asm.GetType("Apocapatrol.TemplateExporter"));
            var wt=exporter.GetType().GetNestedType("Walker",All); var walker=Activator.CreateInstance(wt,true); Set(walker,"Owner",exporter);Set(walker,"Offsets",true);
            wt.GetMethod("Walk",All).Invoke(walker,new object[]{trailer.transform,trailer.transform,Asset(size),-1});
            var parts=((IEnumerable)Get(walker,"Parts")).Cast<TemplatePart>().ToArray();
            Check(parts.Length==2 && parts.All(p=>p.prefab=="small_wheel_1"),size+" exporter records fitted wheels and excludes human turrets");
            // Save the fourth trailer anchor through the public spot API, then edit crew through the real editor writer.
            var source=Path.Combine((string)asm.GetType("Apocapatrol.CarTemplates").GetProperty("PlayerFolder",All).GetValue(null,null),f.name+".json");
            File.WriteAllText(source,f.ToJson()); Call("CarTemplates","Refresh",true);
            var template=Call("CarTemplate","Find",f.name); anchors[6].localPosition=new Vector3(.5f,1.2f,-.8f);
            var message=RiderApi.SaveAnchor(tow,6);
            var saved=TemplateFile.FromJson(File.ReadAllText(source));
            Check(saved.trailer.riderPos4.SequenceEqual(new[]{.5f,1.2f,-.8f}) && saved.rider==f.rider,size+" fourth spot API writes trailer and preserves car turret");
            Call("TemplateMenu","SaveTemplateEdit",template,"None","None","car-empty",new[]{"Warboy","Flexa","Scrud"},new[]{"Flexa","Scrud","Boltjaw","None"});
            saved=TemplateFile.FromJson(File.ReadAllText(source));
            Check(saved.trailer.rider4=="" && saved.trailer.rider=="Flexa" && saved.trailer.parts.Length==2 && saved.trailer.riderPos4.SequenceEqual(new[]{.5f,1.2f,-.8f}),size+" editor changes trailer crew without losing parts or positions");
            RiderWakeChecks(tow,trailer,marker,size);
            if(chief && Environment.GetCommandLineArgs().Contains("-trailer-ui"))
            {
                ((Behaviour)exporter).enabled=false;
                Set(exporter,"_draftCar",tow.transform);Set(exporter,"_draftTrailer",trailer);Set(exporter,"_trailerPage",true);Set(exporter,"_rect",new Rect(0,0,1028,660));
                var selections=(string[])Get(exporter,"_trailerTurrets");for(int i=0;i<4;i++)selections[i]=people[i];
                ui=exporter;uiDraw=exporter.GetType().GetMethod("DrawDraft",All);drawUi=true;
                yield return new WaitForSecondsRealtime(.5f);ScreenCapture.CaptureScreenshot(Path.Combine(TrailerProbe.Output,"save-trailer.png"));yield return new WaitForSecondsRealtime(.5f);
                var menu=host.AddComponent(asm.GetType("Apocapatrol.TemplateMenu"));((Behaviour)menu).enabled=false;
                Set(menu,"_rect",new Rect(0,0,1028,660));Set(menu,"_modal","editTemplate");Set(menu,"_target",template);Set(menu,"_editTrailerPage",true);
                var editing=(string[])Get(menu,"_editTrailerTurrets");for(int i=0;i<4;i++)editing[i]=people[i];
                ui=menu;uiDraw=menu.GetType().GetMethod("DrawWindow",All);
                yield return new WaitForSecondsRealtime(.5f);ScreenCapture.CaptureScreenshot(Path.Combine(TrailerProbe.Output,"edit-trailer.png"));yield return new WaitForSecondsRealtime(.5f);drawUi=false;
            }
            // Native serializer can save both independent prefab identities.
            var bytes=ES3.Serialize(new[]{tow,trailer},new ES3Settings());
            Check(bytes.Length>100 && trailer.GetComponent<ES3Prefab>().prefabId==Asset(size).GetComponent<ES3Prefab>().prefabId,size+" native ES3 serializes both frames with original trailer prefab identity");
            File.WriteAllBytes(Path.Combine(TrailerProbe.Output,size+"-save.json"),bytes);
            var mgr=ES3ReferenceMgrBase.Current;
            foreach(var root in new[]{tow,trailer}) foreach(var node in root.GetComponentsInChildren<Transform>(true))
            { foreach(var c in node.GetComponents<Component>()) if(c!=null) mgr.Remove(c); mgr.Remove(node.gameObject); }
            tow.SetActive(false);trailer.SetActive(false); // the load's raycast must find only the reconstructed trailer
            var restored=ES3.Deserialize<GameObject[]>(bytes,new ES3Settings());
            Check(restored.Length==2 && restored[0]!=tow && restored[1]!=trailer && restored[1].GetComponent<ES3Prefab>().prefabId==Asset(size).GetComponent<ES3Prefab>().prefabId,size+" native ES3 recreates both independent prefab frames");
            var nativeLoad=restored[0].GetComponents<PlayMakerFSM>().Single(p=>p.FsmName=="TrailerLoad");
            Physics.SyncTransforms();
            nativeLoad.enabled=true;nativeLoad.Fsm.SetState("wait");
            // With a second game running, background frames can be far apart. Wait for the native load,
            // rather than assuming a fixed delay contains enough physics/FSM updates for its raycast.
            float reconnectDeadline = Time.realtimeSinceStartup + 10f;
            while (!(bool)Call("TrailerSupport","Connected",restored[0],restored[1]) && Time.realtimeSinceStartup < reconnectDeadline) yield return null;
            Check((bool)Call("TrailerSupport","Connected",restored[0],restored[1]),size+" native TrailerLoad reconnects reconstructed frames through its hitch raycast");
            foreach(var r in restored) UnityEngine.Object.Destroy(r);
            Call("Cleanup","Remove",marker,"verification"); yield return null;
            Check(tow==null && trailer==null,size+" cleanup removes towing vehicle and attached trailer together");
            UnityEngine.Object.Destroy(host);
        }
        Check((int)Call("Rider","Slots","Rustcargo",true,false)==0 && (int)Call("Rider","Slots","Halfbreed",false,true)==1 && (int)Call("Rider","Slots","PipeRat",false,false)==3,"Existing truck, bike and car slot limits remain unchanged");
    }
}

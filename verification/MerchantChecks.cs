using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Apocapatrol;
using BepInEx.Configuration;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
public partial class TrailerRunner
{
    IEnumerator MerchantProbe()
    {
        var storeType=asm.GetType("Apocapatrol.EditorStore");var layoutsType=asm.GetType("Apocapatrol.MerchantLayouts");var previousRoot=(string)storeType.GetField("Root",All).GetValue(null);
        var defaultsRoot=Path.Combine(TrailerProbe.Output,"defaults-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(defaultsRoot);storeType.GetField("Root",All).SetValue(null,defaultsRoot);layoutsType.GetField("_data",All).SetValue(null,null);
        var defaults=(MerchantLayoutFile)layoutsType.GetProperty("Data",All).GetValue(null,null);var layout=defaults.layouts.Single(l=>l.template=="Coyote_Trader");
        Check(layout.objects.Length==9&&Vector3.Distance(layout.objects.Single(p=>p.id=="box").position,new Vector3(1.1499724f,1.349983f,-1.0499865f))<.00001f,"Fresh installation embeds all nine current merchant object placements");
        var customBox=new MerchantLayoutFile{layouts=new[]{new MerchantLayout{template="Coyote_Trader",objects=new[]{new MerchantPose{id="box",position=new Vector3(7,8,9)}}}}};
        File.WriteAllText(Path.Combine(defaultsRoot,"MerchantLayouts.json"),(string)Call("TrafficCodec","WriteRecord",customBox));layoutsType.GetField("_data",All).SetValue(null,null);
        var overrides=(MerchantLayoutFile)layoutsType.GetProperty("Data",All).GetValue(null,null);
        Check(overrides.layouts[0].objects.Length==9&&overrides.layouts[0].objects.Single(p=>p.id=="box").position==new Vector3(7,8,9),"Defaults fill missing placements while preserving newer player edits");
        storeType.GetField("Root",All).SetValue(null,previousRoot);layoutsType.GetField("_data",All).SetValue(null,null);
        using(var stream=asm.GetManifestResourceStream("Apocapatrol.DefaultTemplates.Coyote_Trader.json"))using(var reader=new StreamReader(stream))
        {var busDefaults=TemplateFile.FromJson(reader.ReadToEnd());Check(busDefaults.rider=="Gunnar"&&busDefaults.rider2=="Gunnar"&&busDefaults.riderStanding&&busDefaults.riderStanding2&&busDefaults.riderPos.SequenceEqual(new[]{-.1f,.763f,-3.961f})&&busDefaults.riderPos2.SequenceEqual(new[]{-.95f,.763f,-.361f}),"Embedded bus defaults retain both current standing Gunnar turret placements");}
        var spotter=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="ApocapatrolMerchantSpotter");
        var editor=Resources.FindObjectsOfTypeAll<MonoBehaviour>().Single(c=>c.GetType().FullName=="ApocapatrolMerchantSpotter.MerchantEditor");
        Check(editor.gameObject.activeInHierarchy&&(editor.gameObject.hideFlags&HideFlags.HideInHierarchy)!=0,"Merchant editor runs on a hidden object after loading the game scene");
        var temporaryScene=SceneManager.CreateScene("Merchant editor lifecycle test");yield return SceneManager.UnloadSceneAsync(temporaryScene);
        var wrapper=Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(c=>c.GetType().FullName=="ApocapatrolMerchantSpotter.Plugin");if(wrapper!=null)UnityEngine.Object.Destroy(wrapper);yield return null;
        Check(editor!=null&&editor.gameObject.activeInHierarchy,"Merchant editor survives scene unload and destruction of the BepInEx plugin component");
        var enabled=(ConfigEntry<bool>)Get(editor,"_enabled");var toggle=(ConfigEntry<Key>)Get(editor,"_toggle");
        var keyboard=InputSystem.AddDevice<Keyboard>();enabled.Value=false;toggle.Value=Key.NumpadMultiply;
        InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.NumpadMultiply));InputSystem.Update();TrafficInvoke(editor,"Update");
        Check(enabled.Value,"Numpad multiply event enables the persistent merchant editor");
        InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();
        toggle.Value=Key.Home;InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.Home));InputSystem.Update();TrafficInvoke(editor,"Update");
        Check(!enabled.Value,"Changing the live key binding makes the new key toggle the editor immediately");
        InputSystem.QueueStateEvent(keyboard,new KeyboardState());InputSystem.Update();toggle.Value=Key.None;
        InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.NumpadMultiply));InputSystem.Update();TrafficInvoke(editor,"Update");Check(!enabled.Value,"Unbound editor shortcut is safe and ignores the previous key");
        enabled.Value=true;TrafficInvoke(editor,"Update");Check(enabled.Value,"MODS Enabled setting can open the editor without a key binding");
        enabled.Value=false;toggle.Value=Key.NumpadMultiply;InputSystem.RemoveDevice(keyboard);
        var report=new StringBuilder();
        foreach(var name in new[]{"Sell_Items","Sell_Items_mechanic","Sell_Items_organic_mechanic","MerchantItemsSpawn_V1","MerchantItemsSpawn_Liquids","Bodyguard","Bolter","Gunnar","Lugger","Mechanic","Merchant","Organic_Mechanic","Pistoleer","Pup"})
        {
            var prefab=Asset(name);Check(prefab!=null,"Vanilla merchant/crew prefab available: "+name);
            report.AppendLine("PREFAB "+name+" tag="+prefab.tag);
            foreach(var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                report.AppendLine(" NODE "+t.name+" pos="+t.localPosition+" rot="+t.localEulerAngles+" comps="+string.Join(",",t.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name)));
                foreach(var f in t.GetComponents<PlayMakerFSM>())report.AppendLine("  FSM "+f.FsmName+" / "+string.Join(",",f.Fsm.States.Select(s=>s.Name)));
                foreach(var c in t.GetComponents<Component>().Where(c=>c!=null&&c.GetType().Name=="PlayMakerArrayListProxy"))
                {var field=c.GetType().GetField("preFillGameObjectList",All);var items=field?.GetValue(c) as IEnumerable;if(items!=null)report.AppendLine("  PREFILL "+string.Join(",",items.Cast<UnityEngine.Object>().Where(o=>o!=null).Select(o=>o.name)));}
            }
        }
        File.WriteAllText(Path.Combine(TrailerProbe.Output,"merchant-prefabs.txt"),report.ToString());
        var item=Asset("Repairbox_Small");foreach(var f in item.GetComponents<PlayMakerFSM>().Where(f=>f.FsmName=="ForBuy"||f.FsmName=="Buyable"||f.FsmName=="Value"))
        {report.AppendLine("ITEMFSM "+f.FsmName+" vars="+string.Join(",",f.FsmVariables.GetAllNamedVariables().Select(v=>v.Name+"="+v)));foreach(var s in f.Fsm.States)foreach(var action in s.Actions)report.AppendLine(s.Name+" "+action.GetType().Name+" "+string.Join(",",action.GetType().GetFields().Select(v=>v.Name+"="+(v.GetValue(action) is HutongGames.PlayMaker.NamedVariable n?n.Name+":"+n.ToString():v.GetValue(action)))));}
        File.WriteAllText(Path.Combine(TrailerProbe.Output,"merchant-prefabs.txt"),report.ToString());
        yield return null;
        foreach(var type in new[]{"general","mechanic","organic"})
        {var goods=(GameObject[])Call("MerchantCatalog","Goods",type);Check(goods.Length>0,"Vanilla goods resolve for "+type);}
        var source=Call("CarTemplate","Find","Coyote_Trader");Check(source!=null,"Coyote Trader bus is an embedded template");
        var template=source.GetType().GetMethod("CloneForSpawn",All).Invoke(source,null);Set(template,"MerchantTypes",new[]{"general","mechanic","organic"});Set(template,"MerchantGroup","Coyote merchant");Set(template,"SetupOnly",true);
        ((string[])Get(template,"Turrets"))[1]="Gunnar";((float[][])Get(template,"TurretPos"))[1]=new[]{-.1f,-.137f,-4.011f};
        var host=new GameObject("Merchant fixture builder");var patrol=host.AddComponent(asm.GetType("Apocapatrol.Patrol"));((Behaviour)patrol).enabled=false;
        GameObject bus=null;yield return (IEnumerator)patrol.GetType().GetMethod("Build",All).Invoke(patrol,new object[]{template,(Vector3?)new Vector3(100000,2000,100000),Quaternion.identity,false,true,(Action<GameObject>)(g=>bus=g)});
        Check(bus!=null,"Production builder constructs stationary Coyote merchant bus");
        var merchant=bus.GetComponent(asm.GetType("Apocapatrol.MerchantHost"));Check(merchant!=null,"Merchant host attaches during construction");
        float deadline=Time.realtimeSinceStartup+35f;while(!(bool)Get(merchant,"Ready")&&Time.realtimeSinceStartup<deadline)yield return null;
        Check((bool)Get(merchant,"Ready"),"Legacy multiple selection initializes only one merchant station");
        var turretMarker=bus.GetComponent(asm.GetType("Apocapatrol.PatrolMarker"));var turretAnchor=((Transform[])Get(turretMarker,"RiderAnchors"))[1];var turret=((GameObject[])Get(turretMarker,"Riders"))[1];
        Check(turretAnchor!=null&&Vector3.Distance(turretAnchor.localPosition,new Vector3(-.1f,-.137f,-4.011f))<.001f&&turret!=null,"Second Gunnar turret uses the exact saved bus-local riderPos2");
        var expectedAuto=(Vector3)Call("Rider","AutoSpot",bus,1,false);
        var misleading=GameObject.CreatePrimitive(PrimitiveType.Cube);misleading.transform.SetParent(bus.transform.Find("Apocapatrol.Merchant"),false);misleading.transform.localPosition=new Vector3(15,150,0);Physics.SyncTransforms();
        var actualAuto=(Vector3)Call("Rider","AutoSpot",bus,1,false);Check(Vector3.Distance(expectedAuto,actualAuto)<.01f,"Automatic turret placement ignores shop geometry 150 metres above the bus");UnityEngine.Object.Destroy(misleading);
        var bedroll=Asset("bedroll_1");if(bedroll==null)bedroll=Resources.FindObjectsOfTypeAll<GameObject>().First(g=>!g.scene.IsValid()&&g.name.StartsWith("bedroll",StringComparison.OrdinalIgnoreCase)&&g.GetComponent<Rigidbody>()!=null);
        var probe=(GameObject)TrafficInvoke(merchant,"CreateProbe",bedroll);Check(probe!=null&&probe.GetComponentsInChildren<Renderer>(true).All(r=>!r.enabled)&&probe.GetComponentsInChildren<Collider>(true).All(c=>!c.enabled)&&probe.GetComponentsInChildren<Rigidbody>(true).All(r=>r.isKinematic),"Native bedroll metadata probe is invisible and cannot fall or collide");
        Check(probe.GetComponentsInChildren<PlayMakerFSM>(true).Where(f=>f.enabled).All(f=>f.FsmName=="ItemName"||f.FsmName=="Value"),"Bedroll probe cannot run unpacking or item-spawning FSMs");
        var beforeChildren=probe.GetComponentsInChildren<Transform>(true).Length;yield return new WaitForSeconds(.4f);
        Check(probe==null||probe.GetComponentsInChildren<Transform>(true).Length==beforeChildren,"Bedroll metadata initialization does not unpack extra objects");
        var crew=bus.GetComponent(asm.GetType("Apocapatrol.Crew"));Check((bool)Get(crew,"Hold")&&!(bool)Call("Nwh","EngineRunning",bus),"Merchant driver remains parked with engine off");
        var cam=Camera.main;Check(cam!=null,"Merchant editor has a game camera for nearby bus selection");var cameraPosition=cam.transform.position;
        cam.transform.position=bus.transform.position+Vector3.up*2f;enabled.Value=true;Set(editor,"_next",0f);TrafficInvoke(editor,"Update");
        Check((GameObject)Get(editor,"_car")==bus&&((MerchantObject[])Get(editor,"_objects")).Length>0,"Enabled merchant editor finds the bus and its editable objects when the camera is inside it");
        enabled.Value=false;TrafficInvoke(editor,"Update");cam.transform.position=cameraPosition;
        var objects=MerchantApi.Objects(bus);Check(objects.Count(o=>o.Kind=="Sale items")==4&&objects.Count(o=>o.Kind=="Board")==1&&objects.Count(o=>o.Kind=="Prices")==1,"Debug API exposes only one board, one price list and four stock locations");
        var save=(MerchantSave)Get(merchant,"Data");Check(save.types.SequenceEqual(new[]{"general"})&&save.stock.Length==4&&save.stock.All(s=>!string.IsNullOrEmpty(s.nativeName)),"Legacy multiple selection keeps the first type and registers only its four stock items");
        var snapshot=(string)TrafficInvoke(merchant,"Snapshot");
        Check(save.demands.Length==1&&save.demands.All(d=>d.names.Length>0&&d.names.Length==d.prices.Length),"Single native merchant board has wanted items and valid prices");
        deadline=Time.realtimeSinceStartup+12f;while(bus.GetComponentsInChildren<PlayMakerFSM>(true).Any(f=>f.FsmName=="ItemList"&&f.ActiveStateName!="done")&&Time.realtimeSinceStartup<deadline)yield return null;
        TrafficInvoke(merchant,"CleanProbes");yield return null;
        Check(((IList)Get(merchant,"_probes")).Count==0,"Wanted-board calculation removes all temporary metadata probes");
        Check(snapshot.Contains("names")&&snapshot.Contains("nativeName"),"Merchant snapshot includes nested stock and price demand");
        var marker=bus.GetComponent(asm.GetType("Apocapatrol.PatrolMarker"));var carData=(PatrolCarData)TrafficInvoke(marker,"Snapshot");
        Check((bool)Get(marker,"SetupOnly")&&!(bool)Call("WorldTraffic","CombatAllowed",bus)&&turret.GetComponents<PlayMakerFSM>().All(f=>f.FsmName!="Attack"||!f.enabled),"Setup template remains parked with turret combat disabled");
        var setupRoundTrip=(PatrolSaveData)Call("PatrolPersistence","Deserialize",Call("PatrolPersistence","Serialize",new PatrolSaveData{version=14,saveFile="SaveGame1.es3",worldSeed=42,cars=new[]{carData}}));
        Check(setupRoundTrip.cars[0].setupOnly,"Schema 14 preserves parked setup-only behavior");
        var decoded=(PatrolSaveData)Call("PatrolPersistence","Deserialize",Call("PatrolPersistence","Serialize",new PatrolSaveData{version=13,saveFile="SaveGame1.es3",worldSeed=42,cars=new[]{carData}}));
        Check(decoded.cars[0].merchantJson==carData.merchantJson&&decoded.cars[0].faction=="Coyotes","Schema 13 preserves merchant stock and faction");
        var position=objects.First(o=>o.Id=="general/box");position.Transform.position=bus.transform.TransformPoint(new Vector3(.2f,2f,-3f));MerchantApi.Save(bus);
        Check(File.ReadAllText(Path.Combine((string)asm.GetType("Apocapatrol.EditorStore").GetField("Root",All).GetValue(null),"MerchantLayouts.json")).Contains("\"box\""),"Numpad layout save persists shared object IDs");
        var guardStats=turret.GetComponent(asm.GetType("Apocapatrol.MerchantGuardHealth"));var maximum=(float)Get(guardStats,"Maximum");
        Check(maximum>0f&&Mathf.Abs((float)Call("Patrol","GetHealth",turret)-maximum)<.01f,"Merchant turret starts at its hidden quadrupled maximum HP");
        Call("Patrol","SetHealth",turret,maximum*.6f);
        var first=save.stock.First();var stockObj=Resources.FindObjectsOfTypeAll<GameObject>().First(g=>g.scene.IsValid()&&g.name==first.nativeName);
        var buyable=stockObj.GetComponents<PlayMakerFSM>().First(f=>f.FsmName=="Buyable");
        HutongGames.PlayMaker.FsmVariables.GlobalVariables.GetFsmInt("Money").Value=100000;
        buyable.Fsm.SetState("buy");TrafficInvoke(merchant,"Snapshot");Check(first.purchased,"Saving immediately after a purchase records it before the periodic stock poll");yield return new WaitForSeconds(.7f);
        TrafficInvoke(merchant,"Update");Check(first.purchased&&stockObj.transform.parent==null,"Vanilla purchase debits money and detaches stock from the bus");
        Check(HutongGames.PlayMaker.FsmVariables.GlobalVariables.GetFsmInt("Money").Value<100000,"Native stock purchase uses vanilla money");
        var demand=save.demands.First(d=>d.type=="general");
        var seller=bus.GetComponentsInChildren<PlayMakerFSM>(true).First(f=>f.FsmName=="SellItemsHere"&&f.FsmVariables.FindFsmInt("ItemNameIndex")!=null&&f.transform.IsChildOf(objects.First(o=>o.Id=="general/box").Transform));
        var sellItem=UnityEngine.Object.Instantiate(Asset("Repairbox_Small"));seller.FsmVariables.FindFsmGameObject("Item").Value=sellItem;seller.FsmVariables.FindFsmInt("ItemNameIndex").Value=0;
        int balance=HutongGames.PlayMaker.FsmVariables.GlobalVariables.GetFsmInt("Money").Value;seller.Fsm.SetState("sell");yield return new WaitForSeconds(.4f);
        Check(sellItem==null&&HutongGames.PlayMaker.FsmVariables.GlobalVariables.GetFsmInt("Money").Value==balance+demand.prices[0],"Bus selling box pays its board price using vanilla selling actions after reparenting");
        var restoredJson=(string)TrafficInvoke(merchant,"Snapshot");var nativeObjects=new[]{bus}.Concat(Resources.FindObjectsOfTypeAll<GameObject>().Where(g=>g.scene.IsValid()&&save.stock.Any(s=>!s.purchased&&s.nativeName==g.name))).ToArray();
        var bytes=ES3.Serialize(nativeObjects,new ES3Settings());Check(bytes.Length>0,"Bus and unsold merchandise pass the native ES3 serializer");
        foreach(var g in nativeObjects){Call("Register","RemoveTree",g);UnityEngine.Object.Destroy(g);}yield return null;
        var restored=ES3.Deserialize<GameObject[]>(bytes,new ES3Settings());var restoredBus=restored[0];
        var legacy=(MerchantSave)asm.GetType("Apocapatrol.TrafficCodec").GetMethod("ReadRecord",All).MakeGenericMethod(typeof(MerchantSave)).Invoke(null,new object[]{restoredJson});
        legacy.types=new[]{"general","mechanic","organic"};
        var leftover=new GameObject("Legacy unsold mechanic stock");var purchasedExtra=new GameObject("Legacy purchased organic stock");
        legacy.stock=legacy.stock.Concat(new[]{new MerchantStock{type="mechanic",slot=0,prefab="Repairbox_Small",nativeName=leftover.name},new MerchantStock{type="organic",slot=0,prefab="Repairbox_Small",nativeName=purchasedExtra.name,purchased=true}}).ToArray();
        restoredJson=(string)Call("TrafficCodec","WriteRecord",legacy);
        Call("MerchantRuntime","Restore",restoredBus,restoredJson);var restoredHost=restoredBus.GetComponent(asm.GetType("Apocapatrol.MerchantHost"));
        deadline=Time.realtimeSinceStartup+35f;while(!(bool)Get(restoredHost,"Ready")&&Time.realtimeSinceStartup<deadline)yield return null;
        var restoredData=(MerchantSave)Get(restoredHost,"Data");Check((bool)Get(restoredHost,"Ready")&&restoredData.stock.Count(s=>s.purchased)==1&&restoredData.stock.Where(s=>!s.purchased).All(s=>s.buyPrice>=0),"Native load restores merchant stations, prices and sold-out stock without refilling purchased items");
        yield return null;yield return null;yield return null;
        var restoredTurret=((GameObject[])Get(restoredBus.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")),"Riders"))[1];
        Check(restoredTurret!=null&&Mathf.Abs((float)Call("Patrol","GetHealth",restoredTurret)-maximum*.6f)<.01f,"Restored merchant turret retains wounded HP without healing or multiplying it again");
        Check(restoredData.types.Length==1&&restoredData.stock.Length==4&&leftover==null&&purchasedExtra!=null,"Legacy multi-shop restore removes surplus unsold stock and preserves purchased property");UnityEngine.Object.Destroy(purchasedExtra);
        Check(MerchantApi.Objects(restoredBus).First(o=>o.Id==position.Id).Transform.position==restoredBus.transform.TransformPoint(new Vector3(.2f,2f,-3f)),"Saved in-car box layout is reapplied on native load");
        foreach(var type in new[]{"mechanic","organic"})
        {
            var choice=source.GetType().GetMethod("CloneForSpawn",All).Invoke(source,null);Set(choice,"MerchantTypes",new[]{type});GameObject otherBus=null;
            yield return (IEnumerator)patrol.GetType().GetMethod("Build",All).Invoke(patrol,new object[]{choice,(Vector3?)new Vector3(100080,2000,100000),Quaternion.identity,false,true,(Action<GameObject>)(g=>otherBus=g)});
            Check(otherBus!=null,"Production builder constructs exclusive "+type+" merchant");var otherHost=otherBus.GetComponent(asm.GetType("Apocapatrol.MerchantHost"));
            deadline=Time.realtimeSinceStartup+35f;while(!(bool)Get(otherHost,"Ready")&&Time.realtimeSinceStartup<deadline)yield return null;
            var otherData=(MerchantSave)Get(otherHost,"Data");TrafficInvoke(otherHost,"Snapshot");
            Check((bool)Get(otherHost,"Ready")&&otherData.types.SequenceEqual(new[]{type})&&otherData.stock.Length==4&&otherData.demands.Length==1&&MerchantApi.Objects(otherBus).Count(o=>o.Kind=="Board")==1,"Exclusive "+type+" merchant initializes four stock items and one working board");
            Check(Vector3.Distance(MerchantApi.Objects(otherBus).First(o=>o.Id==type+"/box").Transform.position,otherBus.transform.TransformPoint(new Vector3(.2f,2f,-3f)))<.02f,"General merchant placement is shared with "+type);
            UnityEngine.Object.Destroy(otherBus);yield return null;
        }
        if(Environment.GetCommandLineArgs().Contains("-trailer-ui"))yield return MerchantUiChecks(host);
        UnityEngine.Object.Destroy(restoredBus);UnityEngine.Object.Destroy(host);UnityEngine.Object.Destroy(stockObj);
    }
    IEnumerator MerchantUiChecks(GameObject host)
    {
        ui=host.AddComponent(asm.GetType("Apocapatrol.TemplateMenu"));((Behaviour)ui).enabled=false;uiDraw=ui.GetType().GetMethod("DrawWindow",All);
        Set(ui,"_rect",new Rect(0,0,1028,755));Set(ui,"_tab",2);Set(ui,"_selectedGroup","coyote-merchant");((System.Collections.Generic.HashSet<string>)Get(ui,"_expanded")).Add("coyote-merchant");drawUi=true;
        yield return new WaitForSecondsRealtime(.5f);ScreenCapture.CaptureScreenshot(Path.Combine(TrailerProbe.Output,"merchants-tab.png"));yield return new WaitForSecondsRealtime(.5f);
        var dropdown=Get(ui,"_dropdown");var anchor=(Rect)Get(ui,"_lastFactionAnchor");var data=(EditorData)asm.GetType("Apocapatrol.EditorStore").GetField("Data",All).GetValue(null);
        foreach(var type in new[]{"mechanic","organic","general","general"})
        {TrafficInvoke(ui,"SelectMerchantType",data.Merchants[0],type);var mutation=(Action)Get(ui,"_mutation");Set(ui,"_mutation",null);mutation();Check(data.Merchants[0].MerchantTypes.SequenceEqual(new[]{type}),"Merchant choice replaces selection and cannot toggle off: "+type);}
        var legacyData=EditorData.FromJson("{\"patrols\":[],\"convoys\":[],\"carLoot\":[],\"truckLoot\":[],\"merchants\":[{\"id\":\"legacy\",\"merchantTypes\":[\"mechanic\",\"general\",\"organic\"]}]}");
        Check(legacyData.Merchants[0].MerchantTypes.SequenceEqual(new[]{"mechanic"}),"Legacy editor JSON keeps its first valid merchant choice");
        legacyData.Merchants[0].MerchantTypes.Add("organic");Check(EditorData.FromJson(legacyData.ToJson()).Merchants[0].MerchantTypes.SequenceEqual(new[]{"mechanic"}),"Editor serialization enforces exclusive merchant type");
        TrafficInvoke(ui,"OpenFaction",data.Merchants[0],anchor);TrafficInvoke(dropdown,"Flush");
        Check((bool)dropdown.GetType().GetProperty("IsOpen",All).GetValue(dropdown,null),"Production faction control opens a nonempty dropdown");
        var popup=(Rect)Get(dropdown,"_box");Check(popup.x>100&&popup.y>200&&popup.yMax<755&&((IList)Get(dropdown,"_options")).Count==2,"Faction popup contains both choices inside the window after scroll coordinate conversion");
        yield return new WaitForSecondsRealtime(.3f);ScreenCapture.CaptureScreenshot(Path.Combine(TrailerProbe.Output,"faction-dropdown.png"));yield return new WaitForSecondsRealtime(.5f);
        Check((bool)TrafficInvoke(dropdown,"SelectAt",new Vector2(popup.center.x,popup.y+4+34+17)),"Faction row accepts a click in the visible popup");TrafficInvoke(dropdown,"Flush");
        Check(data.Merchants[0].Faction=="Scrapyard","Selecting the faction popup changes the group faction");
        data.Merchants[0].Faction="Coyotes";drawUi=false;
    }
}

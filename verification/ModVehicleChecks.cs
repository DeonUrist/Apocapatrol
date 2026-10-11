using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Apocapatrol;
using ES3Internal;

public partial class TrailerRunner
{
    bool Available(object template) { return (bool)template.GetType().GetProperty("Available", All).GetValue(template, null); }
    string Missing(object template) { return (string)template.GetType().GetProperty("MissingPrefab", All).GetValue(template, null); }
    object Parse(TemplateFile file) { return Call("CarTemplates", "Parse", file.ToJson(), file.name, "verification", file.name, false); }
    IList Compose(string id, bool convoy)
    {
        var group = Activator.CreateInstance(asm.GetType("Apocapatrol.SpawnGroup"), true);
        ((List<string>)Get(group, "Templates")).Add(id);
        return (IList)Call("EditorStore", "Compose", group, convoy, (Func<double>)(() => .5));
    }
    IEnumerator ModVehicleChecks()
    {
        var fixture = TemplateFile.FromJson(File.ReadAllText(Path.Combine(TrailerProbe.Templates, "Example_PipeRat.json")));
        fixture.name = "Verifier_UnloadedMod"; fixture.driver = fixture.passenger = "";
        fixture.bumpers = new string[0]; fixture.rider = fixture.rider2 = fixture.rider3 = "";
        Check(Available(Parse(fixture)), "Vanilla vehicle remains available");
        Check((bool)Call("TrailerSupport", "IsTrailer", Call("Prefabs", "Exact", "Trailer_Small")), "Exact lookup distinguishes the Trailer_Small frame from its trailer_small mesh");
        var registered = UnityEngine.Object.Instantiate((GameObject)Call("Prefabs", "Exact", "PipeRat"));
        registered.SetActive(false); registered.name = "VerifierRegisteredVehicle";
        var registration = registered.GetComponent<ES3Prefab>(); registration.prefabId = 735710202610110001L;
        var manager = ES3ReferenceMgrBase.Current; manager.prefabs.Add(registration); Call("Prefabs", "Invalidate");
        fixture.body = registered.name;
        Check((GameObject)Call("Prefabs", "Exact", registered.name) == registered && Available(Parse(fixture)), "A registered runtime vehicle is available without a Factory convention");
        manager.prefabs.Remove(registration); UnityEngine.Object.Destroy(registered); yield return null; Call("Prefabs", "Invalidate");
        fixture.body = "PipeRat";
        fixture.body = "UnloadedModVehicle";
        Check(!Available(Parse(fixture)), "Unloaded custom frame is unavailable without rejecting its JSON");
        fixture.body = "Pipe";
        Check(Call("Prefabs", "FindAny", "Pipe") != null && !Available(Parse(fixture)), "Availability never substitutes a partial vanilla name for a missing mod prefab");
        fixture.body = "PipeRat";
        var partName = fixture.parts[0].prefab; fixture.parts[0].prefab = "UnloadedModPart";
        Check(Missing(Parse(fixture)) == "UnloadedModPart", "Missing fitted part disables its entire template");
        fixture.parts[0].prefab = partName;
        fixture.trailer = new TemplateFile { body = "WarRig", parts = new[] { new TemplatePart { hinge = "hinge_wheel_L", prefab = "small_wheel_1" } } };
        var source = Path.Combine((string)asm.GetType("Apocapatrol.CarTemplates").GetProperty("PlayerFolder", All).GetValue(null, null), fixture.name + ".json");
        File.WriteAllText(source, fixture.ToJson()); Call("CarTemplates", "Refresh", true);
        var loaded = Call("CarTemplate", "Find", fixture.name);
        var external = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "WarRig");
        if (external == null)
        {
            Check(loaded != null && !Available(loaded) && Missing(loaded) == "WarRig", "WarRig absent: saved towing template stays loaded and unavailable");
            Check(Compose(fixture.name, false).Count == 1 && (string)Get(Compose(fixture.name, false)[0], "Body") == "PipeRat", "WarRig absent: patrol skips unavailable entry and uses its fallback");
            Check(Compose(fixture.name, true).Count == 1 && (string)Get(Compose(fixture.name, true)[0], "Body") == "Rustcargo", "WarRig absent: convoy skips unavailable escort and keeps its truck fallback");
            Check(File.ReadAllText(source) == fixture.ToJson(), "WarRig absent: saved template file remains unchanged");
        }
        else
        {
            var prefab = (GameObject)external.GetType("WarRig.Factory").GetMethod("Template").Invoke(null, null);
            Check(prefab.scene.IsValid() && prefab.transform.parent != null, "WarRig is a runtime template excluded from vanilla asset scans");
            Check((GameObject)Call("Prefabs", "Exact", "WarRig") == prefab && (GameObject)Call("Prefabs", "FindAny", "WarRig") == prefab, "WarRig loaded: exact and spawn lookup share the foreign factory prefab");
            Check(Available(loaded) && (string)Get(Compose(fixture.name, false)[0], "Name") == fixture.name, "WarRig loaded: saved towing template is available and included in patrols");
            string[] hinges = { "hinge_wheel_L", "hinge_wheel_R", "hinge_wheel_L2", "hinge_wheel_R2", "hinge_wheel_L3", "hinge_wheel_R3" };
            fixture.name = "Verifier_WarRigExport";
            fixture.trailer.parts = hinges.Select(h => new TemplatePart { hinge = h, prefab = "small_wheel_1" }).ToArray();
            string[] turretPeople = { "Warboy", "Flexa", "Scrud", "Warboy" };
            for(int i=0;i<4;i++) { fixture.trailer.SetTurret(i,turretPeople[i]); fixture.trailer.SetTurretPos(i,new[]{i%2==0?-.5f:.5f,3.067f,i<2?-2f:-7.45f}); }
            var host = new GameObject("Mod vehicle verifier");
            var patrol = host.AddComponent(asm.GetType("Apocapatrol.Patrol")); ((Behaviour)patrol).enabled = false;
            GameObject tow = null;
            yield return (IEnumerator)patrol.GetType().GetMethod("Build", All).Invoke(patrol, new object[] { Parse(fixture), (Vector3?)new Vector3(50,20,0), Quaternion.identity, false, true, (Action<GameObject>)(g => tow = g) });
            Check(tow != null, "WarRig loaded: production builder creates towing vehicle");
            var trailer = (GameObject)Call("TrailerSupport", "Attached", tow);
            Check(trailer != null && trailer.name.StartsWith("WarRig"), "WarRig loaded: production builder attaches custom trailer");
            RiderWakeChecks(tow,trailer,tow.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")),"WarRig");
            var exporter = host.AddComponent(asm.GetType("Apocapatrol.TemplateExporter")); ((Behaviour)exporter).enabled = false;
            Check((GameObject)exporter.GetType().GetMethod("Asset", All).Invoke(exporter, new object[] { "WarRig" }) == prefab, "Exporter resolves WarRig through the foreign factory");
            var player = GameObject.Find("Player"); bool madePlayer = player == null;
            if (madePlayer) player = new GameObject("Player");
            var oldParent = player.transform.parent; var oldPosition = player.transform.position; var oldRotation = player.transform.rotation;
            try
            {
                player.transform.SetParent(tow.transform, false);
                Set(exporter, "_draftCar", tow.transform); Set(exporter, "_draftTrailer", trailer);
                Set(exporter, "_templateName", fixture.name); Set(exporter, "_driverName", "None"); Set(exporter, "_passengerName", "None");
                var output = Path.Combine(Path.GetDirectoryName(source), fixture.name + ".json");
                // This file belongs solely to the isolated verifier and can be regenerated on reruns.
                if (File.Exists(output)) File.Delete(output);
                Call("CarTemplates", "Refresh", true);
                exporter.GetType().GetMethod("Dump", All).Invoke(exporter, null);
                var saved = TemplateFile.FromJson(File.ReadAllText(output));
                Check(saved.trailer != null && saved.trailer.body == "WarRig" && saved.trailer.parts.Length == 6, "Production Save template writes WarRig with all six fitted wheels");
                Check(Available(Call("CarTemplate", "Find", fixture.name)), "Exported WarRig template reloads as available");
            }
            finally
            {
                player.transform.SetParent(oldParent, true); player.transform.position = oldPosition; player.transform.rotation = oldRotation;
                if (madePlayer) UnityEngine.Object.Destroy(player);
            }
            var marker = tow.GetComponent(asm.GetType("Apocapatrol.PatrolMarker")); Call("Cleanup", "Remove", marker, "verification");
            UnityEngine.Object.Destroy(host); yield return null;
        }
        // Existing rosters retain unavailable entries, so reinstalling a source mod restores them automatically.
        fixture.name = "Verifier_UnloadedMod"; fixture.trailer.body = "UnloadedModTrailer";
        File.WriteAllText(source, fixture.ToJson()); Call("CarTemplates", "Refresh", true);
        Check(!Available(Call("CarTemplate", "Find", fixture.name)) && Compose(fixture.name, false).Count == 1, "An unloaded custom trailer disables its towing template");
        if (Environment.GetCommandLineArgs().Contains("-trailer-ui"))
        {
            var data = asm.GetType("Apocapatrol.EditorStore").GetField("Data", All).GetValue(null);
            ((List<string>)Get(data, "TemplateOrder")).Insert(0, fixture.name);
            var host = new GameObject("Unavailable template UI verifier");
            var menu = host.AddComponent(asm.GetType("Apocapatrol.TemplateMenu")); ((Behaviour)menu).enabled = false;
            Set(menu, "_rect", new Rect(0,0,1028,660));
            // Even a previously selected template must have Add and Spawn disabled if its prefab disappears.
            Set(menu, "_selectedTemplate", fixture.name);
            ui = menu; uiDraw = menu.GetType().GetMethod("DrawWindow", All); drawUi = true;
            yield return new WaitForSecondsRealtime(.5f);
            ScreenCapture.CaptureScreenshot(Path.Combine(TrailerProbe.Output,"unavailable-template.png"));
            yield return new WaitForSecondsRealtime(.5f); drawUi = false;
            UnityEngine.Object.Destroy(host);
        }
    }
}

#!/bin/sh
# Builds Apocapatrol.dll against the game's own libraries. Usage: ./build.sh [out.dll]
# BaseTemplates JSON and editor defaults are embedded for recovery (run from the repo folder).
RES="-resource:EditorDefaults.json,Apocapatrol.EditorDefaults.json"; for f in BaseTemplates/*.json; do [ -f "$f" ] && RES="$RES -resource:$f,Apocapatrol.DefaultTemplates.$(basename "$f")"; done
M=${MANAGED:-/e/SteamLibrary/steamapps/common/Apocalypter/Apocalypter_Data/Managed}; B=${BEPCORE:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/core}
mcs -nostdlib -noconfig -target:library -langversion:latest -optimize+ -out:${1:-Apocapatrol.dll} \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/netstandard.dll \
  -r:$B/BepInEx.dll -r:$B/0Harmony.dll -r:$M/NWH.Common.dll -r:$M/NWH.VehiclePhysics2.dll -r:$M/NWH.WheelController.dll \
  -r:$M/UnityEngine.dll -r:$M/UnityEngine.CoreModule.dll -r:$M/UnityEngine.TextRenderingModule.dll -r:$M/UnityEngine.PhysicsModule.dll -r:$M/UnityEngine.TerrainPhysicsModule.dll -r:$M/UnityEngine.TerrainModule.dll -r:$M/UnityEngine.IMGUIModule.dll -r:$M/UnityEngine.AudioModule.dll -r:$M/UnityEngine.AnimationModule.dll -r:$M/UnityEngine.ImageConversionModule.dll -r:$M/UnityEngine.JSONSerializeModule.dll \
  -r:$M/Unity.InputSystem.dll -r:$M/PlayMaker.dll -r:$M/Assembly-CSharp.dll -r:$M/Assembly-CSharp-firstpass.dll \
  Plugin.cs Patrol.cs Crew.cs Pilot.cs Passenger.cs Pose.cs Persistence.cs Templates.cs TemplateMenu.cs TemplateMenu.Ledger.cs LedgerSkin.cs TemplateExporter.cs Cargo.cs Ram.cs Convoy.cs Cleanup.cs Explode.cs Paint.cs Rider.cs MeleeWheels.cs CarTemplates.cs CarTemplateFile.cs EditorData.cs EditorStore.cs EditorSession.cs EditorWidgets.cs EditorSelection.cs VehicleRules.cs VehicleAttachments.cs PickupCatalog.cs PatrolSkin.cs ShellRecycle.cs MotorcycleIntegration.cs Motorcycles/Factory.cs Motorcycles/MotorcycleBalance.cs $RES

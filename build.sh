#!/bin/sh
# Builds Apocapatrol.dll against the game's own libraries. Usage: ./build.sh [out.dll]
M=${MANAGED:-/e/SteamLibrary/steamapps/common/Apocalypter/Apocalypter_Data/Managed}; B=${BEPCORE:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/core}
mcs -nostdlib -noconfig -target:library -langversion:latest -optimize+ -out:${1:-Apocapatrol.dll} \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/netstandard.dll \
  -r:$B/BepInEx.dll \
  -r:$M/UnityEngine.dll -r:$M/UnityEngine.CoreModule.dll -r:$M/UnityEngine.PhysicsModule.dll \
  -r:$M/Unity.InputSystem.dll -r:$M/PlayMaker.dll -r:$M/Assembly-CSharp.dll -r:$M/Assembly-CSharp-firstpass.dll \
  Plugin.cs Patrol.cs Crew.cs Passenger.cs Pose.cs Persistence.cs

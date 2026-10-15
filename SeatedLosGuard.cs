using System;
using HarmonyLib;
using Micosmo.SensorToolkit.PlayMaker;
namespace Apocapatrol
{
    internal static class SeatedLosGuard
    {
        internal static void Install()
        {
            var harmony=new Harmony(Plugin.GUID+".seated-los");
            foreach(var method in new[]{"OnEnter3D","OnUpdate3D"})harmony.Patch(AccessTools.Method(typeof(SensorGetLineOfSightResult),method),prefix:new HarmonyMethod(typeof(SeatedLosGuard),nameof(Before)));
        }
        internal static bool Before(SensorGetLineOfSightResult __instance)
        {
            if(__instance.targetObject!=null&&__instance.targetObject.Value!=null)return true;
            if(__instance.storeVisibility!=null&&!__instance.storeVisibility.IsNone)__instance.storeVisibility.Value=0f;
            if(__instance.storeIsVisible!=null&&!__instance.storeIsVisible.IsNone)__instance.storeIsVisible.Value=false;
            if(__instance.storeVisibleTransforms!=null&&!__instance.storeVisibleTransforms.IsNone)__instance.storeVisibleTransforms.Values=Array.Empty<object>();
            if(__instance.storeVisiblePositions!=null&&!__instance.storeVisiblePositions.IsNone)__instance.storeVisiblePositions.Values=Array.Empty<object>();
            if(!__instance.everyFrame)__instance.Finish();
            return false;
        }
    }
}

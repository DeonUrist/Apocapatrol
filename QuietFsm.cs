using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Apocapatrol
{
    // 2.1.4: the game's own FSMs ask every object the player looks at or touches for an "ID" FSM (GetFsmString ID on {Item}: car frame
    // "collider" children, switch_lights, a car root...). PlayMaker's ActionHelpers.GetGameObjectFsm prints a Unity warning
    // "Could not find FSM: ID on GameObject: collider" each time it is missing - hundreds per minute in the BepInEx console.
    // This prefix does the same lookup (first PlayMakerFSM with that name, else null) without the warning. Off: the vanilla method runs.
    internal static class QuietFsm
    {
        private static readonly List<PlayMakerFSM> _buf = new List<PlayMakerFSM>();
        private static int _hidden;

        internal static void Install()
        {
            var target = AccessTools.Method(typeof(HutongGames.PlayMaker.ActionHelpers), "GetGameObjectFsm", new[] { typeof(GameObject), typeof(string) });
            if (target == null) { Plugin.Log.LogWarning("QuietFsm: ActionHelpers.GetGameObjectFsm not found; missing-FSM warnings stay"); return; }
            new Harmony(Plugin.GUID + ".quietfsm").Patch(target, prefix: new HarmonyMethod(typeof(QuietFsm), nameof(Prefix)));
        }

        private static bool Prefix(GameObject __0, string __1, ref PlayMakerFSM __result)
        {
            if (Plugin.HideFsmWarnings == null || !Plugin.HideFsmWarnings.Value) return true;
            if (__0 == null || string.IsNullOrEmpty(__1)) return true;   // the vanilla paths for these
            try
            {
                __0.GetComponents(_buf);
                for (int i = 0; i < _buf.Count; i++)
                    if (_buf[i] != null && _buf[i].FsmName == __1) { __result = _buf[i]; _buf.Clear(); return false; }
                _buf.Clear();
                __result = null;
                if (++_hidden == 1) Plugin.Verbose("QuietFsm: hiding \"Could not find FSM\" warnings (first: " + __1 + " on " + __0.name + ")");
                return false;
            }
            catch (Exception) { _buf.Clear(); return true; }
        }
    }
}

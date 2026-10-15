using HarmonyLib;
using UnityEngine;
using NwhOrigin = NWH.Common.FloatingOrigin.FloatingOrigin;

namespace Apocapatrol
{
    // MapMagic's root is saved by the game and moves with every NWH origin jump. Its local world frame does not.
    // TotalOffset alone is not sufficient: a loaded game restores MapMagic's position in a fresh FloatingOrigin instance.
    internal static class TrafficSpace
    {
        private static Transform _anchor;
        private static float _nextFind;
        internal static Vector3 Origin
        {
            get
            {
                if (_anchor == null && Time.unscaledTime >= _nextFind)
                {
                    _nextFind = Time.unscaledTime + 1f;
                    var map = GameObject.Find("MapMagic");
                    if (map != null) _anchor = map.transform;
                }
                return _anchor != null ? _anchor.position : Vector3.zero;
            }
        }
        internal static Vector3 World(Vector3 scene) { return scene - Origin; }
        internal static Vector3 Scene(Vector3 world) { return world + Origin; }
        internal static void Reset() { _anchor = null; _nextFind = 0f; }
        internal static void Install()
        { new Harmony(Plugin.GUID + ".traffic-origin").PatchAll(typeof(OriginJump)); }

        [HarmonyPatch(typeof(NwhOrigin), "Jump")]
        private static class OriginJump
        {
            private static void Prefix(NwhOrigin __instance, out Vector3 __state)
            { __state = __instance.TotalOffset; }
            private static void Postfix(NwhOrigin __instance, Vector3 __state)
            {
                Vector3 shift = __state - __instance.TotalOffset;
                // Routes and detection memories are stable-world coordinates. Only legacy physics caches need translation.
                foreach (var pilot in Pilot.All) if (pilot != null) pilot.OriginShift(shift);
                PlayerRef.OriginShift(shift);
            }
        }
    }
}

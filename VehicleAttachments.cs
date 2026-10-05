using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    internal static class VehicleAttachments
    {
        private static PlayMakerFSM Flag(GameObject go)
        {
            return go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "CheckBool" && f.FsmVariables.GetFsmBool("Attached") != null);
        }
        internal static bool IsPart(Transform t)
        {
            if (t.CompareTag("vehPart")) return true;
            var flag = Flag(t.gameObject); return flag != null && flag.FsmVariables.GetFsmBool("Attached").Value;
        }
        internal static bool IsFreeAttachment(GameObject go) { return Flag(go) != null; }
        internal static bool Prepare(GameObject part)
        {
            var flag = Flag(part); if (flag == null) return false;
            flag.FsmVariables.GetFsmBool("Attached").Value = true;
            var save = part.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "saveItemVar");
            var attached = save != null ? save.FsmVariables.GetFsmBool("Attached") : null;
            if (attached != null) attached.Value = true;
            // Blastlances keep their native weapon tag/layer, explosion triggers and pickup behavior.
            return true;
        }
    }
}

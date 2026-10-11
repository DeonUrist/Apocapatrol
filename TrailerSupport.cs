using System;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    // The native trailer is a separate saved vehicle. Its ConfigurableJoint lives on the trailer,
    // with the trailer's attacher and the towing frame's hitch as its two anchors.
    internal static class TrailerSupport
    {
        internal static bool IsTrailer(GameObject go)
        {
            return go != null && go.GetComponents<PlayMakerFSM>().Any(f => f != null && f.FsmName == "TrailerAttached");
        }

        internal static bool Connected(GameObject car, GameObject trailer)
        {
            if (car == null || trailer == null) return false;
            var rb = car.GetComponent<Rigidbody>();
            return rb != null && trailer.GetComponents<ConfigurableJoint>().Any(j => j != null && j.connectedBody == rb);
        }

        internal static GameObject Attached(GameObject car)
        {
            if (car == null) return null;
            var marker = car.GetComponent<PatrolMarker>();
            if (marker != null && Connected(car, marker.Trailer)) return marker.Trailer;
            foreach (var f in car.GetComponents<PlayMakerFSM>())
            {
                if (f == null || (f.FsmName != "TrailerAttach" && f.FsmName != "TrailerLoad")) continue;
                var v = f.FsmVariables.GetFsmGameObject("Trailer");
                if (v != null && IsTrailer(v.Value) && Connected(car, v.Value)) return v.Value;
            }
            var rb = car.GetComponent<Rigidbody>();
            if (rb == null) return null;
            foreach (var j in UnityEngine.Object.FindObjectsOfType<ConfigurableJoint>())
                if (j != null && j.connectedBody == rb && IsTrailer(j.gameObject)) return j.gameObject;
            return null;
        }

        internal static void Connect(GameObject car, GameObject trailer)
        {
            var hitch = Patrol.FindChild(car.transform, "hitch");
            var attacher = Patrol.FindChild(trailer.transform, "attacher");
            var towRb = car.GetComponent<Rigidbody>();
            if (hitch == null || attacher == null || towRb == null || trailer.GetComponent<Rigidbody>() == null || !IsTrailer(trailer))
                throw new InvalidOperationException("The vehicle or trailer has no native hitch/attacher");
            var joint = trailer.GetComponent<ConfigurableJoint>();
            if (joint != null && joint.connectedBody != towRb) throw new InvalidOperationException("Trailer is attached to another vehicle");
            if (joint == null)
            {
                trailer.transform.rotation = car.transform.rotation;
                trailer.transform.position += hitch.position - attacher.position;
                joint = trailer.AddComponent<ConfigurableJoint>();
            }
            joint.connectedBody = towRb;
            joint.anchor = trailer.transform.InverseTransformPoint(attacher.position);
            joint.axis = Vector3.right;
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = car.transform.InverseTransformPoint(hitch.position);
            joint.secondaryAxis = Vector3.up;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
            joint.rotationDriveMode = RotationDriveMode.XYAndZ;
            joint.projectionDistance = 0.1f; joint.projectionAngle = 180f;
            joint.enableCollision = true;

            foreach (var f in car.GetComponents<PlayMakerFSM>())
            {
                var attached = f.FsmVariables.GetFsmBool("trailerAttached");
                if (attached != null) attached.Value = true;
                if (f.FsmName != "TrailerAttach" && f.FsmName != "TrailerLoad") continue;
                var v = f.FsmVariables.GetFsmGameObject("Trailer"); if (v != null) v.Value = trailer;
            }
            var rearBumper = Patrol.FindChild(car.transform, "hinge_bumper_rear");
            if (rearBumper != null) rearBumper.gameObject.SetActive(false);
            var leg = Patrol.FindChild(trailer.transform, "leg"); if (leg != null) leg.gameObject.SetActive(false);
            // TrailerLoad would run its raycast recipe and add a second joint. Leave the interactive
            // TrailerAttach FSM enabled only by the player's native driving controls, as on any car.
            foreach (var f in car.GetComponents<PlayMakerFSM>()) if (f.FsmName == "TrailerLoad") f.enabled = false;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // Seated pose for a Mixamo-rigged human: after the Animator has posed the body (LateUpdate), the thighs are swung
    // forward, the shins back down, the upper arms forward and the forearms up a little; the root is moved so the hips
    // sit on the target point (sitPos + offsets). All angles/offsets are read live from the config so they can be tuned
    // in the Apocasetter menu while looking at the driver.
    internal class Pose : MonoBehaviour
    {
        private Transform _root, _anchor;
        private Transform _hips, _lUpLeg, _lLeg, _rUpLeg, _rLeg, _lArm, _lForeArm, _rArm, _rForeArm;
        private bool _logged;

        internal static Pose Apply(GameObject driver, Transform anchor)
        {
            var p = driver.GetComponent<Pose>() ?? driver.AddComponent<Pose>();
            p._root = driver.transform;
            p._anchor = anchor;
            p.FindBones();
            return p;
        }

        private void FindBones()
        {
            var all = _root.GetComponentsInChildren<Transform>(true);
            _hips = Find(all, "Hips");
            _lUpLeg = Find(all, "LeftUpLeg"); _lLeg = Find(all, "LeftLeg");
            _rUpLeg = Find(all, "RightUpLeg"); _rLeg = Find(all, "RightLeg");
            _lArm = Find(all, "LeftArm"); _lForeArm = Find(all, "LeftForeArm");
            _rArm = Find(all, "RightArm"); _rForeArm = Find(all, "RightForeArm");
            var missing = new List<string>();
            if (_hips == null) missing.Add("Hips");
            if (_lUpLeg == null || _lLeg == null || _rUpLeg == null || _rLeg == null) missing.Add("legs");
            if (_lArm == null || _lForeArm == null || _rArm == null || _rForeArm == null) missing.Add("arms");
            Plugin.Log.LogInfo("Pose: bones " + (missing.Count == 0 ? "all found" : "missing " + string.Join(", ", missing.ToArray())));
        }

        // Mixamo names are "mixamorig:LeftUpLeg" (sometimes "mixamorig_LeftUpLeg" or plain); match the tail, exact word
        private static Transform Find(Transform[] all, string bone)
        {
            foreach (var t in all)
            {
                var n = t.name;
                int i = n.LastIndexOf(':'); if (i < 0) i = n.LastIndexOf('_');
                var tail = i >= 0 ? n.Substring(i + 1) : n;
                if (tail == bone) return t;
            }
            return null;
        }

        private void LateUpdate()
        {
            if (_root == null || _anchor == null) return;
            var right = _anchor.right;   // the car's right axis: rotating about it swings limbs forward/back

            // legs: thighs forward, shins back down (relative to the thigh)
            float thigh = Plugin.PoseThigh.Value, knee = Plugin.PoseKnee.Value;
            Swing(_lUpLeg, -thigh, right); Swing(_lLeg, knee, right);
            Swing(_rUpLeg, -thigh, right); Swing(_rLeg, knee, right);

            // arms: upper arms forward, forearms bent up a little more
            float arm = Plugin.PoseArm.Value, elbow = Plugin.PoseElbow.Value;
            Swing(_lArm, -arm, right); Swing(_lForeArm, -elbow, right);
            Swing(_rArm, -arm, right); Swing(_rForeArm, -elbow, right);

            // hips onto the seat point: move the whole body so the hips bone lands on anchor + offsets
            var target = _anchor.position + _anchor.right * Plugin.DriverOffsetX.Value + _anchor.up * Plugin.DriverOffsetY.Value
                       + _anchor.forward * Plugin.DriverOffsetZ.Value;
            var pivot = _hips != null ? _hips.position : _root.position;
            _root.position += target - pivot;

            if (!_logged) { _logged = true; Plugin.Verbose("Pose: applied, hips at " + target + " root at " + _root.position); }
        }

        private static void Swing(Transform bone, float degrees, Vector3 axis)
        {
            if (bone == null || degrees == 0f) return;
            bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }
    }
}

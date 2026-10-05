using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // One fixed seated limb pose for every live occupant. A ranged passenger may rotate that fixed pose through the
    // spine, but combat never receives control of the arms or legs.
    internal class Pose : MonoBehaviour
    {
        private Transform _root, _anchor;
        private Transform _hips, _spine, _spine1, _spine2;
        private Transform _lUpLeg, _lLeg, _rUpLeg, _rLeg, _lArm, _lForeArm, _lHand, _rArm, _rForeArm, _rHand;
        private ArmPoseProfile _armPose;
        private readonly List<WeaponRoot> _weapons = new List<WeaponRoot>();
        private bool _aimActive;
        private Vector3 _aimPoint;
        private float _aimYaw, _aimPitch;
        private bool _lockSeatRotation;
        private bool _useProfile = true;      // per-human arm/weapon profile (shooting pose) vs the generic driver arms
        private Quaternion _seatRotation;
        private bool _logged;

        internal static Pose Apply(GameObject occupant, Transform anchor, string humanType)
        {
            var p = occupant.GetComponent<Pose>() ?? occupant.AddComponent<Pose>();
            p._root = occupant.transform;
            p._anchor = anchor;
            Plugin.HumanArmPoses.TryGetValue(humanType ?? "", out p._armPose);
            p.FindBones();
            return p;
        }

        private void FindBones()
        {
            var all = _root.GetComponentsInChildren<Transform>(true);
            _hips = Find(all, "Hips");
            _spine = Find(all, "Spine"); _spine1 = Find(all, "Spine1"); _spine2 = Find(all, "Spine2");
            _lUpLeg = Find(all, "LeftUpLeg"); _lLeg = Find(all, "LeftLeg");
            _rUpLeg = Find(all, "RightUpLeg"); _rLeg = Find(all, "RightLeg");
            _lArm = Find(all, "LeftArm"); _lForeArm = Find(all, "LeftForeArm"); _lHand = Find(all, "LeftHand");
            _rArm = Find(all, "RightArm"); _rForeArm = Find(all, "RightForeArm"); _rHand = Find(all, "RightHand");
            FindWeapons();
            var missing = new List<string>();
            if (_hips == null) missing.Add("Hips");
            if (_spine == null || _spine1 == null || _spine2 == null) missing.Add("spine");
            if (_lUpLeg == null || _lLeg == null || _rUpLeg == null || _rLeg == null) missing.Add("legs");
            if (_lArm == null || _lForeArm == null || _lHand == null || _rArm == null || _rForeArm == null || _rHand == null) missing.Add("arms/hands");
            Plugin.Verbose("Pose: bones " + (missing.Count == 0 ? "all found" : "missing " + string.Join(", ", missing.ToArray())));
        }

        private void FindWeapons()
        {
            _weapons.Clear();
            if (_lHand == null || _armPose == null || _armPose.Weapon == null) return;
            foreach (Transform child in _lHand)
            {
                // Mixamo finger bones have no renderers. Each ranged weapon variant is a rendered direct child
                // of LeftHand, so storing every rendered child also covers variants enabled later by WeaponType.
                if (child.GetComponentInChildren<Renderer>(true) == null) continue;
                _weapons.Add(new WeaponRoot(child, child.localPosition, child.localRotation));
                Plugin.Verbose("Pose: weapon root " + child.name + " found under LeftHand");
            }
        }

        internal void SetAim(Vector3 point, bool active)
        {
            _aimPoint = point;
            _aimActive = active;
        }

        // A shooting mob at the wheel: generic driver arms (hands on the wheel), the per-human shooting profile only while firing.
        internal void SetShooting(bool on) { _useProfile = on; }

        internal void ConfigurePassengerAim()
        {
            _lockSeatRotation = true;
            _seatRotation = _root.localRotation;
        }

        // the occupant changed seats (passenger promoted to driver): follow the new anchor, no passenger aiming any more
        internal void Reseat(Transform anchor)
        {
            _anchor = anchor;
            _lockSeatRotation = false;
            _aimActive = false;
        }

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

        private int _lodFrame;
        // the camera position, read once per frame for every occupant
        private static int _camFrame = -1; private static bool _camOk; private static Vector3 _camPos;

        private static bool CameraPos(out Vector3 pos)
        {
            if (_camFrame != Time.frameCount)
            {
                _camFrame = Time.frameCount;
                var cam = Camera.main;
                _camOk = cam != null;
                _camPos = _camOk ? cam.transform.position : Vector3.zero;
            }
            pos = _camPos;
            return _camOk;
        }

        private void LateUpdate()
        {
            if (_root == null || _anchor == null) return;
            // Far from the camera the pose is refreshed less often: every 4th frame beyond 120 m, every 16th beyond 300 m (a convoy
            // spawning 350 m away is fourteen occupants; ~40 bone rotations each). The animator keeps the body in a sane idle in between.
            Vector3 camPos;
            if (CameraPos(out camPos))
            {
                float d2 = (camPos - _anchor.position).sqrMagnitude;
                int mask = d2 > 300f * 300f ? 15 : d2 > 120f * 120f ? 3 : 0;
                if (mask != 0 && (++_lodFrame & mask) != 0) return;
            }
            if (_lockSeatRotation) _root.localRotation = _seatRotation;
            var right = _anchor.right;
            var up = _anchor.up;

            float thigh = Plugin.PoseThigh.Value, knee = Plugin.PoseKnee.Value;
            Swing(_lUpLeg, -thigh, right); Swing(_lUpLeg, Plugin.PoseLeftLegCloser.Value, up); Swing(_lLeg, knee, right);
            Swing(_rUpLeg, -thigh, right); Swing(_rUpLeg, -Plugin.PoseRightLegCloser.Value, up); Swing(_rLeg, knee, right);

            if (_armPose != null && _useProfile)
            {
                ApplyBone(_lArm, _lForeArm, _armPose.LeftArm, right, up, false);
                ApplyBone(_lForeArm, _lHand, _armPose.LeftElbow, right, up, false);
                ApplyBone(_lHand, _lForeArm, _armPose.LeftHand, right, up, true);
                ApplyBone(_rArm, _rForeArm, _armPose.RightArm, right, up, false);
                ApplyBone(_rForeArm, _rHand, _armPose.RightElbow, right, up, false);
                ApplyBone(_rHand, _rForeArm, _armPose.RightHand, right, up, true);
                ApplyWeapons();
            }
            else
            {
                float arm = Plugin.PoseArm.Value, elbow = Plugin.PoseElbow.Value;
                Swing(_lArm, -arm, right); Swing(_lArm, Plugin.PoseLeftArmCloser.Value, up); Swing(_lForeArm, -elbow, right);
                Swing(_rArm, -arm, right); Swing(_rArm, -Plugin.PoseRightArmCloser.Value, up); Swing(_rForeArm, -elbow, right);
            }

            // Arms remain permanently in the driver pose. Rotate that fixed upper-body pose through the spine only.
            float wantedYaw = 0f, wantedPitch = 0f;
            if (_aimActive && _spine2 != null)
            {
                var local = _anchor.InverseTransformDirection(_aimPoint - _spine2.position);
                float flat = Mathf.Sqrt(local.x * local.x + local.z * local.z);
                if (flat > 0.001f)
                {
                    wantedYaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg,
                        -Plugin.FireArc.Value, Plugin.FireArc.Value);
                    wantedPitch = Mathf.Clamp(Mathf.Atan2(local.y, flat) * Mathf.Rad2Deg,
                        -Plugin.MaxAimPitch.Value, Plugin.MaxAimPitch.Value);
                }
            }
            float step = Plugin.AimTurnSpeed.Value * Time.deltaTime;
            _aimYaw = Mathf.MoveTowardsAngle(_aimYaw, wantedYaw, step);
            _aimPitch = Mathf.MoveTowardsAngle(_aimPitch, wantedPitch, step);
            AimSpine(_spine, 0.25f, up, right);
            AimSpine(_spine1, 0.35f, up, right);
            AimSpine(_spine2, 0.40f, up, right);

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

        private void AimSpine(Transform bone, float weight, Vector3 up, Vector3 right)
        {
            Swing(bone, _aimYaw * weight, up);
            var yawedRight = Quaternion.AngleAxis(_aimYaw, up) * right;
            Swing(bone, -_aimPitch * weight, yawedRight);
        }

        private static void ApplyBone(Transform bone, Transform joint, BonePoseConfig config, Vector3 right, Vector3 up, bool hand)
        {
            if (bone == null || config == null) return;
            Swing(bone, -config.Vertical.Value, right);
            Swing(bone, config.Horizontal.Value, up);
            Vector3 axis = joint != null ? joint.position - bone.position : bone.forward;
            // Hands pass their forearm as the joint, so reverse that vector to keep the twist axis shoulder-to-fingertips.
            if (hand && joint != null) axis = bone.position - joint.position;
            if (axis.sqrMagnitude > 0.000001f) Swing(bone, config.Rotation.Value, axis.normalized);
        }

        private void ApplyWeapons()
        {
            if (_armPose == null || _armPose.Weapon == null) return;
            var config = _armPose.Weapon;
            var offset = new Vector3(config.PositionX.Value, config.PositionY.Value, config.PositionZ.Value);
            var rotation = Quaternion.Euler(config.RotationX.Value, config.RotationY.Value, config.RotationZ.Value);
            foreach (var weapon in _weapons)
            {
                if (weapon.Transform == null) continue;
                weapon.Transform.localPosition = weapon.LocalPosition + offset;
                weapon.Transform.localRotation = weapon.LocalRotation * rotation;
            }
        }

        private sealed class WeaponRoot
        {
            internal readonly Transform Transform;
            internal readonly Vector3 LocalPosition;
            internal readonly Quaternion LocalRotation;

            internal WeaponRoot(Transform transform, Vector3 localPosition, Quaternion localRotation)
            {
                Transform = transform;
                LocalPosition = localPosition;
                LocalRotation = localRotation;
            }
        }
    }
}

using UnityEngine;

namespace Apocapatrol
{
    // Runs after animation/pose work, including frames where the distant pose is throttled.
    [DefaultExecutionOrder(20000)]
    [ES3NonSerializable]
    internal sealed class RiderAnchorGuard : MonoBehaviour
    {
        private RiderGuard _rider;
        private Transform _anchor;
        private Rigidbody _body;
        private bool _gunner;

        internal static void Attach(GameObject who, RiderGuard rider, Transform anchor, bool gunner)
        {
            var guard = who.AddComponent<RiderAnchorGuard>();
            guard._rider = rider; guard._anchor = anchor; guard._gunner = gunner;
            guard._body = who.GetComponent<Rigidbody>();
            // A culled Animator retains the last bone pose; procedural adjustments would then reuse it.
            var animator = who.GetComponentInChildren<Animator>();
            if (animator != null) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private void LateUpdate()
        {
            if (_rider == null || !_rider.Mounted || _anchor == null) return;
            if (transform.parent != _anchor) transform.SetParent(_anchor, true);
            // Warboys' crouch/throw clips put their feet at the anchor. Gunner height is owned by Pose,
            // which also supports the legacy bone pose; keep its calculated offset.
            if (!_gunner) transform.position = _anchor.position;
            if (_body == null) return;
            if (!_body.isKinematic) _body.isKinematic = true;
            // A parented kinematic body must follow the current transform when its platform sleeps/wakes.
            if (_body.position != transform.position) _body.position = transform.position;
            if (_body.rotation != transform.rotation) _body.rotation = transform.rotation;
        }
    }
}

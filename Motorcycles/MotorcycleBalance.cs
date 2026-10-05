using NWH.VehiclePhysics2;
using UnityEngine;

namespace Apocapatrol.Motorcycles
{
    [DefaultExecutionOrder(-100)]
    public sealed class MotorcycleBalance : MonoBehaviour
    {
        private VehicleController vehicle;
        private Rigidbody body;
        private float targetLean;

        private void Awake()
        {
            vehicle = GetComponent<VehicleController>();
            body = GetComponent<Rigidbody>();
        }

        private void FixedUpdate()
        {
            if (!vehicle.IsInitialized || !vehicle.isActiveAndEnabled || body.isKinematic) return;
            foreach (var wheel in vehicle.powertrain.wheels)
            {
                var controller = (NWH.WheelController3D.WheelController)wheel.wheelUAPI;
                if (!controller.IsGrounded || controller.spring.maxLength < .001f) continue;
                // NWH limits tyre impulses using translational mass, without accounting for roll inertia.
                // Applying that impulse below this narrow body's CoM produces an alternating roll impulse.
                // Its supported force-height setting lets the lean controller own roll instead.
                float height = Vector3.Dot(body.worldCenterOfMass - controller.HitPoint, transform.up);
                controller.forceApplicationPointDistance = Mathf.Clamp(height / controller.spring.maxLength, 0f, 5f);
            }
            var axis = transform.forward;
            var worldUp = Vector3.ProjectOnPlane(Vector3.up, axis).normalized;
            var bodyUp = Vector3.ProjectOnPlane(transform.up, axis).normalized;
            // Do not try to stand the bike on its wheels while upside down or nearly vertical.
            if (worldUp.sqrMagnitude < .1f || Vector3.Dot(bodyUp, worldUp) < .2f) { targetLean = 0; return; }
            float dt = Time.fixedDeltaTime;
            float speed = Mathf.Abs(vehicle.Speed);
            float speedLean = .7f * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(3, 10, speed))
                + .3f * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(10, 25, speed));
            float requestedLean = vehicle.input.Steering * 25f * speedLean;
            targetLean = Mathf.MoveTowards(targetLean, requestedLean, 60f * dt);
            var desiredUp = Quaternion.AngleAxis(-targetLean, axis) * worldUp;
            float error = Vector3.SignedAngle(bodyUp, desiredUp, axis) * Mathf.Deg2Rad;
            float rollRate = Vector3.Dot(body.angularVelocity, axis);
            // Stable, critically damped PD in radians. The old module differentiated degree errors
            // with a 50x multiplier: its damping alone could reverse roll velocity each fixed step.
            const float frequency = 8f;
            float acceleration = (frequency * frequency * error - 2f * frequency * rollRate)
                / (1f + 2f * frequency * dt + frequency * frequency * dt * dt);
            // Scale torque by the actual inertia, including a loaded save's inertia tensor.
            var inertiaAxis = Quaternion.Inverse(body.rotation * body.inertiaTensorRotation) * axis;
            var inertia = body.inertiaTensor;
            float inverseInertia = inertiaAxis.x * inertiaAxis.x / Mathf.Max(.01f, inertia.x)
                + inertiaAxis.y * inertiaAxis.y / Mathf.Max(.01f, inertia.y)
                + inertiaAxis.z * inertiaAxis.z / Mathf.Max(.01f, inertia.z);
            body.AddTorque(axis * (Mathf.Clamp(acceleration, -80f, 80f) / Mathf.Max(.001f, inverseInertia)), ForceMode.Force);
        }
    }
}

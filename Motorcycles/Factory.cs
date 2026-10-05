using System;
using System.Collections.Generic;
using System.Linq;
using ES3Internal;
using NWH.Common.CoM;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.Powertrain.Wheel;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocapatrol.Motorcycles
{
    public static class Factory
    {
        // Stable, distinct from the shipped Crossbreed prefab ID.
        public const long PrefabId = 735710202610050001L;
        public const string Name = "Motorcycle";
        private static GameObject template, holder;

        public static GameObject Source(string name)
        {
            // Never clone a player's modified scene vehicle as the factory chassis.
            var source = Resources.FindObjectsOfTypeAll<ES3Prefab>()
                .FirstOrDefault(p => p != null && !p.gameObject.scene.IsValid() && p.name == name);
            if (source == null) throw new InvalidOperationException("Prefab '" + name + "' is not loaded. Load a game first.");
            return source.gameObject;
        }

        public static GameObject Template()
        {
            if (template != null) return template;
            var source = Source("Crossbreed");
            if (holder == null)
            {
                holder = new GameObject("Apocapatrol.MotorcycleTemplates") { hideFlags = HideFlags.HideAndDontSave };
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
            }
            var copy = Object.Instantiate(source, holder.transform, false);
            try
            {
                copy.SetActive(false);
                copy.name = Name;
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                Convert(copy, 220f);
                copy.GetComponent<ES3Prefab>().prefabId = PrefabId;
                template = copy;
                Apocapatrol.Plugin.Log.LogInfo("Motorcycle chassis created from Crossbreed: two wheels, rear car geometry and mounts disabled.");
                return template;
            }
            catch { Object.Destroy(copy); throw; }
        }

        public static void Convert(GameObject copy, float mass)
        {
            if (copy.activeInHierarchy) throw new InvalidOperationException("Chassis conversion requires an inactive clone.");
            var vc = copy.GetComponent<VehicleController>();
            if (vc == null || vc.IsInitialized) throw new InvalidOperationException("Expected an uninitialized Crossbreed controller.");
            var model = Required(copy, "parts/model");
            Required(copy, "parts/model/colliders_bike");
            Required(copy, "hinge_wheel_F"); Required(copy, "hinge_wheel_M");
            foreach (var path in new[] { "parts/model/colliders_car", "hinge_wheel_RL", "hinge_wheel_RR",
                "hinge_bumper_rear", "hinge_roofrack", "hinge_trunk", "hinge_seat_rear", "hitch", "hitch_raycast",
                "parts/interior_car_light", "parts/toolset_1", "parts/toolset_2" })
                Disable(Required(copy, path));
            // Duplicate names (fabric, wire, bottom) require enumeration, not Transform.Find.
            var rearNames = new HashSet<string>(new[] { "fabric", "wire", "roof", "bottom", "wheel cover", "springs", "suspension", "body rear" });
            foreach (Transform child in model) if (rearNames.Contains(child.name)) Disable(child);
            foreach (var collider in copy.GetComponents<BoxCollider>()) if (collider.center.z < -.5f) collider.enabled = false;
            foreach (var fsm in copy.GetComponents<PlayMakerFSM>())
                if (fsm.FsmName == "TrailerAttach" || fsm.FsmName == "TrailerLoad") fsm.enabled = false;
            foreach (var box in Required(copy, "parts/PhysicsLock").GetComponents<BoxCollider>())
            {
                box.center = new Vector3(0, .65f, 1.25f);
                box.size = new Vector3(.55f, 1.05f, 1.9f);
            }

            var front = vc.powertrain.wheels.Single(w => w.name == "hinge_wheel_F");
            var rear = vc.powertrain.wheels.Single(w => w.name == "hinge_wheel_M");
            vc.powertrain.wheels = new List<WheelComponent> { front, rear };
            vc.powertrain.wheelGroups = new List<WheelGroup> {
                new WheelGroup { name = "Motorcycle front", addAckerman = false, steerCoefficient = 1, brakeCoefficient = 1 },
                new WheelGroup { name = "Motorcycle rear", addAckerman = false, steerCoefficient = 0, brakeCoefficient = .7f, handbrakeCoefficient = 1 }
            };
            front.wheelGroupSelector = new WheelGroupSelector { index = 0 };
            rear.wheelGroupSelector = new WheelGroupSelector { index = 1 };
            foreach (var wheel in vc.powertrain.wheels)
                ((NWH.WheelController3D.WheelController)wheel.wheelUAPI).loadContribution = .5f;
            // Recompute name hashes in the running Mono runtime, and remove the dormant car differential.
            vc.powertrain.differentials.Clear();
            front.Input = null;
            vc.powertrain.engine.Output = vc.powertrain.clutch;
            vc.powertrain.clutch.Output = vc.powertrain.transmission;
            vc.powertrain.transmission.Output = rear;
            vc.wheelbase = Mathf.Abs(front.wheelUAPI.transform.localPosition.z - rear.wheelUAPI.transform.localPosition.z);

            var rb = copy.GetComponent<Rigidbody>();
            var com = copy.GetComponent<VariableCenterOfMass>();
            com.useDefaultMass = false; com.useMassAffectors = false;
            com.baseMass = mass; com.combinedMass = mass;
            com.useDefaultCenterOfMass = false;
            com.centerOfMass = new Vector3(0, -.32f, .64f);
            com.dimensions = new Vector3(.6f, 1.1f, 2.15f);
            var aero = copy.GetComponent<NWH.VehiclePhysics2.Modules.Aerodynamics.AerodynamicsModuleWrapper>();
            if (aero != null) aero.module.dimensions = com.dimensions;
            // Explicit motorcycle inertia prevents the removed cabin's inertia surviving the clone.
            com.useDefaultInertia = false;
            com.inertiaTensor = new Vector3(mass * .49f, mass * .42f, mass * .13f);
            rb.mass = mass; rb.centerOfMass = com.centerOfMass;
            rb.inertiaTensor = com.inertiaTensor; rb.inertiaTensorRotation = Quaternion.identity;
            rb.isKinematic = false; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            MotorcycleIntegration.TrimRearCollision(copy);
            copy.AddComponent<MotorcycleRearAccess>();
            copy.AddComponent<MotorcycleBalance>();
        }

        private static Transform Required(GameObject root, string path) => root.transform.Find(path)
            ?? throw new InvalidOperationException("Crossbreed layout changed: missing " + path);

        private static void Disable(Transform node)
        {
            // Retain native Easy Save local reference IDs and FSM targets. Disabled objects have no model/physics presence.
            foreach (var renderer in node.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            foreach (var collider in node.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var behaviour in node.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
            node.gameObject.SetActive(false);
        }

        public static GameObject Create(Vector3 position, Quaternion rotation, bool equip, Func<int> nextId)
        {
            var prefab = Template();
            var kit = new[] {
                new[] { "hinge_wheel_F", "motorcycle_wheel" }, new[] { "hinge_wheel_M", "motorcycle_wheel" },
                new[] { "hinge_engine", "594cc I2 23HP 39Nm Gasoline" }, new[] { "hinge_radiator", "radiator_motorcycle" },
                new[] { "hinge_exhaust", "exhaust_motorcycle" }, new[] { "hinge_seat_driver", "seat_motorcycle" }
            };
            var assets = equip ? kit.Select(k => Source(k[1])).ToArray() : new GameObject[0];
            var bike = Object.Instantiate(prefab, holder.transform, false);
            try
            {
                bike.name = Name + "(Clone)" + nextId();
                for (int i = 0; i < assets.Length; i++)
                {
                    var part = Object.Instantiate(assets[i], Required(bike, kit[i][0]), false);
                    part.name = assets[i].name + "(Clone)" + nextId();
                    part.tag = "vehPart"; part.layer = 8;
                    part.transform.localPosition = Vector3.zero;
                    part.transform.localRotation = Quaternion.identity;
                    // Only newly-created components in an inactive clone are destroyed immediately.
                    var partBody = part.GetComponent<Rigidbody>();
                    if (partBody != null) Object.DestroyImmediate(partBody);
                    part.SetActive(true);
                }
                var liquid = MotorcycleIntegration.FindFsm(Required(bike, "Fuel").gameObject, "LiquidAmount");
                liquid.FsmVariables.GetFsmFloat("Liquid").Value = 10f;
                bike.transform.SetParent(null, false);
                bike.transform.SetPositionAndRotation(position, rotation);
                bike.SetActive(true);
                return bike;
            }
            catch { Object.Destroy(bike); throw; }
        }
    }
}

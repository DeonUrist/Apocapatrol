using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // Loot in a truck bed. Every vehicle has parts/PhysicsLock: two BoxColliders on layer 18 (floor and ceiling of the cargo
    // volume). Vanilla items carry a LockPhysics FSM that raycasts up and down on that layer and, when both hit, parents the
    // item to the lower box and destroys its Rigidbody - that is how cargo rides along until the player grabs it (GrabItem
    // sends LockPhysics_OFF, the FSM re-adds the Rigidbody). We spawn the items inside that volume with the vanilla spawn
    // recipe (registered, so the game saves them) and lock them the same way at once.
    internal static class Cargo
    {
        private class Slot { public GameObject Prefab; public int Count; }

        // "prefab:count;prefab:min-max;..." (prefab names or in-game item names)
        private static List<Slot> Parse(string spec)
        {
            var slots = new List<Slot>();
            if (string.IsNullOrEmpty(spec)) return slots;
            foreach (var raw in spec.Split(';', ','))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                int colon = part.LastIndexOf(':');
                string name = colon >= 0 ? part.Substring(0, colon).Trim() : part;
                string countText = colon >= 0 ? part.Substring(colon + 1).Trim() : "1";
                int count = 1;
                int dash = countText.IndexOf('-');
                if (dash > 0)
                {
                    int lo, hi;
                    if (int.TryParse(countText.Substring(0, dash), out lo) && int.TryParse(countText.Substring(dash + 1), out hi))
                        count = UnityEngine.Random.Range(Mathf.Min(lo, hi), Mathf.Max(lo, hi) + 1);
                }
                else int.TryParse(countText, out count);
                var prefab = Prefabs.FindAny(name);
                if (prefab == null) { Plugin.Log.LogWarning("Cargo: item prefab not found: " + name); continue; }
                if (count > 0) slots.Add(new Slot { Prefab = prefab, Count = Mathf.Min(count, 200) });
            }
            return slots;
        }

        // Fills the car's PhysicsLock volume with the items of the spec. Returns the spawned items (already locked).
        internal static List<GameObject> Load(GameObject car, string spec)
        {
            var items = new List<GameObject>();
            var slots = Parse(spec);
            if (slots.Count == 0) return items;

            var lockGo = Patrol.FindChild(car.transform, "PhysicsLock");
            var boxes = lockGo != null ? lockGo.GetComponents<BoxCollider>() : null;
            if (boxes == null || boxes.Length < 2)
            {
                Plugin.Log.LogWarning("Cargo: " + car.name + " has no parts/PhysicsLock with two boxes; no cargo");
                return items;
            }
            var lower = boxes[0].center.y <= boxes[1].center.y ? boxes[0] : boxes[1];
            var upper = lower == boxes[0] ? boxes[1] : boxes[0];
            var t = lockGo;
            // cargo volume in the PhysicsLock object's local space: XZ = overlap of both boxes, Y = between them
            float minX = Mathf.Max(lower.center.x - lower.size.x / 2f, upper.center.x - upper.size.x / 2f) + 0.15f;
            float maxX = Mathf.Min(lower.center.x + lower.size.x / 2f, upper.center.x + upper.size.x / 2f) - 0.15f;
            float minZ = Mathf.Max(lower.center.z - lower.size.z / 2f, upper.center.z - upper.size.z / 2f) + 0.15f;
            float maxZ = Mathf.Min(lower.center.z + lower.size.z / 2f, upper.center.z + upper.size.z / 2f) - 0.15f;
            float floorY = lower.center.y + lower.size.y / 2f;
            float ceilY = upper.center.y - upper.size.y / 2f;
            var scale = t.lossyScale;
            Plugin.Verbose("Cargo: volume x " + minX.ToString("0.00") + ".." + maxX.ToString("0.00") + " z " + minZ.ToString("0.00") + ".." + maxZ.ToString("0.00")
                + " y " + floorY.ToString("0.00") + ".." + ceilY.ToString("0.00") + " (local, scale " + scale + ")");
            if (maxX <= minX || maxZ <= minZ || ceilY <= floorY) { Plugin.Log.LogWarning("Cargo: PhysicsLock volume is degenerate on " + car.name); return items; }

            float x = minX, z = minZ, y = floorY, rowDepth = 0f, layerHeight = 0f;
            int placed = 0, wanted = 0;
            foreach (var slot in slots)
            {
                wanted += slot.Count;
                for (int i = 0; i < slot.Count; i++)
                {
                    var go = UnityEngine.Object.Instantiate(slot.Prefab, t.TransformPoint(new Vector3(x, y + 5f, z)), t.rotation);
                    go.SetActive(true);
                    // footprint from its renderers (world AABB, item aligned with the truck so it is close enough)
                    var b = Bounds(go);
                    float w = Mathf.Max(0.1f, b.size.x / Mathf.Max(scale.x, 0.01f)) + 0.04f;
                    float d = Mathf.Max(0.1f, b.size.z / Mathf.Max(scale.z, 0.01f)) + 0.04f;
                    float h = Mathf.Max(0.05f, b.size.y / Mathf.Max(scale.y, 0.01f)) + 0.02f;
                    if (x + w > maxX) { x = minX; z += rowDepth; rowDepth = 0f; }
                    if (z + d > maxZ) { z = minZ; y += layerHeight; layerHeight = 0f; x = minX; rowDepth = 0f; }
                    if (y + h > ceilY) { Plugin.Log.LogInfo("Cargo: bed full after " + placed + " of " + wanted + " items"); UnityEngine.Object.Destroy(go); goto done; }
                    // put it down: pivot offset so the renderer bottom sits on the floor and the AABB corner on the cursor
                    var local = new Vector3(x + w / 2f, y, z + d / 2f);
                    var pivotWorld = go.transform.position;
                    var bottomWorld = new Vector3(b.center.x, b.min.y, b.center.z);
                    var pivotOffsetLocal = t.InverseTransformVector(pivotWorld - bottomWorld);
                    go.transform.position = t.TransformPoint(local + pivotOffsetLocal);
                    go.transform.rotation = t.rotation * Quaternion.Euler(0f, UnityEngine.Random.Range(-8f, 8f), 0f);
                    x += w; rowDepth = Mathf.Max(rowDepth, d); layerHeight = Mathf.Max(layerHeight, h);

                    if (Plugin.RegisterWithGame.Value) { Register.Name(go, slot.Prefab.name); Register.Add(go, false); }
                    Lock(go, t);
                    items.Add(go);
                    placed++;
                }
            }
            done:
            Plugin.Log.LogInfo("Cargo: " + placed + " item(s) loaded into " + car.name);
            return items;
        }

        // what the item's own LockPhysics FSM would do 3 s later: parent to the PhysicsLock object, no Rigidbody
        private static void Lock(GameObject go, Transform lockGo)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
            go.transform.SetParent(lockGo, true);
        }

        // after the FSMs have started: park the LockPhysics FSMs in "off" so they do not redo the lock (and warn about a missing Rigidbody)
        internal static void SettleFsms(List<GameObject> items)
        {
            foreach (var go in items)
            {
                if (go == null) continue;
                foreach (var f in go.GetComponents<PlayMakerFSM>())
                    if (f.FsmName == "LockPhysics" && f.Fsm.Initialized && f.ActiveStateName != "off")
                        f.Fsm.SetState("off");
            }
        }

        private static Bounds Bounds(GameObject go)
        {
            bool any = false; var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r.GetType().Name == "ParticleSystemRenderer") continue;   // smoke, not the item
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any)
                foreach (var c in go.GetComponentsInChildren<Collider>())
                { if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds); }
            if (!any) b = new Bounds(go.transform.position, new Vector3(0.3f, 0.3f, 0.3f));
            return b;
        }
    }
}

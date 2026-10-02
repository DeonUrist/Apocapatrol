using System;
using System.Collections;
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
        private class Slot { public GameObject Prefab; public int Count; public float Condition = -1f; }
        // items that must keep a set condition (spec "#N"): applied after the build's part-condition roll, which covers the whole car
        private static readonly Dictionary<GameObject, float> _forcedCondition = new Dictionary<GameObject, float>();

        // "prefab:count;prefab:min-max;..." (prefab names or in-game item names). Extras: "a|b|c:3" = each of the 3 a random one of
        // a, b, c; "prefab:1@50" = this entry only with a 50 % chance (and not scaled by the loot multiplier - a barrel is a barrel);
        // "@15/30" = 15 % on a basic loot truck, 30 % on an advanced one; "#100" = the item's condition is set to 100 %.
        private static List<Slot> Parse(string spec)
        {
            var slots = new List<Slot>();
            if (string.IsNullOrEmpty(spec)) return slots;
            foreach (var raw in spec.Split(';', ','))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                float condition = -1f;
                int hash = part.LastIndexOf('#');
                if (hash > 0 && float.TryParse(part.Substring(hash + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out condition))
                    part = part.Substring(0, hash).Trim();
                else condition = -1f;
                float chance = 100f;
                int at = part.LastIndexOf('@');
                if (at > 0)
                {
                    string ct = part.Substring(at + 1).Trim();
                    int slash = ct.IndexOf('/');
                    if (slash >= 0) ct = _scale > 1.01f ? ct.Substring(slash + 1) : ct.Substring(0, slash);   // advanced loot truck = template factor > 1
                    if (float.TryParse(ct.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out chance))
                        part = part.Substring(0, at).Trim();
                    else chance = 100f;
                }
                if (chance < 100f && UnityEngine.Random.value * 100f >= chance) continue;
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
                var choices = new List<GameObject>();
                foreach (var alt in name.Split('|'))
                {
                    string n = alt.Trim();
                    if (n.Length == 0) continue;
                    var pf = Prefabs.FindAny(n);
                    if (pf == null) Plugin.Log.LogWarning("Cargo: item prefab not found: " + n); else choices.Add(pf);
                }
                if (choices.Count == 0) continue;
                if (chance >= 100f) count = Scaled(count);
                count = Mathf.Min(count, 200);
                if (choices.Count == 1) { if (count > 0) slots.Add(new Slot { Prefab = choices[0], Count = count, Condition = condition }); }
                else for (int i = 0; i < count; i++) slots.Add(new Slot { Prefab = choices[UnityEngine.Random.Range(0, choices.Count)], Count = 1, Condition = condition });
            }
            return slots;
        }

        private static readonly string[] Weapons =
        {
            "akm_drum", "akm_trash", "akms", "9mm_borz_smg", "crossbow", "folk_17", "m16a1", "redmark_m11", "redmark_m11_scoped",
            "rochester_m24", "rochester_m24_chopped", "slamberg_500", "slamberg_500_chopped", "slamfire_shotgun",
            "22_pipe_pistol", "22_pipe_revolver", "22_pipe_smg", "blastlance_1"
        };
        private static readonly string[] AmmoBoxes =
        {
            // the big boxes (ammo_box_<calibre> are the small everyday ones)
            "ammo_box_big_12gauge", "ammo_box_big_20gauge", "ammo_box_big_22", "ammo_box_big_3006", "ammo_box_big_556mm", "ammo_box_big_762mm",
            "ammo_box_big_9mm", "ammo_box_big_arrow"
        };

        // Weapons truck: 2-4 random weapons and 3-8 big ammo boxes, each box a random calibre (before the loot multiplier).
        internal static string WeaponsSpec()
        {
            var parts = new List<string>();
            int guns = UnityEngine.Random.Range(2, 5);   // 2-4
            for (int i = 0; i < guns; i++) parts.Add(Weapons[UnityEngine.Random.Range(0, Weapons.Length)] + ":1");
            int boxes = UnityEngine.Random.Range(3, 9);
            for (int i = 0; i < boxes; i++) parts.Add(AmmoBoxes[UnityEngine.Random.Range(0, AmmoBoxes.Length)] + ":1");
            return string.Join(";", parts.ToArray());
        }

        // The item spec of a loot type (hardcoded in CarTemplate.LootDefaults; "@weapons" is rolled fresh every time).
        internal static string SpecFor(string key)
        {
            foreach (var d in CarTemplate.LootDefaults)
                if (string.Equals(d[0], key, StringComparison.OrdinalIgnoreCase)) return d[1] == "@weapons" ? WeaponsSpec() : d[1];
            return "";
        }

        private static float _scale = 1f;   // the template's per-spawn factor for the current load

        // [Loot] Multiplier x template factor: counts are scaled, fractions rounded by chance (x1.5 of 1 = 1 or 2), 0 = nothing at all
        private static int Scaled(int count)
        {
            float m = Plugin.LootMultiplier.Value * _scale;
            if (m <= 0f) return 0;
            float v = count * m;
            int n = Mathf.FloorToInt(v);
            if (UnityEngine.Random.value < v - n) n++;
            return n;
        }

        // Picks a loot type by the [Loot] XChance weights (they should add up to 100; any total works, it is normalised).
        internal static string RollLootType()
        {
            float total = 0f;
            foreach (var d in CarTemplate.LootDefaults) total += Mathf.Max(0f, Plugin.LootChance(d[0]));
            if (total <= 0f) return "";
            float r = UnityEngine.Random.Range(0f, total), acc = 0f;
            foreach (var d in CarTemplate.LootDefaults)
            {
                acc += Mathf.Max(0f, Plugin.LootChance(d[0]));
                if (r < acc) return d[0];
            }
            return CarTemplate.LootDefaults[CarTemplate.LootDefaults.Length - 1][0];
        }

        private const int ItemsPerFrame = 4;   // a full bed (30+ items, each an Instantiate + bounds + a raycast) is spread over frames

        // Fills the car's PhysicsLock volume with the items of the spec, a few items per frame (a coroutine step of the build).
        // The spawned items (already locked) are appended to `items`.
        // ------------------------------------------------------------ back-seat loot (cars, 1.13.0)
        // Every raider car (not trucks, not motorcycles) may carry a box of ammo or a bandage on its back seat. Chance = [Scaling]
        // LootMultiplier as a % (1 = 100 %), doubled for an advanced car; every full 100 % is one sure item, the rest the chance of one
        // more (1.5 = one item + 50 % for a second). Each item: 30 % a bandage, 70 % a small ammo box of a random calibre.
        private const string BackSeatBandage = "bandage_1";
        private static readonly string[] SmallAmmo =
            { "ammo_box_12gauge", "ammo_box_20gauge", "ammo_box_22", "ammo_box_3006", "ammo_box_556mm", "ammo_box_762mm", "ammo_box_9mm", "ammo_arrow" };
        private const int BackSeatMax = 6;

        internal static void BackSeatLoot(GameObject car, bool advanced, List<GameObject> items)
        {
            float chance = Mathf.Max(0f, Plugin.LootMultiplier.Value) * (advanced ? 2f : 1f);
            int n = Mathf.FloorToInt(chance);
            if (UnityEngine.Random.value < chance - n) n++;
            n = Mathf.Min(n, BackSeatMax);
            if (n <= 0) return;
            var lockGo = Patrol.FindChild(car.transform, "PhysicsLock");
            if (lockGo == null) { Plugin.Verbose("Back seat: " + car.name + " has no PhysicsLock, no loot"); return; }
            var ct = car.transform;
            // where the back seat is: the rear seat hinge if the frame has one, else behind the middle of the two front seats
            Vector3 spot;
            var rear = Patrol.FindChild(ct, "hinge_seat_rear");
            var drv = Patrol.FindChild(ct, "hinge_seat_driver");
            var pax = Patrol.FindChild(ct, "hinge_seat_passenger");
            if (rear != null) spot = rear.position;
            else if (drv != null && pax != null) spot = (drv.position + pax.position) * 0.5f - ct.forward * 0.65f;
            else if (drv != null) spot = drv.position - ct.forward * 0.6f;
            else { Plugin.Verbose("Back seat: " + car.name + " has no seat hinges, no loot"); return; }
            float halfWidth = (drv != null && pax != null) ? Mathf.Max(0.15f, Vector3.Distance(drv.position, pax.position) * 0.5f) : 0.2f;
            int placed = 0;
            for (int i = 0; i < n; i++)
            {
                string name = UnityEngine.Random.value < 0.3f ? BackSeatBandage : SmallAmmo[UnityEngine.Random.Range(0, SmallAmmo.Length)];
                var prefab = Prefabs.FindAny(name);
                if (prefab == null) { Plugin.Log.LogWarning("Back seat: prefab not found: " + name); continue; }
                // spread across the seat, a little back and forth
                var p = spot + ct.right * UnityEngine.Random.Range(-halfWidth, halfWidth) + ct.forward * UnityEngine.Random.Range(-0.12f, 0.12f);
                var go = UnityEngine.Object.Instantiate(prefab, p + ct.up * 1.5f, ct.rotation);
                go.SetActive(true);
                // random rotation: any heading, a quarter of them lying on a side
                var rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                if (UnityEngine.Random.value < 0.25f) rot = rot * Quaternion.Euler(0f, 0f, UnityEngine.Random.value < 0.5f ? 90f : -90f);
                go.transform.rotation = ct.rotation * rot;
                // drop it onto the seat / floor: the highest of the car's own solid colliders under the spot (not the occupants)
                float floor = SeatSurface(car, p);
                var b = Bounds(go);
                float lift = go.transform.position.y - b.min.y;                // pivot height above the box's bottom
                var centre = go.transform.position - b.center; centre.y = 0f;     // the renderer centred on the spot
                go.transform.position = new Vector3(p.x, floor + lift + 0.01f, p.z) + centre;
                Register.Name(go, prefab.name); Register.Add(go, false);
                Lock(go, lockGo);
                items.Add(go);
                placed++;
            }
            Plugin.Verbose("Back seat: " + placed + " item(s) in " + car.name + " (chance " + (chance * 100f).ToString("0") + " %" + (advanced ? ", advanced" : "") + ")");
        }

        // the top of the car's own solid geometry under a point: a ray from 1.2 m above, down 2.5 m, ignoring triggers, the crew and loose items
        private static float SeatSurface(GameObject car, Vector3 p)
        {
            float best = float.MinValue;
            foreach (var h in Physics.RaycastAll(p + car.transform.up * 1.2f, -car.transform.up, 2.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || !h.collider.transform.IsChildOf(car.transform)) continue;
                bool occupant = false;
                for (var a = h.collider.transform; a != null && a != car.transform; a = a.parent)
                    if (a.name.IndexOf("(Driver)", StringComparison.Ordinal) >= 0 || a.name.IndexOf("(Passenger)", StringComparison.Ordinal) >= 0
                        || a.name.IndexOf("_Dead", StringComparison.Ordinal) >= 0 || a.name == "PhysicsLock") { occupant = true; break; }
                if (occupant) continue;
                if (h.point.y > best) best = h.point.y;
            }
            return best > float.MinValue ? best : p.y;
        }

        internal static IEnumerator Load(GameObject car, string spec, float factor, List<GameObject> items)
        {
            if (Plugin.LootMultiplier.Value <= 0f) { Plugin.Verbose("Cargo: loot multiplier 0, nothing loaded"); yield break; }
            _scale = Mathf.Max(0f, factor);
            var slots = Parse(spec);
            if (slots.Count == 0) yield break;

            var lockGo = Patrol.FindChild(car.transform, "PhysicsLock");
            var boxes = lockGo != null ? lockGo.GetComponents<BoxCollider>() : null;
            if (boxes == null || boxes.Length < 1)
            {
                Plugin.Log.LogWarning("Cargo: " + car.name + " has no parts/PhysicsLock box; no cargo");
                yield break;
            }
            // each BoxCollider is one lock zone (an item inside it, or up to 10 m above it, gets locked by its own LockPhysics raycasts);
            // the bed is the zone with the largest footprint
            BoxCollider bed = boxes[0];
            foreach (var bx in boxes) if (bx.size.x * bx.size.z > bed.size.x * bed.size.z) bed = bx;
            var t = lockGo;
            float minX = bed.center.x - bed.size.x / 2f + 0.15f, maxX = bed.center.x + bed.size.x / 2f - 0.15f;
            float minZ = bed.center.z - bed.size.z / 2f + 0.15f, maxZ = bed.center.z + bed.size.z / 2f - 0.15f;
            float floorY = bed.center.y - bed.size.y / 2f + 0.02f;
            float ceilY = bed.center.y + bed.size.y / 2f;
            var scale = t.lossyScale;
            foreach (var bx in boxes) Plugin.Verbose("Cargo: lock zone centre " + bx.center.ToString("0.00") + " size " + bx.size.ToString("0.00") + (bx == bed ? " (bed)" : ""));
            Plugin.Verbose("Cargo: volume x " + minX.ToString("0.00") + ".." + maxX.ToString("0.00") + " z " + minZ.ToString("0.00") + ".." + maxZ.ToString("0.00")
                + " y " + floorY.ToString("0.00") + ".." + ceilY.ToString("0.00") + " (local, scale " + scale + ")");
            if (maxX <= minX || maxZ <= minZ || ceilY <= floorY) { Plugin.Log.LogWarning("Cargo: PhysicsLock volume is degenerate on " + car.name); yield break; }

            // scatter over the bed floor: random XZ that keeps clear of the items already placed (a few tries, then stack on top)
            var placedPos = new List<Vector3>(); var placedR = new List<float>(); var placedTop = new List<float>();
            int placed = 0, wanted = 0, sinceYield = 0;
            bool full = false;
            foreach (var slot in slots)
            {
                if (full) break;
                wanted += slot.Count;
                for (int i = 0; i < slot.Count; i++)
                {
                    if (sinceYield >= ItemsPerFrame) { sinceYield = 0; yield return null; if (car == null || t == null) yield break; }
                    sinceYield++;
                    var go = UnityEngine.Object.Instantiate(slot.Prefab, t.TransformPoint(new Vector3(0f, ceilY + 5f, 0f)), t.rotation);
                    go.SetActive(true);
                    // orientation: random yaw; cans / bottles / canisters lie on their side more often than not
                    var rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                    if (IsCanLike(slot.Prefab.name) && UnityEngine.Random.value < 0.6f)
                        rot = rot * Quaternion.Euler(UnityEngine.Random.value < 0.5f ? 90f : -90f, 0f, UnityEngine.Random.Range(-10f, 10f));
                    go.transform.rotation = t.rotation * rot;
                    var b = Bounds(go);
                    float r = Mathf.Max(b.size.x, b.size.z) / 2f / Mathf.Max(scale.x, 0.01f) + 0.03f;
                    float h = b.size.y / Mathf.Max(scale.y, 0.01f);
                    float lx = 0f, lz = 0f, baseY = -1f; bool free = false;
                    for (int attempt = 0; attempt < 25 && !free; attempt++)
                    {
                        lx = UnityEngine.Random.Range(minX + r, maxX - r); lz = UnityEngine.Random.Range(minZ + r, maxZ - r);
                        if (maxX - minX < 2f * r) lx = (minX + maxX) / 2f;
                        if (maxZ - minZ < 2f * r) lz = (minZ + maxZ) / 2f;
                        free = true;
                        for (int k = 0; k < placedPos.Count && free; k++)
                        {
                            float dx = placedPos[k].x - lx, dz = placedPos[k].z - lz;
                            if (dx * dx + dz * dz < (placedR[k] + r) * (placedR[k] + r)) free = false;
                        }
                    }
                    if (!free)
                    {
                        // no free spot: put it on top of whatever is there
                        for (int k = 0; k < placedPos.Count; k++)
                        {
                            float dx = placedPos[k].x - lx, dz = placedPos[k].z - lz;
                            if (dx * dx + dz * dz < (placedR[k] + r) * (placedR[k] + r)) baseY = Mathf.Max(baseY, placedTop[k]);
                        }
                    }
                    if (baseY < 0f) baseY = FloorAt(car, t, lx, lz, floorY, ceilY);
                    if (baseY + h > ceilY) { Plugin.Verbose("Cargo: bed full after " + placed + " of " + wanted + " items"); UnityEngine.Object.Destroy(go); full = true; break; }
                    // move the renderer bottom-centre onto (lx, baseY, lz)
                    var pivotWorld = go.transform.position;
                    var bottomWorld = new Vector3(b.center.x, b.min.y, b.center.z);
                    var pivotOffsetLocal = t.InverseTransformVector(pivotWorld - bottomWorld);
                    go.transform.position = t.TransformPoint(new Vector3(lx, baseY + 0.01f, lz) + pivotOffsetLocal);
                    placedPos.Add(new Vector3(lx, baseY, lz)); placedR.Add(r); placedTop.Add(baseY + h + 0.01f);

                    Register.Name(go, slot.Prefab.name); Register.Add(go, false);
                    Lock(go, t);
                    items.Add(go);
                    if (slot.Condition >= 0f) _forcedCondition[go] = slot.Condition;
                    placed++;
                }
            }
            Plugin.Verbose("Cargo: " + placed + " item(s) loaded into " + car.name);
        }

        private static bool IsCanLike(string prefab)
        {
            string n = prefab.ToLowerInvariant();
            return n.Contains("can") || n.Contains("bottle") || n.Contains("canister") || n.Contains("jerry") || n.Contains("barrel");
        }

        // the real floor under (lx, lz): highest car collider hit below the zone ceiling, else the zone bottom
        private static float FloorAt(GameObject car, Transform t, float lx, float lz, float floorY, float ceilY)
        {
            var from = t.TransformPoint(new Vector3(lx, ceilY - 0.05f, lz));
            float best = -1f;
            foreach (var h in Physics.RaycastAll(from, -t.up, ceilY - floorY + 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || !h.collider.transform.IsChildOf(car.transform)) continue;
                float ly = t.InverseTransformPoint(h.point).y;
                if (ly < floorY - 0.3f) continue;
                if (best < 0f || ly > best) best = ly;
            }
            return best >= 0f ? best : floorY;
        }

        // what the item's own LockPhysics FSM would do 3 s later: parent to the PhysicsLock object, no Rigidbody
        private static void Lock(GameObject go, Transform lockGo)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
            go.transform.SetParent(lockGo, true);
        }

        // after the FSMs have started: park the LockPhysics FSMs in "off" so they do not redo the lock (and warn about a missing Rigidbody)
        // after Patrol.SetPartConditions (which rolls every Condition FSM under the car, the bed included): "#N" items get their condition back
        internal static void ApplyForcedConditions(List<GameObject> items)
        {
            if (items == null) return;
            foreach (var go in items)
            {
                float c;
                if (go == null || !_forcedCondition.TryGetValue(go, out c)) continue;
                _forcedCondition.Remove(go);
                Explode.SetCondition(go.transform, c);
                Plugin.Verbose("Cargo: " + go.name + " condition " + c.ToString("0") + " %");
            }
            _expired.Clear();
            foreach (var k in _forcedCondition.Keys) if (k == null) _expired.Add(k);
            foreach (var k in _expired) _forcedCondition.Remove(k);
        }
        private static readonly List<GameObject> _expired = new List<GameObject>();

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

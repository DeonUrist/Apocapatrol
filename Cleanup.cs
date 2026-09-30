using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // Removes raider cars the player has left behind, so they do not pile up in the world and in the save. The game itself only
    // deletes cars beyond 5000 m (DeleteFarAwayItems); everything closer stays forever. A car is eligible when the player never sat
    // in it (loot you took or a car you drove is never touched) and it is farther than [Cleanup] MinDistance. It goes once it has been
    // that far for RemoveAfterMinutes, or right away (farthest first) while there are more than MaxCars raider cars in the world.
    // Removal takes the car, its crew, its parts and its cargo out of the game's registration lists as well.
    internal class Cleanup : MonoBehaviour
    {
        private const float Interval = 5f;
        private float _nextTick, _nextEnteredCheck;
        private Patrol _patrol;

        private void Update()
        {
            if (_patrol == null) _patrol = GetComponent<Patrol>();
            if (_patrol == null || !_patrol.InGame()) return;

            // a raider car the player sits in is theirs from now on
            if (Time.unscaledTime >= _nextEnteredCheck)
            {
                _nextEnteredCheck = Time.unscaledTime + 1f;
                var pc = PlayerRef.PlayerCar;
                var m = pc != null ? pc.GetComponent<PatrolMarker>() : null;
                if (m != null && !m.PlayerEntered) { m.PlayerEntered = true; Plugin.Verbose("Cleanup: " + pc.name + " is the player's now, never removed"); }
            }

            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + Interval;
            if (!Plugin.CleanupEnabled.Value) return;
            var player = PlayerRef.Player;
            if (player == null) return;

            var all = PatrolMarker.All.ToArray();   // copy: Remove() destroys cars, which edits the list
            float minD = Plugin.CleanupDistance.Value;
            float limit = Plugin.CleanupMinutes.Value * 60f;
            var eligible = new List<KeyValuePair<float, PatrolMarker>>();
            int alive = 0;
            foreach (var m in all)
            {
                if (m == null) continue;
                alive++;
                if (m.PlayerEntered) { m.FarSeconds = 0f; continue; }
                float d = Vector3.Distance(m.transform.position, player.position);
                if (d < minD) { m.FarSeconds = 0f; continue; }
                m.FarSeconds += Interval;
                eligible.Add(new KeyValuePair<float, PatrolMarker>(d, m));
            }
            if (eligible.Count == 0) return;

            // timer
            for (int i = eligible.Count - 1; i >= 0; i--)
            {
                var m = eligible[i].Value;
                if (limit > 0f && m.FarSeconds >= limit)
                {
                    Remove(m, "left behind for " + (m.FarSeconds / 60f).ToString("0") + " min");
                    eligible.RemoveAt(i);
                    alive--;
                }
            }
            // cap: farthest first
            int max = Plugin.CleanupMaxCars.Value;
            if (max <= 0 || alive <= max) return;
            eligible.Sort((a, b) => b.Key.CompareTo(a.Key));
            foreach (var kv in eligible)
            {
                if (alive <= max) break;
                Remove(kv.Value, "more than " + max + " raider cars, " + kv.Key.ToString("0") + " m away");
                alive--;
            }
        }

        private static void Remove(PatrolMarker m, string why)
        {
            var car = m.gameObject;
            Plugin.Verbose("Cleanup: removed " + car.name + " (" + why + ")");
            Register.RemoveTree(car);
            Destroy(car);
        }
    }
}

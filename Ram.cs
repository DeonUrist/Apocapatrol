using System;
using System.Collections.Generic;
using UnityEngine;

namespace Apocapatrol
{
    // Ram damage: the game's own CarAttack bumper trigger only hurts creatures, and the player's car's CrashDamage FSM scales
    // with the player's own speed - a parked player is never hurt by an AI car. This applies damage the vanilla way instead:
    // Player [Bodypart].Damage = -amount (damage values are negative in this game: BumperDamage -3, speedDamage -0.5) and the
    // event "Damage" on that FSM (armor first, then health). The hurt sound, camera kick and red blood splash live in the
    // DamageEffectSound / DamageEffectSound_InCar FSMs (one of them enabled, toggled by InCar); Bodypart's own SendEvent to them
    // is disabled in the prefab - every attacker sends "Damage" to the effect FSM itself (FallDamage does), so we do too.
    internal static class Ram
    {
        // sits on every AI car root (next to PatrolMarker); the Rigidbody is there, so every collision of the car arrives here
        internal sealed class Sensor : MonoBehaviour
        {
            private void OnCollisionEnter(Collision col)
            {
                try
                {
                    if (col.collider == null) return;
                    var p = PlayerRef.Player;                                   // refresh the cached player first
                    int kind = p == null ? 0 : PlayerRef.Kind(col.collider.transform);
                    if (kind == 0) return;
                    var mk = GetComponent<PatrolMarker>();
                    Hit(gameObject, mk != null ? mk.BodyPrefab : null, kind, col.relativeVelocity.magnitude, col.collider);
                }
                catch (Exception e) { Plugin.Log.LogError("Ram: " + e); }
            }
        }

        internal const float DefaultDamage = 30f;
        private static readonly Dictionary<int, float> _next = new Dictionary<int, float>();   // car instance id -> next allowed hit time
        private static float _pending, _pendingAt = -1f, _healthBefore;
        private static string _pendingCar;

        // kind: 1 = the player on foot, 2 = the player's car. rel = relative impact speed (m/s).
        internal static void Hit(GameObject car, string body, int kind, float rel, Collider hit)
        {
            if (!Plugin.RamDamage.Value || car == null || kind == 0) return;
            float kmh = rel * 3.6f;
            if (kmh < Plugin.RamMinSpeedKmh.Value) return;
            int id = car.GetInstanceID();
            float next;
            if (_next.TryGetValue(id, out next) && Time.time < next) return;       // one hit per second per car (many colliders touch at once)
            _next[id] = Time.time + 1f;

            float full = Mathf.Max(0f, Plugin.RamFullSpeedKmh.Value);
            float speedFactor = full <= 0f ? 1f : Mathf.Clamp01(kmh / full);
            float amount = BaseDamage(body) * Plugin.RamDamageMultiplier.Value * speedFactor;
            if (kind == 2) amount *= Plugin.RamInCarFactor.Value;
            amount = Mathf.Round(amount);
            if (amount < 1f) { Plugin.Verbose("Ram: " + car.name + " hit the player at " + kmh.ToString("0") + " km/h - too slow/weak for damage"); return; }

            var player = PlayerRef.Player;
            if (player == null) return;
            PlayMakerFSM bodypart = null, health = null;
            var effects = new List<PlayMakerFSM>();
            foreach (var f in player.GetComponents<PlayMakerFSM>())
            {
                if (f.FsmName == "Bodypart") bodypart = f;
                else if (f.FsmName == "Health") health = f;
                else if (f.FsmName == "DamageEffectSound" || f.FsmName == "DamageEffectSound_InCar") effects.Add(f);
            }
            float before = -1f;
            if (health != null && health.Fsm.Initialized) { var h = health.FsmVariables.GetFsmFloat("Health"); if (h != null) before = h.Value; }

            bool applied = false;
            if (bodypart != null && bodypart.Fsm.Initialized)
            {
                var d = bodypart.FsmVariables.GetFsmFloat("Damage");
                if (d != null) { d.Value = -amount; bodypart.SendEvent("Damage"); applied = true; }
            }
            if (!applied && health != null && health.Fsm.Initialized)
            {
                var h = health.FsmVariables.GetFsmFloat("Health");
                if (h != null) { h.Value -= amount; applied = true; }
            }
            if (applied)
                foreach (var f in effects)
                    if (f.enabled && f.Fsm.Initialized) f.SendEvent("Damage");   // the disabled one ignores it
            Plugin.Log.LogInfo("Ram: " + car.name + " (" + (body ?? "?") + ") hit the player " + (kind == 2 ? "in their car" : "on foot")
                + " at " + kmh.ToString("0") + " km/h -> " + amount + " damage" + (applied ? "" : " (no Player FSM found!)")
                + (before >= 0f ? ", health " + before.ToString("0") : "") + (hit != null ? ", collider " + hit.name + " layer " + LayerMask.LayerToName(hit.gameObject.layer) : ""));
            if (applied && before >= 0f) { _pending = amount; _pendingAt = Time.time + 0.5f; _healthBefore = before; _pendingCar = car.name; }
        }

        // Called from the runner: checks half a second after a hit that the health really went down (sign / armor sanity, verbose only).
        internal static void Tick()
        {
            if (_pendingAt < 0f || Time.time < _pendingAt) return;
            _pendingAt = -1f;
            var player = PlayerRef.Player;
            if (player == null) return;
            foreach (var f in player.GetComponents<PlayMakerFSM>())
                if (f.FsmName == "Health" && f.Fsm.Initialized)
                {
                    var h = f.FsmVariables.GetFsmFloat("Health");
                    if (h == null) return;
                    float delta = h.Value - _healthBefore;
                    if (delta > 0f) Plugin.Log.LogWarning("Ram: player health went UP after " + _pendingCar + "'s hit (" + _healthBefore.ToString("0") + " -> " + h.Value.ToString("0") + ") - damage sign wrong?");
                    else Plugin.Verbose("Ram: player health " + _healthBefore.ToString("0") + " -> " + h.Value.ToString("0") + " (" + _pending + " sent)");
                    return;
                }
        }

        // "Junker=50, Rust*=70" -> full-speed damage for this body; unlisted bodies = DefaultDamage.
        internal static float BaseDamage(string body)
        {
            string b = (body ?? "").Trim();
            string table = Plugin.RamDamageByBody.Value ?? "";
            float best = -1f; int bestLen = -1;
            foreach (var raw in table.Split(',', ';'))
            {
                int eq = raw.IndexOf('=');
                if (eq < 0) continue;
                string key = raw.Substring(0, eq).Trim(); float val;
                if (key.Length == 0 || !float.TryParse(raw.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val)) continue;
                bool match = key.EndsWith("*") ? b.StartsWith(key.Substring(0, key.Length - 1), StringComparison.OrdinalIgnoreCase)
                                                : string.Equals(key, b, StringComparison.OrdinalIgnoreCase);
                if (match && key.Length > bestLen) { best = val; bestLen = key.Length; }   // the most specific key wins
            }
            return best >= 0f ? best : DefaultDamage;
        }
    }
}

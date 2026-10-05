using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class PickupItem
    {
        internal string Id, Name;
    }
    internal static class PickupCatalog
    {
        private static List<PickupItem> _items;
        internal static void Invalidate() { _items = null; }
        internal static List<PickupItem> All()
        {
            if (_items != null) return _items;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(Paths.ConfigPath, "com.denis.apocalypter.apocaspawner.cfg");
            try
            {
                bool section = false;
                if (File.Exists(path)) foreach (string line in File.ReadAllLines(path))
                {
                    string s = line.Trim(); if (s.StartsWith("[")) { section = s == "[Names]"; continue; }
                    int eq = s.IndexOf('='); if (section && eq > 0 && !s.StartsWith("#")) names[s.Substring(0, eq).Trim()] = s.Substring(eq + 1).Trim();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Item names: " + e.Message); }
            var items = new Dictionary<string, PickupItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || fsm.FsmName != "ID") continue;
                var go = fsm.gameObject;
                if (go.scene.IsValid() || go.transform.parent != null || go.GetComponent<Rigidbody>() == null) continue;
                var fsms = go.GetComponents<PlayMakerFSM>();
                if (fsms.Any(f => f.FsmName == "RpmGear" || f.FsmName == "CrashDamage" || f.FsmName == "TrailerAttached")) continue;
                string id = fsm.FsmVariables.GetFsmString("ID") != null ? fsm.FsmVariables.GetFsmString("ID").Value : "";
                if (id == "PartAdjusterTools" || id == "grenadeexplode" || id == "blastlanceexplode" || go.name.StartsWith("toolset_", StringComparison.OrdinalIgnoreCase)) continue;
                string name;
                if (!names.TryGetValue(go.name, out name) || string.IsNullOrEmpty(name))
                {
                    var label = fsms.FirstOrDefault(f => f.FsmName == "ItemName");
                    var value = label != null ? label.FsmVariables.GetFsmString("ItemName") : null;
                    name = value != null && !string.IsNullOrEmpty(value.Value) ? value.Value : go.name.Replace('_', ' ');
                }
                items[go.name] = new PickupItem { Id = go.name, Name = name };
            }
            _items = items.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id).ToList(); return _items;
        }
        internal static string Name(string id) { var item = _items != null ? _items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) : null; return item != null ? item.Name : id.Replace('_', ' '); }
    }
}

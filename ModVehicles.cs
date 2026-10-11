using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Apocapatrol
{
    // Vehicles and trailers added by other mods (War Rig, ...) are runtime templates, not asset prefabs, so the asset
    // scans don't see them. Convention: the mod has a type "<Namespace>.Factory" with `public const string Name` (the prefab
    // name its instances are cloned as, e.g. "WarRig") and `public static GameObject Template()`.
    internal static class ModVehicles
    {
        private static Dictionary<string, MethodInfo> _byName;
        private static int _assemblyCount;

        internal static GameObject Template(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_byName == null || _assemblyCount != AppDomain.CurrentDomain.GetAssemblies().Length) Scan();
            MethodInfo m;
            if (!_byName.TryGetValue(name.Trim(), out m)) return null;
            try { return m.Invoke(null, null) as GameObject; }
            catch (Exception e) { Plugin.Log.LogWarning("Mod vehicle " + name + ": " + (e.InnerException ?? e).Message); return null; }
        }

        private static void Scan()
        {
            _byName = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            _assemblyCount = assemblies.Length;
            foreach (var asm in assemblies)
            {
                string an = asm.GetName().Name;
                if (an == "Apocapatrol" || an == "MotorcycleMod" || an.StartsWith("Unity") || an.StartsWith("System") || an == "mscorlib" || an == "Assembly-CSharp") continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; } catch { continue; }
                foreach (var t in types)
                {
                    if (t == null || t.Name != "Factory") continue;
                    var nameField = t.GetField("Name", BindingFlags.Public | BindingFlags.Static);
                    var template = t.GetMethod("Template", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (nameField == null || template == null || template.ReturnType != typeof(GameObject)) continue;
                    var n = nameField.GetValue(null) as string;
                    if (string.IsNullOrEmpty(n) || _byName.ContainsKey(n)) continue;
                    _byName[n] = template;
                    Plugin.Log.LogInfo("Mod vehicle available for templates: " + n + " (" + an + ")");
                }
            }
        }
    }
}

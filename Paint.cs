using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Apocapatrol
{
    // Custom paint for the mod's own cars: BepInEx/plugins/Apocapatrol/Textures/<texture name>.png replaces the frame texture of that name
    // on every car the mod spawns (and on its saved cars after a load). Textures/junker.png = the Junker body's "junker" texture.
    // The file must follow the original texture's layout; its alpha is kept (the body shader cuts out pixels below 50 % alpha = rust holes).
    // One replacement material per original material, shared by every mod car that uses it; vanilla cars keep the original.
    internal static class Paint
    {
        internal const string Folder = "Textures";
        private static string _dir;
        private static readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);   // null = no file
        private static readonly Dictionary<int, Material> _mat = new Dictionary<int, Material>();   // original material id -> painted copy

        internal static void Init(string pluginDll)
        {
            _dir = Path.Combine(Path.GetDirectoryName(pluginDll) ?? ".", Folder);   // called early in Awake: no logging here
        }

        internal static void Apply(GameObject car)
        {
            if (car == null || _dir == null || !Directory.Exists(_dir)) return;
            int n = 0;
            try
            {
                foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.GetType().Name == "ParticleSystemRenderer" || !OnFrame(car, r.transform)) continue;
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || !m.HasProperty("_MainTex")) continue;
                        var painted = Painted(m);
                        if (painted != null && painted != m) { mats[i] = painted; changed = true; n++; }
                    }
                    if (changed) r.sharedMaterials = mats;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Paint: " + e.Message); }
            if (n > 0) Plugin.Verbose("Paint: " + car.name + " repainted (" + n + " material(s))");
        }

        // Cargo trucks: the container (texture shipping_container_6m_1 - not its doors _2/_3, not the cab) wears a texture by its load:
        // Textures/cargo_<load>.png (water, gasoline, diesel, medicine, weapons, drugs, mechanic), cargo_food.png for dog food, rats and
        // corpses, cargo.png for an empty truck or any other load, and cargo.png again when the specific file is missing.
        internal const string ContainerTexture = "shipping_container_6m_1";
        private static readonly Dictionary<string, Material> _cargoMat = new Dictionary<string, Material>();   // "<orig mat id>|<file>" -> copy
        private static readonly Dictionary<Material, Material> _cargoOrig = new Dictionary<Material, Material>();   // copy -> original

        internal static string CargoFile(string lootKey)
        {
            string k = (lootKey ?? "").Trim().ToLowerInvariant();
            switch (k)
            {
                case "food": case "rats": case "corpses": return "cargo_food";
                case "water": case "gasoline": case "diesel": case "medicine": case "weapons": case "drugs": case "mechanic": return "cargo_" + k;
                default: return "cargo";
            }
        }

        internal static void ApplyCargo(GameObject car, string lootKey)
        {
            if (car == null || _dir == null || !Directory.Exists(_dir)) return;
            try
            {
                string file = CargoFile(lootKey);
                var tex = Load(file);
                if (tex == null && file != "cargo") { file = "cargo"; tex = Load(file); }
                if (tex == null) return;
                int n = 0;
                foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.GetType().Name == "ParticleSystemRenderer" || !OnFrame(car, r.transform)) continue;
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        Material orig;
                        if (!_cargoOrig.TryGetValue(m, out orig))
                        {
                            if (!m.HasProperty("_MainTex") || m.mainTexture == null || m.mainTexture.name != ContainerTexture) continue;
                            orig = m;
                        }
                        string key = orig.GetInstanceID() + "|" + file;
                        Material copy;
                        if (!_cargoMat.TryGetValue(key, out copy) || copy == null)
                        {
                            copy = new Material(orig) { name = orig.name + " (Apocapatrol " + file + ")" };
                            copy.mainTexture = tex;
                            _cargoMat[key] = copy; _cargoOrig[copy] = orig;
                        }
                        if (mats[i] != copy) { mats[i] = copy; changed = true; n++; }
                    }
                    if (changed) r.sharedMaterials = mats;
                }
                if (n > 0) Plugin.Verbose("Paint: " + car.name + " container = " + file + " (" + (string.IsNullOrEmpty(lootKey) ? "no cargo" : lootKey) + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Paint: cargo: " + e.Message); }
        }

        // the frame's own renderers: not the attached parts, occupants, carcasses or cargo
        private static bool OnFrame(GameObject car, Transform t)
        {
            for (var a = t; a != null && a != car.transform; a = a.parent)
                if (a.CompareTag("vehPart") || a.GetComponent<Crew>() != null || a.name.IndexOf("(Driver)", StringComparison.Ordinal) >= 0
                    || a.name.IndexOf("(Passenger)", StringComparison.Ordinal) >= 0 || a.name.IndexOf("_Dead", StringComparison.Ordinal) >= 0
                    || a.name == "PhysicsLock") return false;
            return true;
        }

        private static Material Painted(Material m)
        {
            Material cached;
            int id = m.GetInstanceID();
            if (_mat.TryGetValue(id, out cached)) return cached;
            foreach (var kv in _mat) if (kv.Value == m) return m;   // already one of ours
            var main = m.mainTexture;
            var tex = main != null ? Load(main.name) : null;
            Material result = null;
            if (tex != null)
            {
                result = new Material(m) { name = m.name + " (Apocapatrol paint)" };
                result.mainTexture = tex;
            }
            _mat[id] = result;
            return result;
        }

        private static Texture2D Load(string name)
        {
            Texture2D t;
            if (string.IsNullOrEmpty(name)) return null;
            if (_tex.TryGetValue(name, out t)) return t;
            t = null;
            var path = Path.Combine(_dir, name + ".png");
            if (File.Exists(path))
            {
                try
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name + " (Apocapatrol)" };
                    if (ImageConversion.LoadImage(t, File.ReadAllBytes(path), true))
                    {
                        t.wrapMode = TextureWrapMode.Repeat; t.filterMode = FilterMode.Trilinear; t.anisoLevel = 4;
                        Plugin.Verbose("Paint: loaded " + path + " (" + t.width + "x" + t.height + ")");
                    }
                    else { Plugin.Log.LogWarning("Paint: could not decode " + path); UnityEngine.Object.Destroy(t); t = null; }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Paint: " + path + ": " + e.Message); t = null; }
            }
            _tex[name] = t;
            return t;
        }
    }
}

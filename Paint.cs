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

        // Cargo trucks: the container's two long sides show its load. The game's container mesh maps its sides, top and ends onto the same
        // strip of shipping_container_6m_1 (and it is not readable at runtime), so instead of re-texturing it the mod lays one thin panel over
        // each outer side (3 mm out, no collider, no shadow) with its own material: Textures/cargo_<load>.png as ONE upright side picture -
        // left to right along the truck as seen from outside (both sides read the same way), bottom to top = container floor to roof,
        // about 2:1 (2048 x 1024). cargo_food.png for food, rats and corpses; cargo_<load>.png for water, gasoline, diesel, medicine,
        // weapons, drugs, mechanic; cargo.png for an empty truck or any other load, and as the fallback. The container itself (roof,
        // ends) and the rear doors keep the game's texture. Transparent pixels show the container underneath (cutout shader).
        // Inside: Textures/cargo_inside.png (any load) on the inner walls, floor, roof and the closed front end, tiled once per
        // InsideTileMeters (a square texture = 2 m x 2 m of surface); without the file the inside keeps the game's texture.
        internal const string ContainerTexture = "shipping_container_6m_1";
        private const string PanelName = "ApocapatrolCargoSide";
        private const float PanelOffset = 0.003f;
        private const string InsideFile = "cargo_inside";
        private const float InsideTileMeters = 2f;
        // the inner box of the game's container mesh (read from the asset): walls, floor and roof 0.069 m inside the outer box, the closed
        // end (local +z, the cab side) 0.066 m in, the door end open
        private const float WallInset = 0.069f, EndInset = 0.066f;
        private static readonly Dictionary<string, Material> _cargoMat = new Dictionary<string, Material>();   // "<container mat id>|<file>"

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
                foreach (var r in car.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null || r.name.StartsWith(PanelName, StringComparison.Ordinal) || !OnFrame(car, r.transform)) continue;
                    Material container = null;
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m.HasProperty("_MainTex") && m.mainTexture != null && m.mainTexture.name == ContainerTexture) { container = m; break; }
                    if (container == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mat = CargoMaterial(container, file, tex);
                    var inside = Load(InsideFile);
                    var insideMat = inside != null ? CargoMaterial(container, InsideFile, inside) : null;
                    foreach (var spec in Panels(mf.sharedMesh, r.transform, car.transform))
                    {
                        bool inner = spec.Inside;
                        if (inner && insideMat == null) continue;
                        var panel = Panel(r, spec);
                        panel.sharedMaterial = inner ? insideMat : mat;
                        n++;
                    }
                }
                if (n > 0) Plugin.Verbose("Paint: " + car.name + " container sides = " + file + " (" + (string.IsNullOrEmpty(lootKey) ? "no cargo" : lootKey) + "), "
                    + n + " panel(s)" + (Load(InsideFile) != null ? ", inside = " + InsideFile : ""));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Paint: cargo: " + e.Message); }
        }

        private static Material CargoMaterial(Material container, string file, Texture2D tex)
        {
            string key = container.GetInstanceID() + "|" + file;
            Material m;
            if (_cargoMat.TryGetValue(key, out m) && m != null) return m;
            m = new Material(container) { name = container.name + " (Apocapatrol " + file + ")" };
            m.mainTexture = tex;
            m.mainTextureScale = Vector2.one; m.mainTextureOffset = Vector2.zero;
            _cargoMat[key] = m;
            return m;
        }

        private sealed class PanelSpec
        {
            internal string Name; internal bool Inside;
            internal Vector3 Center, Right, Up, Normal;   // container-local; Right/Up = the viewer's right/up looking at the face, Normal toward the viewer
            internal float HalfW, HalfH; internal Vector2 UvScale;
            internal Mesh Mesh;
        }
        private static readonly Dictionary<int, List<PanelSpec>> _panels = new Dictionary<int, List<PanelSpec>>();   // mesh id -> panels

        private static MeshRenderer Panel(MeshRenderer container, PanelSpec spec)
        {
            var t = container.transform.Find(spec.Name);
            if (t == null)
            {
                var go = new GameObject(spec.Name);
                go.layer = container.gameObject.layer;
                t = go.transform;
                t.SetParent(container.transform, false);
                t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; t.localScale = Vector3.one;
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = true;
            }
            t.GetComponent<MeshFilter>().sharedMesh = spec.Mesh;
            return t.GetComponent<MeshRenderer>();
        }

        // outer long sides (picture 0..1) + inside walls, floor, roof and closed end (tiled), built once per container mesh
        private static List<PanelSpec> Panels(Mesh mesh, Transform container, Transform car)
        {
            List<PanelSpec> list;
            int id = mesh.GetInstanceID();
            if (_panels.TryGetValue(id, out list)) return list;
            list = new List<PanelSpec>();
            var b = mesh.bounds;   // available even though the game's mesh is not readable
            var right = Axis(container.InverseTransformDirection(car.right));
            var up = Axis(container.InverseTransformDirection(car.up));
            var len = Vector3.Cross(right, up); len = new Vector3(Mathf.Abs(len.x), Mathf.Abs(len.y), Mathf.Abs(len.z));   // + along the length axis
            var c = b.center;
            float eS = Along(b.extents, right), eU = Along(b.extents, up), eL = Along(b.extents, len);
            float cS = Vector3.Dot(c, right), cU = Vector3.Dot(c, up), cL = Vector3.Dot(c, len);
            Func<float, float, float, Vector3> P = (s, u, l) => right * s + up * u + len * l;
            // outer sides
            for (int side = -1; side <= 1; side += 2)
            {
                var n = right * side;
                list.Add(new PanelSpec
                {
                    Name = PanelName + (side > 0 ? "_R" : "_L"), Inside = false,
                    Center = P(cS + side * (eS + PanelOffset), cU, cL), Normal = n, Up = up, Right = Vector3.Cross(up, -n),
                    HalfW = eL, HalfH = eU, UvScale = Vector2.one
                });
            }
            // inside: the inner box
            float iS = eS - WallInset, iBot = cU - eU + WallInset, iTop = cU + eU - WallInset;
            float lOpen = cL - eL, lClosed = cL + eL - EndInset;               // the closed end is at local +length (the cab side)
            float iCU = (iBot + iTop) * 0.5f, iHU = (iTop - iBot) * 0.5f, iCL = (lOpen + lClosed) * 0.5f, iHL = (lClosed - lOpen) * 0.5f;
            float tile = 1f / InsideTileMeters;
            for (int side = -1; side <= 1; side += 2)
            {
                var n = -right * side;                                         // facing into the box
                list.Add(new PanelSpec
                {
                    Name = PanelName + "Inside" + (side > 0 ? "_R" : "_L"), Inside = true,
                    Center = P(side * (iS - PanelOffset) + cS, iCU, iCL), Normal = n, Up = up, Right = Vector3.Cross(up, -n),
                    HalfW = iHL, HalfH = iHU, UvScale = new Vector2(2f * iHL * tile, 2f * iHU * tile)
                });
            }
            list.Add(new PanelSpec
            {
                Name = PanelName + "Inside_Floor", Inside = true,
                Center = P(cS, iBot + PanelOffset, iCL), Normal = up, Up = len, Right = Vector3.Cross(len, -up),
                HalfW = iS, HalfH = iHL, UvScale = new Vector2(2f * iS * tile, 2f * iHL * tile)
            });
            list.Add(new PanelSpec
            {
                Name = PanelName + "Inside_Roof", Inside = true,
                Center = P(cS, iTop - PanelOffset, iCL), Normal = -up, Up = len, Right = Vector3.Cross(len, up),
                HalfW = iS, HalfH = iHL, UvScale = new Vector2(2f * iS * tile, 2f * iHL * tile)
            });
            list.Add(new PanelSpec
            {
                Name = PanelName + "Inside_End", Inside = true,
                Center = P(cS, iCU, lClosed - PanelOffset), Normal = -len, Up = up, Right = Vector3.Cross(up, len),
                HalfW = iS, HalfH = iHU, UvScale = new Vector2(2f * iS * tile, 2f * iHU * tile)
            });
            foreach (var spec in list) spec.Mesh = Quad(spec);
            _panels[id] = list;
            return list;
        }

        private static Mesh Quad(PanelSpec q)
        {
            var r = q.Right.normalized * q.HalfW; var u = q.Up.normalized * q.HalfH;
            var m = new Mesh { name = q.Name };
            m.vertices = new[] { q.Center - r - u, q.Center + r - u, q.Center + r + u, q.Center - r + u };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(q.UvScale.x, 0f), new Vector2(q.UvScale.x, q.UvScale.y), new Vector2(0f, q.UvScale.y) };
            m.normals = new[] { q.Normal, q.Normal, q.Normal, q.Normal };
            m.triangles = new[] { 0, 3, 2, 0, 2, 1 };   // clockwise seen from the viewer (Normal side) = front face in Unity
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

        private static Vector3 Axis(Vector3 local)
        {
            float ax = Mathf.Abs(local.x), ay = Mathf.Abs(local.y), az = Mathf.Abs(local.z);
            if (ax >= ay && ax >= az) return new Vector3(Mathf.Sign(local.x), 0f, 0f);
            if (ay >= az) return new Vector3(0f, Mathf.Sign(local.y), 0f);
            return new Vector3(0f, 0f, Mathf.Sign(local.z));
        }

        private static float Along(Vector3 v, Vector3 axis) { return Mathf.Abs(Vector3.Dot(v, axis)); }

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

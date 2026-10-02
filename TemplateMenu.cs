using System;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Apocapatrol
{
    // F8: a window listing the park; clicking a template builds that car in front of the player.
    // Uses Apocasetter's theme and input blocker when it is installed (resolved by reflection, no hard dependency);
    // otherwise a plain IMGUI window with timeScale 0 and a freed cursor.
    internal class TemplateMenu : MonoBehaviour
    {
        private bool _open;
        private Vector2 _scroll;
        private float _prevTimeScale = 1f;
        private CursorLockMode _prevLock;
        private bool _prevVisible;
        private Rect _rect;
        private Patrol _patrol;   // sibling on the runner (was a GetComponent every frame)

        // Apocasetter, if present
        private static bool _resolved, _hasSetter;
        private static MethodInfo _themeApply, _blockerSet;
        private static FieldInfo _themeHeader;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                if (!Chainloader.PluginInfos.ContainsKey("com.denis.apocalypter.apocasetter")) return;
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "Apocasetter") { asm = a; break; }
                if (asm == null) return;
                var theme = asm.GetType("Apocasetter.Theme");
                var blocker = asm.GetType("Apocasetter.InputBlocker");
                if (theme == null || blocker == null) return;
                _themeApply = theme.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public);
                _themeHeader = theme.GetField("Header", BindingFlags.Static | BindingFlags.Public);
                _blockerSet = blocker.GetMethod("Set", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                _hasSetter = _themeApply != null && _blockerSet != null;
                Plugin.Verbose("Template menu: Apocasetter theme/input blocker " + (_hasSetter ? "found" : "incomplete"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Template menu: Apocasetter lookup failed: " + e.Message); }
        }

        internal bool IsOpen { get { return _open; } }

        private void Update()
        {
            if (_open)
            {
                if (Plugin.Pressed(Plugin.MenuKey.Value) || Plugin.Pressed(UnityEngine.InputSystem.Key.Escape)) Close();
                return;
            }
            if (Plugin.MenuKey.Value == UnityEngine.InputSystem.Key.None) return;
            if (_patrol == null) _patrol = GetComponent<Patrol>();
            if (_patrol == null || !_patrol.InGame()) return;
            if (Plugin.Pressed(Plugin.MenuKey.Value)) Open();
        }

        private void LateUpdate()
        {
            if (!_open) return;
            // the game re-locks the cursor every frame (and the input blocker does not free it); keep it free while the window is up
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        private void Open()
        {
            Resolve();
            CarTemplates.Refresh(false);   // a template dumped a moment ago (Apocatemplater) is in the list now
            _open = true;
            _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
            if (_hasSetter) { try { _blockerSet.Invoke(null, new object[] { true }); } catch (Exception e) { Plugin.Log.LogWarning("InputBlocker: " + e.Message); } }
            else
            {
                _prevTimeScale = Time.timeScale;
                Time.timeScale = 0f;
            }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            int cars = 0, trucks = 0;
            foreach (var t in CarTemplate.Park) { if (IsTruck(t)) trucks++; else cars++; }
            float w = 640f, h = Mathf.Min(Screen.height - 80f, 200f + Mathf.Max(cars, trucks) * 32f);
            _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            if (_hasSetter) { try { _blockerSet.Invoke(null, new object[] { false }); } catch (Exception e) { Plugin.Log.LogWarning("InputBlocker: " + e.Message); } }
            else if (Time.timeScale == 0f) Time.timeScale = _prevTimeScale > 0f ? _prevTimeScale : 1f;
            Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
        }

        private void OnDestroy() { Close(); }

        private void OnGUI()
        {
            if (!_open) return;
            if (_hasSetter) { try { _themeApply.Invoke(null, null); } catch (Exception) { } }
            GUI.depth = -10;
            _rect = GUI.Window(GetInstanceID(), _rect, DrawWindow, "");
        }

        private void DrawWindow(int id)
        {
            var header = _hasSetter && _themeHeader != null ? _themeHeader.GetValue(null) as GUIStyle : null;
            GUILayout.BeginVertical();
            GUILayout.Label("Template spawner", header ?? GUI.skin.label);
            GUILayout.Label("Click a car to build it " + Plugin.SpawnDistance.ToString("0") + " m in front of you.  [Esc] Close");
            GUILayout.Space(6f);
            var convoy = GetComponent<Convoy>();
            int debugSpawn = 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Enemy spawns" + (convoy != null ? " (heat " + (convoy.Heat * 100f).ToString("0") + " %, " + convoy.Km.ToString("0.0") + " km, " + convoy.Bosses + " bosses)" : ""), header ?? GUI.skin.label, GUILayout.Width(300f));
            if (GUILayout.Button("Enemy cars", GUILayout.Height(28f), GUILayout.Width(150f))) debugSpawn = 1;
            GUILayout.Space(6f);
            if (GUILayout.Button("Enemy patrol", GUILayout.Height(28f), GUILayout.Width(150f))) debugSpawn = 2;
            GUILayout.EndHorizontal();
            GUILayout.Label("A group " + Plugin.ConvoySpawnDistance.Value.ToString("0") + " m ahead of your car (behind you on foot), as if every distance/boss requirement were met.");
            GUILayout.Space(6f);
            _scroll = GUILayout.BeginScrollView(_scroll);
            CarTemplate chosen = null;
            GUILayout.BeginHorizontal();
            for (int col = 0; col < 2; col++)
            {
                GUILayout.BeginVertical(GUILayout.Width(300f));
                GUILayout.Label(col == 0 ? "Cars" : "Trucks", header ?? GUI.skin.label);
                foreach (var t in CarTemplate.Park)
                {
                    if (IsTruck(t) != (col == 1)) continue;
                    if (GUILayout.Button(t.Origin == "built-in" ? t.Name : t.Name + "  [" + t.Origin + "]", GUILayout.Height(28f))) chosen = t;
                    GUILayout.Space(2f);
                }
                GUILayout.EndVertical();
                if (col == 0) GUILayout.Space(12f);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            if (GUILayout.Button("Close")) Close();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
            if (chosen != null)
            {
                Close();                          // the build coroutine waits on scaled time; the blocker sets timeScale 0
                if (_patrol == null) _patrol = GetComponent<Patrol>();
                if (_patrol != null) _patrol.Spawn(chosen);
            }
            else if (debugSpawn != 0 && convoy != null)
            {
                Close();
                if (debugSpawn == 1) convoy.DebugCars(); else convoy.DebugConvoy();
            }
        }

        private static bool IsTruck(CarTemplate t) { return t.IsTruck; }

        private static string Summary(CarTemplate t)
        {
            return t.Body + "  ·  " + t.Engine + "  ·  " + (t.Driver.Length > 0 ? t.Driver : "no driver") + " / " + (t.Passenger.Length > 0 ? t.Passenger : "no passenger")
                + "  ·  rams " + t.Rams + (t.Cargo.Length > 0 ? "  ·  loot: " + t.Cargo : "");
        }
    }
}

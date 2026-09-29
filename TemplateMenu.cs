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
            var patrol = GetComponent<Patrol>();
            if (_open)
            {
                if (Plugin.Pressed(Plugin.MenuKey.Value) || Plugin.Pressed(UnityEngine.InputSystem.Key.Escape)) Close();
                return;
            }
            if (patrol == null || !patrol.InGame()) return;
            if (Plugin.Pressed(Plugin.MenuKey.Value)) Open();
        }

        private void LateUpdate()
        {
            if (!_open || _hasSetter) return;
            // the game re-locks the cursor every frame; keep it free while the window is up
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        private void Open()
        {
            Resolve();
            _open = true;
            if (_hasSetter) { try { _blockerSet.Invoke(null, new object[] { true }); } catch (Exception e) { Plugin.Log.LogWarning("InputBlocker: " + e.Message); } }
            else
            {
                _prevTimeScale = Time.timeScale; _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }
            float w = 520f, h = Mathf.Min(Screen.height - 80f, 120f + CarTemplate.Park.Length * 58f);
            _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
        }

        private void Close()
        {
            if (!_open) return;
            _open = false;
            if (_hasSetter) { try { _blockerSet.Invoke(null, new object[] { false }); } catch (Exception e) { Plugin.Log.LogWarning("InputBlocker: " + e.Message); } }
            else
            {
                if (Time.timeScale == 0f) Time.timeScale = _prevTimeScale > 0f ? _prevTimeScale : 1f;
                Cursor.lockState = _prevLock; Cursor.visible = _prevVisible;
            }
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
            _scroll = GUILayout.BeginScrollView(_scroll);
            CarTemplate chosen = null;
            foreach (var t in CarTemplate.Park)
            {
                if (GUILayout.Button(t.Name + "\n" + Summary(t), GUILayout.Height(52f))) chosen = t;
                GUILayout.Space(2f);
            }
            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            if (GUILayout.Button("Close")) Close();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
            if (chosen != null)
            {
                Close();                          // the build coroutine waits on scaled time; the blocker sets timeScale 0
                var patrol = GetComponent<Patrol>();
                if (patrol != null) patrol.Spawn(chosen);
            }
        }

        private static string Summary(CarTemplate t)
        {
            return t.Body + "  ·  " + t.Engine + "  ·  " + (t.Driver.Length > 0 ? t.Driver : "no driver") + " / " + (t.Passenger.Length > 0 ? t.Passenger : "no passenger")
                + "  ·  rams " + t.Rams + (t.Cargo.Length > 0 ? "  ·  loot: " + t.Cargo : "");
        }
    }
}

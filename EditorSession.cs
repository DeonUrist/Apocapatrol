using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed class EditorSession
    {
        private static EditorSession _owner;
        private static MethodInfo _block;
        private static FieldInfo _active;
        private static bool _resolved;
        private int _releaseFrame = -1;
        private float _time;
        private CursorLockMode _lock;
        private bool _visible, _blocked;
        internal bool Open { get; private set; }
        internal static bool Busy { get { return _owner != null; } }
        internal static int Generation { get; private set; }
        internal static void CancelAll() { Generation++; if (_owner != null) _owner.Close(true); }
        internal bool Acquire()
        {
            Resolve();
            if (Busy || Time.timeScale <= 0 || (_active != null && (bool)_active.GetValue(null))) return false;
            _owner = this; Open = true; _time = Time.timeScale; _lock = Cursor.lockState; _visible = Cursor.visible;
            if (_block != null) try { _block.Invoke(null, new object[] { true }); _blocked = true; } catch (Exception e) { Plugin.Log.LogWarning("Editor input blocker: " + e.Message); }
            Time.timeScale = 0; Maintain(); return true;
        }
        internal void Maintain()
        {
            if (!Open) return;
            Time.timeScale = 0; Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        internal void Close(bool immediate = false)
        {
            if (!Open && _releaseFrame < 0) return;
            Open = false;
            if (immediate) Release(); else _releaseFrame = Time.frameCount + 1;
        }
        internal static void Tick() { if (_owner != null && _owner._releaseFrame >= 0 && Time.frameCount >= _owner._releaseFrame) _owner.Release(); }
        private void Release()
        {
            if (_blocked && _block != null) try { _block.Invoke(null, new object[] { false }); } catch (Exception e) { Plugin.Log.LogWarning(e.Message); }
            _blocked = false; _releaseFrame = -1; Time.timeScale = _time; Cursor.lockState = _lock; Cursor.visible = _visible; _owner = null;
        }
        private static void Resolve()
        {
            if (_resolved) return; _resolved = true;
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Apocasetter");
            var type = asm != null ? asm.GetType("Apocasetter.InputBlocker") : null;
            if (type == null) return;
            _block = type.GetMethod("Set", BindingFlags.Static | BindingFlags.Public); _active = type.GetField("Active", BindingFlags.Static | BindingFlags.Public);
            var skin = asm.GetType("Apocasetter.GameSkin"); var font = skin != null ? skin.GetField("GameFont") : null;
            if (font != null) PatrolSkin.SetGameFont(font.GetValue(null) as Font);
        }
    }
}

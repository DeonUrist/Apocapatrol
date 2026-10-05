using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Apocapatrol
{
    internal static class EditorWidgets
    {
        internal static float Scale = 1;
        private static readonly Dictionary<string, string> _numbers = new Dictionary<string, string>();
        private static GUISkin _skin;
        private static GUIStyle _button;
        private static GUIStyle _row, _selectedRow, _group, _selectedGroup, _name, _sub, _arrow, _number;
        internal static float U(float n) { return n * Scale; }
        internal static GUISkin Begin(float scale)
        {
            Scale = scale; PatrolSkin.Ensure(scale); var old = GUI.skin;
            if (_button == null || _button.fontSize != PatrolSkin.BtnSmall.fontSize)
            {
                _button = new GUIStyle(PatrolSkin.BtnSmall) { clipping = TextClipping.Clip };
                _name = new GUIStyle(PatrolSkin.ListName) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
                _sub = new GUIStyle(PatrolSkin.ListSub) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
                _arrow = new GUIStyle(PatrolSkin.Body) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip, wordWrap = false, padding = new RectOffset() };
                _number = new GUIStyle(PatrolSkin.TextField) { font = PatrolSkin.Body.font, fontSize = PatrolSkin.Body.fontSize, alignment = TextAnchor.MiddleLeft };
                _row = FlatStyle("1B1510", "393027", 5, 5);
                _selectedRow = FlatStyle("4B3E22", "A48E5A", 5, 5);
                _group = FlatStyle("16100C", "332A23", 10, 7);
                _selectedGroup = FlatStyle("16100C", "8E7D56", 10, 7);
                _skin = null;
            }
            if (_skin == null) _skin = UnityEngine.Object.Instantiate(GUI.skin);
            _skin.label = PatrolSkin.Body; _skin.textField = PatrolSkin.TextField; _skin.verticalScrollbar = PatrolSkin.ScrollV; _skin.verticalScrollbarThumb = PatrolSkin.ScrollThumb;
            GUI.skin = _skin; return old;
        }
        private static GUIStyle FlatStyle(string fill, string border, int x, int y)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            Color inner = PatrolSkin.Hex(fill), edge = PatrolSkin.Hex(border);
            for (int row = 0; row < 8; row++) for (int col = 0; col < 8; col++) texture.SetPixel(col, row, row == 0 || col == 0 || row == 7 || col == 7 ? edge : inner);
            texture.Apply();
            var style = new GUIStyle(GUIStyle.none) { border = new RectOffset(1, 1, 1, 1), padding = new RectOffset((int)U(x), (int)U(x), (int)U(y), (int)U(y)), margin = new RectOffset(0, 0, 0, (int)U(6)) };
            style.normal.background = texture; return style;
        }
        internal static GUIStyle RowStyle(bool selected) { return selected ? _selectedRow : _row; }
        internal static GUIStyle GroupStyle(bool selected) { return selected ? _selectedGroup : _group; }
        internal static void BodyLabel(string text, bool bold = false, Color? color = null, float height = 24)
        {
            var style = bold ? _name : _sub;
            var rect = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.Height(U(height)), GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            PatrolSkin.Label(rect, text, style, color ?? PatrolSkin.Desc);
        }
        internal static void Separator()
        {
            var rect = GUILayoutUtility.GetRect(0, U(1), GUILayout.ExpandWidth(true));
            PatrolSkin.Fill(rect, PatrolSkin.Hex("30271F"));
        }
        private static void Arrow(Rect rect, bool down, Color color)
        {
            PatrolSkin.Label(rect, down ? "▼" : "▶", _arrow, color);
        }
        internal static bool Heading(string title, string subtitle, bool expanded, bool selected)
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(U(50)), GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            Color color = selected ? PatrolSkin.Yellow : PatrolSkin.White;
            Arrow(new Rect(r.x, r.y, U(20), r.height), expanded, color);
            PatrolSkin.Label(new Rect(r.x + U(29), r.y + U(2), r.width - U(29), U(23)), title, _name, color);
            PatrolSkin.Label(new Rect(r.x + U(29), r.y + U(25), r.width - U(29), U(21)), subtitle, _sub, PatrolSkin.Desc);
            return hit;
        }
        internal static bool TemplateRow(string title, bool selected)
        {
            var r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(U(30)), GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            PatrolSkin.Label(new Rect(r.x + U(5), r.y, r.width - U(10), r.height), title, _name, selected ? PatrolSkin.Yellow : PatrolSkin.White);
            return hit;
        }
        internal static bool Dropdown(string text, float width = 0, bool headingFont = false)
        {
            var options = width > 0 ? new[] { GUILayout.Width(U(width)), GUILayout.Height(U(34)) } : new[] { GUILayout.Height(U(34)), GUILayout.ExpandWidth(true), GUILayout.MinWidth(0) };
            var r = GUILayoutUtility.GetRect(GUIContent.none, PatrolSkin.TextField, options);
            GUI.Box(r, GUIContent.none, PatrolSkin.TextField);
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            Arrow(new Rect(r.x + U(5), r.y, U(20), r.height), true, PatrolSkin.Desc);
            PatrolSkin.Label(new Rect(r.x + U(32), r.y, r.width - U(42), r.height), text, headingFont ? PatrolSkin.Section : _name, PatrolSkin.White);
            return hit;
        }
        internal static void PopupSurface(Rect rect)
        {
            PatrolSkin.Fill(rect, PatrolSkin.Hex("5C544B"));
            PatrolSkin.Fill(new Rect(rect.x + U(1), rect.y + U(1), rect.width - U(2), rect.height - U(2)), PatrolSkin.Hex("16100C"));
        }
        internal static bool PopupOption(string text, bool selected)
        {
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(U(32)), GUILayout.ExpandWidth(true));
            bool hit = GUI.Button(rect, GUIContent.none, GUIStyle.none);
            if (selected || rect.Contains(Event.current.mousePosition)) PatrolSkin.Fill(rect, PatrolSkin.Hex(selected ? "4B3E22" : "30271F"));
            PatrolSkin.Label(new Rect(rect.x + U(10), rect.y, rect.width - U(20), rect.height), text, _name, selected ? PatrolSkin.Yellow : PatrolSkin.White);
            return hit;
        }
        internal static bool Button(string text, float width = 0, float height = 32, bool enabled = true, Color? color = null)
        {
            var options = width > 0 ? new[] { GUILayout.Width(U(width)), GUILayout.Height(U(height)) } : new[] { GUILayout.Height(U(height)), GUILayout.ExpandWidth(true), GUILayout.MinWidth(U(28)) };
            var r = GUILayoutUtility.GetRect(new GUIContent(text), PatrolSkin.Btn, options);
            return PatrolSkin.PlankButton(r, text, color ?? PatrolSkin.White, _button, enabled);
        }
        internal static void Title(string text)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(text), PatrolSkin.Title, GUILayout.Width(U(310)), GUILayout.Height(U(54)));
            PatrolSkin.Out(rect, text, PatrolSkin.Title, PatrolSkin.Yellow);
        }
        internal static void Label(string text, bool heading = false, Color? color = null)
        {
            var style = heading ? PatrolSkin.Section : PatrolSkin.Small;
            var r = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.Height(U(heading ? 27 : 24)));
            PatrolSkin.Out(r, text, style, color ?? (heading ? PatrolSkin.White : PatrolSkin.Desc));
        }
        internal static float Number(string key, float value, float min, float max, Action changed, float width = 84)
        {
            string text; if (!_numbers.TryGetValue(key, out text)) text = value.ToString("0.####", CultureInfo.InvariantCulture);
            string next = GUILayout.TextField(text, _number, GUILayout.Width(U(width)), GUILayout.Height(U(32)));
            if (next != text)
            {
                _numbers[key] = next; float parsed;
                if ((float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || float.TryParse(next, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)) && !float.IsNaN(parsed) && !float.IsInfinity(parsed))
                { float n = Mathf.Clamp(parsed, min, max); if (n != parsed) _numbers[key] = n.ToString("0.####", CultureInfo.InvariantCulture); if (n != value) { changed(); return n; } }
            }
            return value;
        }
        internal static void ClearNumbers() { _numbers.Clear(); }
        internal static string Quantity(float chance)
        {
            int n = (int)Math.Floor(chance / 100f); float remainder = chance - n * 100;
            return remainder > 0 ? n + " guaranteed + " + remainder.ToString("0.##", CultureInfo.InvariantCulture) + " % for another" : n + " guaranteed";
        }
        internal static void WindowBackground(Rect rect)
        {
            PatrolSkin.RustBack(rect); PatrolSkin.Frame(rect, U(18));
        }
        private static Texture2D _star, _outlineStar, _trash;
        internal static bool Icon(bool star, bool filled, bool enabled, Color color)
        {
            if (_star == null) CreateIcons();
            Rect r = GUILayoutUtility.GetRect(U(28), U(30), GUILayout.Width(U(28)), GUILayout.Height(U(30)));
            bool old = GUI.enabled; GUI.enabled = old && enabled;
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            Color previous = GUI.color; GUI.color = enabled ? color : PatrolSkin.Dim;
            GUI.DrawTexture(new Rect(r.x + U(6), r.y + U(7), U(16), U(16)), star ? filled ? _star : _outlineStar : _trash);
            GUI.color = previous; GUI.enabled = old; return hit;
        }
        private static void CreateIcons()
        {
            _star = new Texture2D(16, 16, TextureFormat.RGBA32, false); _trash = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            var points = new Vector2[10]; for (int i = 0; i < 10; i++) { float angle = Mathf.PI / 2 + i * Mathf.PI / 5; float radius = i % 2 == 0 ? 7 : 3; points[i] = new Vector2(8 + Mathf.Cos(angle) * radius, 8 + Mathf.Sin(angle) * radius); }
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                bool inside = false; for (int i = 0, j = 9; i < 10; j = i++) if ((points[i].y > y) != (points[j].y > y) && x < (points[j].x - points[i].x) * (y - points[i].y) / (points[j].y - points[i].y) + points[i].x) inside = !inside;
                _star.SetPixel(x, y, inside ? Color.white : Color.clear);
                bool trash = (y == 12 && x >= 3 && x <= 12) || (y == 14 && x >= 6 && x <= 9) || (x >= 4 && x <= 11 && y >= 2 && y <= 10 && (x == 4 || x == 11 || y == 2 || x == 7 || x == 9));
                _trash.SetPixel(x, y, trash ? Color.white : Color.clear);
            }
            _outlineStar = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                bool edge = _star.GetPixel(x, y).a > 0 && (x == 0 || y == 0 || x == 15 || y == 15 || _star.GetPixel(Mathf.Max(0, x - 1), y).a == 0 || _star.GetPixel(Mathf.Min(15, x + 1), y).a == 0 || _star.GetPixel(x, Mathf.Max(0, y - 1)).a == 0 || _star.GetPixel(x, Mathf.Min(15, y + 1)).a == 0);
                _outlineStar.SetPixel(x, y, edge ? Color.white : Color.clear);
            }
            _star.Apply(); _outlineStar.Apply(); _trash.Apply(); _star.hideFlags = _outlineStar.hideFlags = _trash.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}

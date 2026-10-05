using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    // Measured from the selected Salvage ledger prototype, including its game chrome.
    internal static class LedgerSkin
    {
        internal const float Width = 1028, Height = 755;
        internal static readonly Color Background = C("1B1610"), Surface = C("282119"), Raised = C("332C21"),
            Field = C("17130D"), Text = C("EEE4CF"), Muted = C("B8AD99"), Line = C("514835"),
            Accent = C("E4CB69"), Selected = C("3B3321"), Danger = C("F09578");
        internal static GUIStyle Body, Small, Heading, Brand, GameButton, DialogTitle, Input, Scrollbar;
        private static GUIStyle CloseText, InputRight;
        private static GUISkin _skin;
        private static Font _body, _bold, _game;
        private static readonly Dictionary<string, Texture2D> Icons = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, string> Numbers = new Dictionary<string, string>();
        private static readonly Dictionary<string, Texture2D> Boxes = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, float> ButtonOffsets = new Dictionary<string, float>();
        internal static Color C(string hex) { return PatrolSkin.Hex(hex); }
        internal static GUISkin Begin()
        {
            var old = GUI.skin; PatrolSkin.Ensure(1);
            var font = PatrolSkin.GameFont ?? Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == "Helveticrap");
            if (_skin == null || (_game != font && font != null))
            {
                if (_body == null) _body = Font.CreateDynamicFontFromOSFont("Segoe UI", 14);
                if (_bold == null) _bold = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI Semibold", "Segoe UI" }, 14);
                _game = font;
                Body = Style(_body, 14); Small = Style(_body, 12); Heading = Style(_bold, 14); Heading.fontStyle = FontStyle.Bold;
                DialogTitle = Style(_bold, 18); DialogTitle.fontStyle = FontStyle.Bold; Brand = Style(_game ?? _bold, 26); GameButton = Style(_game ?? _bold, 15); CloseText = Style(_game ?? _bold, 13);
                Input = Style(_body, 14); Input.padding = new RectOffset(9, 9, 6, 6); Input.border = new RectOffset(1, 1, 1, 1);
                Input.normal.background = Box("17130D", "514835"); Input.hover.background = Input.normal.background;
                Input.focused.background = Box("17130D", "E4CB69"); Input.active.background = Input.focused.background;
                InputRight = new GUIStyle(Input) { alignment = TextAnchor.MiddleRight };
                _skin = UnityEngine.Object.Instantiate(old); _skin.label = Body; _skin.textField = Input;
                Scrollbar = new GUIStyle(old.verticalScrollbar) { fixedWidth = 7, padding = new RectOffset(), margin = new RectOffset(), border = new RectOffset() };
                Scrollbar.normal.background = Box("17130D", "17130D");
                _skin.verticalScrollbar = Scrollbar;
                var thumb = new GUIStyle(old.verticalScrollbarThumb) { fixedWidth = 7, padding = new RectOffset(), margin = new RectOffset(), border = new RectOffset(), fixedHeight = 0 };
                thumb.normal.background = thumb.hover.background = thumb.active.background = Box("514835", "514835");
                _skin.verticalScrollbarThumb = thumb;
                _skin.verticalScrollbarUpButton = _skin.verticalScrollbarDownButton = new GUIStyle(GUIStyle.none) { fixedHeight = 0, fixedWidth = 0 };
            }
            GUI.skin = _skin; return old;
        }
        private static GUIStyle Style(Font font, int size)
        {
            var s = new GUIStyle(GUIStyle.none) { font = font, fontSize = size, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = Text; return s;
        }
        private static Texture2D Box(string fill, string edge)
        {
            string key = fill + edge; Texture2D texture; if (Boxes.TryGetValue(key, out texture)) return texture;
            texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) texture.SetPixel(x, y, x == 0 || y == 0 || x == 7 || y == 7 ? C(edge) : C(fill));
            texture.Apply(); Boxes[key] = texture; return texture;
        }
        internal static void Fill(Rect r, Color color) { PatrolSkin.Fill(r, color); }
        internal static void Panel(Rect r, Color? fill = null, Color? border = null)
        {
            Fill(r, fill ?? Surface); var edge = border ?? Line;
            Fill(new Rect(r.x, r.y, r.width, 1), edge); Fill(new Rect(r.x, r.yMax - 1, r.width, 1), edge);
            Fill(new Rect(r.x, r.y, 1, r.height), edge); Fill(new Rect(r.xMax - 1, r.y, 1, r.height), edge);
        }
        internal static void Label(Rect r, string text, GUIStyle font = null, Color? color = null, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var style = font ?? Body; var previous = style.alignment; style.alignment = align;
            PatrolSkin.Label(r, text, style, color ?? Text); style.alignment = previous;
        }
        internal static bool Button(Rect r, string text, bool enabled = true, bool primary = false, bool gameFont = false, Color? color = null, bool transparent = false)
        {
            bool previous = GUI.enabled; GUI.enabled = previous && enabled;
            bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            var fill = primary ? Accent : hover ? Selected : transparent ? Color.clear : Raised; var border = primary || hover ? Accent : Line;
            if (!enabled) { fill = Color.Lerp(Background, fill, .4f); border = Color.Lerp(Background, border, .4f); }
            Panel(r, fill, border);
            var ink = color ?? (primary ? Background : Text);
            if (text.StartsWith("+  ", StringComparison.Ordinal))
            {
                string caption = text.Substring(3); var font = caption.StartsWith("New ", StringComparison.Ordinal) ? Small : Body;
                float length = font.CalcSize(new GUIContent(caption)).x, left = r.center.x - (length + 23) / 2;
                IconImage(new Rect(left, r.center.y - 8, 16, 16), "plus", ink);
                ButtonLabel(new Rect(left + 23, r.y, length + 1, r.height), caption, font, ink, TextAnchor.MiddleLeft);
            }
            else if (gameFont) Label(r, text, CloseText, ink, TextAnchor.MiddleCenter);
            else ButtonLabel(r, text, Body, ink, TextAnchor.MiddleCenter);
            GUI.enabled = previous; return hit;
        }
        internal static void ButtonLabel(Rect r, string text, GUIStyle style, Color ink, TextAnchor alignment)
        {
            // Centre the visible glyphs, rather than Segoe UI's asymmetric ascent/descent line box.
            string key = style.font.GetInstanceID() + "/" + style.fontSize + "/" + text; float shift;
            if (!ButtonOffsets.TryGetValue(key, out shift))
            {
                var font = style.font; font.RequestCharactersInTexture(text, style.fontSize, style.fontStyle);
                int min = int.MaxValue, max = int.MinValue;
                foreach (char c in text) { if (char.IsWhiteSpace(c)) continue; CharacterInfo info; if (!font.GetCharacterInfo(c, out info, style.fontSize, style.fontStyle)) continue; min = Mathf.Min(min, info.minY); max = Mathf.Max(max, info.maxY); }
                float scale = font.fontSize > 0 ? style.fontSize / (float)font.fontSize : 1;
                shift = min <= max ? font.lineHeight * scale / 2 - (font.ascent * scale - (min + max) / 2f) : 0;
                ButtonOffsets[key] = shift;
            }
            r.y += shift; Label(r, text, style, ink, alignment);
        }
        internal static void IconImage(Rect r, string name, Color color)
        {
            Texture2D icon;
            if (!Icons.TryGetValue(name, out icon))
            {
                string path = Path.Combine(Path.GetDirectoryName(PatrolSkin.Dir), "ledger", name + ".png");
                if (!File.Exists(path)) return;
                icon = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (!ImageConversion.LoadImage(icon, File.ReadAllBytes(path), false)) return; Icons[name] = icon;
            }
            var previous = GUI.color; GUI.color = color; GUI.DrawTexture(r, icon); GUI.color = previous;
        }
        internal static bool Icon(Rect r, string name, bool enabled = true, Color? color = null)
        {
            bool previous = GUI.enabled; GUI.enabled = previous && enabled;
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (GUI.enabled && r.Contains(Event.current.mousePosition)) Panel(r, Selected, Accent);
            var tint = enabled ? color ?? Text : new Color(Muted.r, Muted.g, Muted.b, .45f);
            IconImage(new Rect(r.center.x - 8, r.center.y - 8, 16, 16), name, tint);
            GUI.enabled = previous; return hit;
        }
        internal static bool Dropdown(Rect r, string text)
        {
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none); Panel(r, Field, r.Contains(Event.current.mousePosition) && GUI.enabled ? Accent : Line);
            ButtonLabel(new Rect(r.x + 12, r.y, r.width - 40, r.height), text, Body, Text, TextAnchor.MiddleLeft);
            IconImage(new Rect(r.xMax - 27, r.center.y - 8, 16, 16), "chevron-down", Text); return hit;
        }
        internal static void Rule(float x, float y, float width) { Fill(new Rect(x, y, width, 1), Line); }
        internal static void Window(Rect r)
        {
            PatrolSkin.RustBack(r); Fill(r, new Color(16 / 255f, 13 / 255f, 9 / 255f, .5f));
            // Stretch each edge independently exactly like the prototype's native game frame.
            if (PatrolSkin.RebarH != null)
            {
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, 6), PatrolSkin.RebarH);
                GUI.DrawTexture(new Rect(r.x, r.yMax - 6, r.width, 6), PatrolSkin.RebarH);
                GUI.DrawTexture(new Rect(r.x, r.y, 6, r.height), PatrolSkin.RebarV);
                GUI.DrawTexture(new Rect(r.xMax - 6, r.y, 6, r.height), PatrolSkin.RebarV);
            }
            if (PatrolSkin.Rivet != null) foreach (var at in new[] { new Vector2(r.x + 10, r.y + 10), new Vector2(r.xMax - 18, r.y + 10), new Vector2(r.x + 10, r.yMax - 18), new Vector2(r.xMax - 18, r.yMax - 18) }) GUI.DrawTexture(new Rect(at.x, at.y, 8, 8), PatrolSkin.Rivet);
        }
        internal static void Header(float width)
        {
            Fill(new Rect(6, 6, width - 12, 74), new Color(16 / 255f, 13 / 255f, 9 / 255f, .65f));
            Label(new Rect(26, 24, 430, 38), "APOCAPATROL", Brand, Accent);
            Rule(6, 79, width - 12);
        }
        internal static bool Toggle(Rect r, bool value, string label)
        {
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none); Panel(new Rect(r.x, r.center.y - 8, 16, 16), value ? Accent : Field);
            if (value) IconImage(new Rect(r.x + 1, r.center.y - 7, 14, 14), "check", Background);
            Label(new Rect(r.x + 24, r.y, r.width - 24, r.height), label); return hit ? !value : value;
        }
        internal static float Number(Rect r, string key, float value, float min, float max, Action changed, bool right = false)
        {
            string text; if (!Numbers.TryGetValue(key, out text)) text = value.ToString("0.####", CultureInfo.InvariantCulture);
            var next = GUI.TextField(r, text, right ? InputRight : Input); if (next == text) return value; Numbers[key] = next;
            float parsed; if ((!float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && !float.TryParse(next, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)) || float.IsNaN(parsed) || float.IsInfinity(parsed)) return value;
            var number = Mathf.Clamp(parsed, min, max); if (number != parsed) Numbers[key] = number.ToString("0.####", CultureInfo.InvariantCulture);
            if (number != value) changed(); return number;
        }
        internal static void ClearNumbers() { Numbers.Clear(); }
        internal static string Quantity(float chance)
        {
            int n = (int)Math.Floor(chance / 100f); float rest = chance % 100;
            return n + " guaranteed" + (rest > 0 ? " + " + rest.ToString("0.##", CultureInfo.InvariantCulture) + "% next" : "");
        }
        internal static Vector2 Scroll(Rect r, Vector2 scroll, float height, Action<float> draw)
        {
            bool overflow = height > r.height; float width = r.width - (overflow ? 10 : 0);
            scroll = GUI.BeginScrollView(r, scroll, new Rect(0, 0, width, Mathf.Max(height, r.height)), false, overflow, GUIStyle.none, Scrollbar);
            draw(width); GUI.EndScrollView(); return scroll;
        }
    }

    // One popup implementation for spawning configuration and template creation.
    internal sealed class LedgerDropdown
    {
        internal bool IsOpen { get { return _options != null; } }
        private Rect _anchor, _box;
        private string _selected;
        private List<KeyValuePair<string, string>> _options;
        private Action<string> _setter;
        private Action _pending;
        private Vector2 _scroll;
        internal void Open(Rect anchor, List<KeyValuePair<string, string>> options, string selected, Action<string> setter, float bottom)
        {
            _pending = () =>
            {
                _anchor = anchor; _options = options; _selected = selected; _setter = setter; _scroll = Vector2.zero;
                float height = Mathf.Min(196, 8 + options.Count * 34), y = anchor.yMax + 4;
                if (y + height > bottom) y = anchor.y - height - 4;
                _box = new Rect(anchor.x, Mathf.Max(10, y), anchor.width, height);
            };
        }
        internal void Before()
        {
            if (IsOpen && Event.current.type == EventType.MouseDown && !_box.Contains(Event.current.mousePosition))
            { _pending = Close; Event.current.Use(); }
        }
        internal void Draw()
        {
            if (!IsOpen) return;
            LedgerSkin.Panel(_box, LedgerSkin.Surface, LedgerSkin.Accent);
            _scroll = LedgerSkin.Scroll(new Rect(_box.x + 4, _box.y + 4, _box.width - 8, _box.height - 8), _scroll, _options.Count * 34, width =>
            {
                for (int i = 0; i < _options.Count; i++)
                {
                    var option = _options[i]; var row = new Rect(0, i * 34, width, 34); bool selected = option.Key == _selected;
                    if (selected || row.Contains(Event.current.mousePosition)) LedgerSkin.Fill(row, LedgerSkin.Selected);
                    bool hit = GUI.Button(row, GUIContent.none, GUIStyle.none);
                    LedgerSkin.ButtonLabel(new Rect(9, row.y, width - 18, 34), option.Value, LedgerSkin.Body, selected ? LedgerSkin.Accent : LedgerSkin.Text, TextAnchor.MiddleLeft);
                    if (hit) { string key = option.Key; var setter = _setter; _pending = () => { setter(key); Close(); }; }
                }
            });
        }
        internal void Flush() { if (_pending != null) { var action = _pending; _pending = null; action(); } }
        internal void Close() { _options = null; _pending = null; }
    }
}

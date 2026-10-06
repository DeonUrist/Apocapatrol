using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Apocapatrol
{
    internal sealed partial class TemplateMenu
    {
        private readonly LedgerDropdown _dropdown = new LedgerDropdown();
        private Vector2 _allowedScroll, _cargoLibraryScroll;
        private string _cargoChoice = "";
        private static readonly string[] People = { "None", "Boltjaw", "Flexa", "Lugnut", "Scraffa", "Scrud", "Spanna", "Sprokka" };
        private string _editDriver = "", _editPassenger = "", _editLoot = "";
        private readonly string[] _editTurrets = { "None", "None", "None" };

        // the editor's template order: EditorData.TemplateOrder first, then the unlisted ones (defaults first, by name)
        private static CarTemplate[] Ordered(bool truck)
        {
            var order = EditorStore.Data.TemplateOrder;
            return CarTemplate.Park.Where(t => t.IsTruck == truck)
                .Select(t => new { t, i = order.FindIndex(n => n.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) })
                .OrderBy(x => x.i < 0 ? 1 : 0).ThenBy(x => x.i).ThenBy(x => x.t.IsDefault ? 0 : 1).ThenBy(x => x.t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.t).ToArray();
        }
        private static void MoveTemplate(bool truck, int index, int delta)
        {
            var list = Ordered(truck).ToList(); int to = index + delta;
            if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return;
            var t = list[index]; list.RemoveAt(index); list.Insert(to, t);
            // both lists are kept in one order list: the cars first, then the trucks
            EditorStore.Data.TemplateOrder = (truck ? Ordered(false) : list.ToArray()).Concat(truck ? list.ToArray() : Ordered(true)).Select(c => c.Name).ToList();
        }
        private static void Move<T>(List<T> list, int index, int delta)
        {
            int to = index + delta; if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return;
            var item = list[index]; list.RemoveAt(index); list.Insert(to, item);
        }
        private static List<KeyValuePair<string, string>> Options(IEnumerable<string> values) { return values.Select(v => new KeyValuePair<string, string>(v, v)).ToList(); }

        // writes driver / passenger / loot option into the template's file (BaseTemplates or PlayerTemplates) and reloads the park
        private static void SaveTemplateEdit(CarTemplate t, string driver, string passenger, string loot, string[] turrets)
        {
            if (string.IsNullOrEmpty(t.SourcePath) || !System.IO.File.Exists(t.SourcePath)) throw new InvalidOperationException("The template file was not found");
            var file = TemplateFile.FromJson(System.IO.File.ReadAllText(t.SourcePath));
            file.driver = driver == "None" ? "" : driver; file.passenger = passenger == "None" ? "" : passenger; file.lootPreset = loot ?? "";
            for (int i = 0; i < 3; i++) file.SetTurret(i, i < turrets.Length ? Rider.Stored(turrets[i]) : "");
            EditorStore.AtomicWrite(t.SourcePath, file.ToJson());
            CarTemplates.Refresh(true);
        }
        private static string TurretSummary(CarTemplate t)
        {
            var on = t.Turrets.Where(v => !string.IsNullOrEmpty(v)).Select(Rider.Display).ToArray();
            return on.Length > 0 ? " ● turrets " + string.Join(", ", on) : "";
        }
        private void ShowModal(string kind, object target = null)
        {
            Queue(() => { _target = target; _modal = kind; _name = ""; _deleteCustom = false; _modalScroll = Vector2.zero; }, false);
        }
        private void DrawWindow(int id)
        {
            _dropdown.Before(); bool modal = _modal.Length > 0, enabled = GUI.enabled;
            LedgerSkin.Window(_rect); LedgerSkin.Header(_rect.width);
            GUI.enabled = enabled && !modal && !_dropdown.IsOpen;
            if (LedgerSkin.Button(new Rect(_rect.width - 96, 24, 70, 34), "CLOSE", gameFont: true, transparent: true)) Queue(Close, false);
            LedgerSkin.Fill(new Rect(6, 80, _rect.width - 12, 43), LedgerSkin.Surface);
            string[] tabs = { "PATROLS", "CONVOYS", "LOOT" };
            for (int i = 0; i < 3; i++)
            {
                int tab = i; var r = new Rect(26 + 150 * i, 80, 150, 43);
                bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
                if (_tab == i) { LedgerSkin.Fill(r, LedgerSkin.Selected); LedgerSkin.Fill(new Rect(r.x, r.yMax - 3, r.width, 3), LedgerSkin.Accent); }
                LedgerSkin.Rule(r.xMax - 1, r.y, 1); LedgerSkin.Fill(new Rect(r.xMax - 1, r.y, 1, r.height), LedgerSkin.Line);
                LedgerSkin.Label(r, tabs[i], LedgerSkin.GameButton, _tab == i ? LedgerSkin.Accent : LedgerSkin.Text, TextAnchor.MiddleCenter);
                if (hit) Queue(() => { _tab = tab; _selectedGroup = _selectedTemplate = ""; _expanded.Clear(); _leftScroll = Vector2.zero; _dropdown.Close(); _status = tab == 2 ? "Each item rolls its own absolute chance." : "Select a patrol or convoy, then choose a template."; }, false);
            }
            LedgerSkin.Rule(6, 122, _rect.width - 12);
            if (_tab == 2) DrawLootLedger(); else DrawGroupsLedger();
            float footer = _rect.height - 70; LedgerSkin.Rule(6, footer, _rect.width - 12);
            var detail = CarTemplate.Find(_selectedTemplate);
            if (detail != null) LedgerSkin.Label(new Rect(26, footer + 12, _rect.width - 308, 40), detail.Body + " ● " + (string.IsNullOrEmpty(detail.Driver) ? "None" : detail.Driver) + " ● " + (string.IsNullOrEmpty(detail.Passenger) ? "None" : detail.Passenger) + " ● " + (detail.IsTruck ? (EditorStore.Loot(true, detail.LootPreset)?.Name ?? "Convoy Cargo") : EditorStore.Loot(false, detail.LootPreset)?.Name ?? "None") + TurretSummary(detail), LedgerSkin.Small, LedgerSkin.Muted);
            if (Plugin.AllowDebugSpawns.Value && LedgerSkin.Button(new Rect(_rect.width - 270, footer + 15, 118, 34), "SPAWN", gameFont: true)) Queue(() => Schedule(() => _convoy.DebugRoll()), false);
            if (LedgerSkin.Button(new Rect(_rect.width - 144, footer + 15, 118, 34), "RESET", gameFont: true, color: LedgerSkin.Danger, transparent: true)) ShowModal("reset");
            GUI.enabled = enabled;
            if (modal) DrawLedgerModal();
            _dropdown.Draw();
        }
        private void DrawGroupsLedger()
        {
            float gap = 16, available = _rect.width - 52, leftWidth = (available - gap) * 1.65f / 2.65f;
            float top = 143, height = _rect.height - 233;
            var left = new Rect(26, top, leftWidth, height); var right = new Rect(left.xMax + gap, top, available - gap - leftWidth, height);
            LedgerSkin.Panel(left, new Color(23 / 255f, 19 / 255f, 13 / 255f, .91f)); LedgerSkin.Panel(right, new Color(23 / 255f, 19 / 255f, 13 / 255f, .91f));
            float lx = left.x + 11, lw = left.width - 22;
            LedgerSkin.Label(new Rect(lx, top + 11, lw - 150, 34), _tab == 1 ? "Convoy types" : "Patrol types", LedgerSkin.Heading);
            if (LedgerSkin.Button(new Rect(lx + lw - 121, top + 11, 121, 34), _tab == 1 ? "+  New convoy" : "+  New patrol")) ShowModal("newGroup");
            var scrollRect = new Rect(lx, top + 57, lw, height - 68);
            LedgerSkin.Fill(scrollRect, LedgerSkin.Surface);
            float content = Groups.Sum(GroupHeight);
            _leftScroll = LedgerSkin.Scroll(scrollRect, _leftScroll, content, width =>
            {
                float y = 0;
                for (int gi = 0; gi < Groups.Count; gi++) { var group = Groups[gi]; DrawLedgerGroup(group, gi, new Rect(0, y, width, GroupHeight(group))); y += GroupHeight(group); }
                if (Groups.Count == 0) LedgerSkin.Label(new Rect(10, 8, width - 20, 36), "No groups. Create one to assign templates.", LedgerSkin.Small, LedgerSkin.Muted);
            });
            float rx = right.x + 11, rw = right.width - 22;
            LedgerSkin.Label(new Rect(rx, top + 11, rw, 20), "Patrol car templates", LedgerSkin.Heading);
            DrawTemplateLedger(false, new Rect(rx, top + 39, rw, 174), ref _carScroll);
            LedgerSkin.Label(new Rect(rx, top + 225, rw, 20), "Convoy truck templates", LedgerSkin.Heading);
            DrawTemplateLedger(true, new Rect(rx, top + 253, rw, 158), ref _truckScroll);
            float actions = right.yMax - 99; LedgerSkin.Rule(rx, actions, rw);
            var chosen = CarTemplate.Find(_selectedTemplate); var selected = Selected;
            bool canAdd = chosen != null && selected != null && (_tab == 1 || !chosen.IsTruck);
            if (LedgerSkin.Button(new Rect(rx, actions + 13, rw, 34), _tab == 1 ? "+  Add template to convoy" : "+  Add template to patrol", canAdd, true)) Queue(() => { selected.Templates.Add(chosen.Name); _status = "Added one copy of " + chosen.Name; });
            if (Plugin.AllowDebugSpawns.Value && LedgerSkin.Button(new Rect(rx, actions + 54, rw, 34), "Spawn template", chosen != null)) Queue(() => { var spawn = chosen.CloneForSpawn(); if (spawn.IsTruck && _tab == 1 && selected != null) spawn.SpawnCargoOptions = selected.AllowedCargo.ToArray(); Schedule(() => _patrol.Spawn(spawn)); }, false);
        }
        private float GroupHeight(SpawnGroup group)
        {
            if (!_expanded.Contains(group.Id)) return 73;
            int rows = group.Templates.Count; bool fallback = !group.Templates.Any(n => { var t = CarTemplate.Find(n); return t != null && (_tab != 1 || t.IsTruck); });
            return 73 + 12 + 63 + 14 + 22 + (rows + (fallback ? 1 : 0)) * 36 + (_tab == 1 ? 79 : 0) + 36 + 12;
        }
        private static int VehicleCount(SpawnGroup group, bool convoy)
        {
            var list = group.Templates.Select(CarTemplate.Find).Where(t => t != null && (convoy || !t.IsTruck)).ToList();
            return list.Count + ((convoy && !list.Any(t => t.IsTruck)) || (!convoy && list.Count == 0) ? 1 : 0);
        }
        private void DrawLedgerGroup(SpawnGroup group, int index, Rect r)
        {
            bool open = _expanded.Contains(group.Id); int count = VehicleCount(group, _tab == 1);
            LedgerSkin.Fill(r, LedgerSkin.Surface); LedgerSkin.Rule(r.x, r.yMax - 1, r.width);
            float headerY = open ? Mathf.Clamp(_leftScroll.y, r.y, r.yMax - 73) : r.y;
            bool wasEnabled = GUI.enabled;
            if (open)
            {
                // Sticky selected header gets the input instead of the content underneath it.
                if (new Rect(r.x, headerY, r.width, 73).Contains(Event.current.mousePosition)) GUI.enabled = false;
                float x = r.x + 12, w = r.width - 24, y = r.y + 86, col = (w - 20) / 3;
                LedgerSkin.Rule(r.x, r.y + 72, r.width);
                string[] labels = { "Min. distance · km", "Min. bosses", "Spawn chance · %" };
                for (int i = 0; i < 3; i++) LedgerSkin.Label(new Rect(x + i * (col + 10), y, col, 20), labels[i], LedgerSkin.Small, LedgerSkin.Muted);
                group.MinKm = LedgerSkin.Number(new Rect(x, y + 25, col, 34), group.Id + ".km", group.MinKm, 0, 10000, Dirty);
                group.MinBosses = (int)LedgerSkin.Number(new Rect(x + col + 10, y + 25, col, 34), group.Id + ".bosses", group.MinBosses, 0, 7, Dirty);
                group.Chance = LedgerSkin.Number(new Rect(x + 2 * (col + 10), y + 25, col, 34), group.Id + ".chance", group.Chance, 0, 100, Dirty);
                y += 77; LedgerSkin.Label(new Rect(x, y, w, 22), "VEHICLE ROSTER", LedgerSkin.Small, LedgerSkin.Muted); y += 22;
                int found = 0, trucks = 0;
                for (int i = 0; i < group.Templates.Count; i++)
                {
                    int slot = i; var t = CarTemplate.Find(group.Templates[i]); if (t != null) { found++; if (t.IsTruck) trucks++; }
                    LedgerSkin.Label(new Rect(x, y, 19, 36), (i + 1).ToString(), LedgerSkin.Small, LedgerSkin.Muted);
                    LedgerSkin.Label(new Rect(x + 27, y, w - 65, 36), t != null ? t.Name.Replace('_', ' ') : group.Templates[i] + " [missing]", null, t != null ? LedgerSkin.Text : LedgerSkin.Muted);
                    if (LedgerSkin.Icon(new Rect(x + w - 30, y + 3, 30, 30), "circle-minus")) Queue(() => group.Templates.RemoveAt(slot));
                    LedgerSkin.Rule(x, y + 35, w); y += 36;
                }
                if ((_tab == 0 && found == 0) || (_tab == 1 && trucks == 0)) { LedgerSkin.Label(new Rect(x, y, w, 36), _tab == 1 ? "Empty truck" : "Basic PipeRat", LedgerSkin.Small, LedgerSkin.Muted); y += 36; }
                if (_tab == 1)
                {
                    if (LedgerSkin.Button(new Rect(x, y + 7, 145, 34), "Allowed cargo")) Queue(() => { _target = group; _modal = "allowedCargo"; _cargoChoice = ""; _allowedScroll = _cargoLibraryScroll = Vector2.zero; }, false);
                    y += 42;
                    bool uniform = LedgerSkin.Toggle(new Rect(x, y + 7, w, 30), group.UniformCargo, "Uniform cargo"); if (uniform != group.UniformCargo) { group.UniformCargo = uniform; Dirty(); } y += 37;
                }
                LedgerSkin.Label(new Rect(x, y + 8, w, 24), count + " vehicles · each entry spawns one vehicle", LedgerSkin.Small, LedgerSkin.Muted);
                GUI.enabled = wasEnabled;
            }
            LedgerSkin.Fill(new Rect(r.x, headerY, r.width, 72), open ? LedgerSkin.Selected : LedgerSkin.Surface);
            float tools = Plugin.AllowDebugSpawns.Value ? 108 : 78;   // trash [+ S] + the reorder column
            var select = new Rect(r.x + 9, headerY + 9, r.width - tools - 30, 54);
            // the small controls first: the header's own button covers them and would take the click otherwise
            float nameWidth = Mathf.Min(LedgerSkin.TextWidth(group.Name, LedgerSkin.Body) + 4, select.width - 29 - 62);
            if (LedgerSkin.TextButton(new Rect(select.x + 29 + nameWidth + 6, headerY + 15, 54, 23), "rename")) Queue(() => { _target = group; _modal = "rename"; _name = group.Name; }, false);
            int move = LedgerSkin.Reorder(new Rect(r.xMax - tools - 26, headerY + 9, 26, 54), index > 0, index < Groups.Count - 1);
            if (move != 0) Queue(() => Move(Groups, index, move));
            bool hit = GUI.Button(select, GUIContent.none, GUIStyle.none);
            LedgerSkin.IconImage(new Rect(select.x + 4, select.center.y - 8, 17, 17), open ? "chevron-down" : "chevron-right", LedgerSkin.Muted);
            LedgerSkin.Label(new Rect(select.x + 29, headerY + 15, nameWidth, 23), group.Name, null, open ? LedgerSkin.Accent : LedgerSkin.Text);
            LedgerSkin.Label(new Rect(select.x + 29, headerY + 40, select.width - 29, 20), group.MinKm.ToString("0.#") + " km · " + group.MinBosses + " bosses · " + group.Chance.ToString("0.#") + "% · " + count + " vehicles", LedgerSkin.Small, LedgerSkin.Muted);
            if (hit) Queue(() => _selectedGroup = EditorSelection.Select(_expanded, group.Id, !open), false);
            if (Plugin.AllowDebugSpawns.Value)
            {
                var spawn = new Rect(r.xMax - 65, headerY + 21, 30, 30); bool spawnHit = GUI.Button(spawn, GUIContent.none, GUIStyle.none);
                LedgerSkin.Label(spawn, "S", null, null, TextAnchor.MiddleCenter);
                if (spawnHit) { bool convoy = _tab == 1; Queue(() => Schedule(() => _convoy.DebugSpawn(group, convoy)), false); }
            }
            if (LedgerSkin.Icon(new Rect(r.xMax - 35, headerY + 21, 30, 30), "trash-2", color: LedgerSkin.Danger)) ShowModal("deleteGroup", group);
        }
        private void DrawTemplateLedger(bool truck, Rect r, ref Vector2 scroll)
        {
            LedgerSkin.Panel(r); var templates = Ordered(truck);
            scroll = LedgerSkin.Scroll(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), scroll, templates.Length * 39, width =>
            {
                for (int i = 0; i < templates.Length; i++)
                {
                    var t = templates[i]; bool selected = _selectedTemplate == t.Name; float y = i * 39;
                    if (selected) LedgerSkin.Fill(new Rect(0, y, width, 39), LedgerSkin.Selected);
                    string caption = t.Name.Replace('_', ' ');
                    float nameWidth = Mathf.Min(LedgerSkin.TextWidth(caption, LedgerSkin.Body) + 4, width - 91 - 9 - 30);
                    // the small controls first (the row's own button covers them)
                    if (LedgerSkin.TextButton(new Rect(9 + nameWidth + 6, y + 9, 21, 21), "E", true)) Queue(() => { _target = t; _modal = "editTemplate"; _editDriver = t.Driver.Length > 0 ? t.Driver : "None"; _editPassenger = t.Passenger.Length > 0 ? t.Passenger : "None"; _editLoot = t.LootPreset; for (int k = 0; k < 3; k++) _editTurrets[k] = Rider.Display(t.Turrets[k]); _dropdown.Close(); }, false);
                    int row = i, move = LedgerSkin.Reorder(new Rect(width - 91, y + 1, 26, 37), i > 0, i < templates.Length - 1);
                    if (move != 0) Queue(() => MoveTemplate(truck, row, move));
                    var name = new Rect(0, y, width - 91, 39); bool hit = GUI.Button(name, GUIContent.none, GUIStyle.none);
                    LedgerSkin.Label(new Rect(9, y, nameWidth, 39), caption, null, selected ? LedgerSkin.Accent : LedgerSkin.Text);
                    if (hit) Queue(() => _selectedTemplate = t.Name, false);
                    if (LedgerSkin.Icon(new Rect(width - 61, y + 4, 30, 30), t.Favorite ? "star-filled" : "star", !t.IsDefault, t.Favorite ? LedgerSkin.Muted : LedgerSkin.Text)) Queue(() => { CarTemplates.Favorite(t); _status = t.Name + " moved to " + (t.Favorite ? "PlayerTemplates" : "BaseTemplates"); }, false);
                    if (LedgerSkin.Icon(new Rect(width - 31, y + 4, 30, 30), "trash-2", !t.Favorite && !t.IsDefault, LedgerSkin.Danger)) ShowModal("deleteTemplate", t);
                    LedgerSkin.Rule(0, y + 38, width);
                }
            });
        }
        private void DrawLootLedger()
        {
            float height = LootHeight(false) + 16 + LootHeight(true);
            _lootScroll = LedgerSkin.Scroll(new Rect(26, 143, _rect.width - 52, _rect.height - 233), _lootScroll, height, width =>
            {
                DrawLootLedgerSection(false, new Rect(0, 0, width, LootHeight(false)));
                DrawLootLedgerSection(true, new Rect(0, LootHeight(false) + 16, width, LootHeight(true)));
            });
        }
        private LootProfile CurrentLoot(bool truck)
        {
            int i = truck ? 1 : 0; var profiles = truck ? EditorStore.Data.TruckLoot : EditorStore.Data.CarLoot;
            var selected = profiles.FirstOrDefault(p => p.Id == _lootSelected[i]) ?? profiles.FirstOrDefault();
            _lootSelected[i] = selected != null ? selected.Id : ""; return selected;
        }
        private float LootHeight(bool truck) { var p = CurrentLoot(truck); return 14 + 34 + 12 + 34 + 10 + (truck ? 96 : 0) + Mathf.Max(1, p == null ? 0 : p.Items.Count) * 46 + 34 + 10 + 20 + 14; }
        private void DrawLootLedgerSection(bool truck, Rect r)
        {
            var profiles = truck ? EditorStore.Data.TruckLoot : EditorStore.Data.CarLoot; var profile = CurrentLoot(truck); int category = truck ? 1 : 0;
            LedgerSkin.Panel(r); float x = r.x + 14, width = r.width - 28, y = r.y + 14;
            LedgerSkin.Label(new Rect(x, y, width - 165, 34), truck ? "Loot for convoy trucks" : "Loot for patrol cars", LedgerSkin.Heading);
            if (LedgerSkin.Button(new Rect(x + width - 157, y, 157, 34), "+  New loot option")) ShowModal("newLoot", profiles);
            y += 46; var anchor = new Rect(x, y, width - 36 - 66, 34);
            if (LedgerSkin.TextButton(new Rect(x + width - 36 - 60, y, 54, 34), "rename", false, profile != null)) Queue(() => { _target = profile; _modal = "rename"; _name = profile.Name; }, false);
            if (LedgerSkin.Dropdown(anchor, profile != null ? profile.Name : "No loot options"))
            {
                // Anchor leaves scroll-local space so the popup can render over both sections.
                var global = anchor; global.position += new Vector2(26, 143 - _lootScroll.y);
                _dropdown.Open(global, profiles.Select(p => new KeyValuePair<string, string>(p.Id, p.Name)).ToList(), profile != null ? profile.Id : "", id => _lootSelected[category] = id, _rect.height - 84);
            }
            if (LedgerSkin.Icon(new Rect(x + width - 30, y + 2, 30, 30), "trash-2", profile != null, LedgerSkin.Danger)) ShowModal("deleteLoot", profile);
            y += 44;
            if (truck && profile != null)
            {
                LedgerSkin.Label(new Rect(x + 9, y, width - 165, 40), "Cargo selection chance · %", LedgerSkin.Small, LedgerSkin.Muted);
                profile.SpawnChance = LedgerSkin.Number(new Rect(x + width - 139, y + 3, 74, 34), profile.Id + ".cargoChance", profile.SpawnChance, 0, 100, Dirty, true);
                LedgerSkin.Label(new Rect(x + width - 56, y, 14, 40), "%"); y += 48;
                LedgerSkin.Label(new Rect(x + 9, y, 185, 34), "Truck bed texture", LedgerSkin.Small, LedgerSkin.Muted);
                var texture = new Rect(x + 204, y, width - 204, 34);
                if (LedgerSkin.Dropdown(texture, string.IsNullOrEmpty(profile.TextureKey) ? "None" : (profile.TextureKey.StartsWith("cargo_", StringComparison.OrdinalIgnoreCase) ? profile.TextureKey : Paint.CargoFile(profile.TextureKey)) + ".png"))
                {
                    var global = texture; global.position += new Vector2(26, 143 - _lootScroll.y);
                    _dropdown.Open(global, new[] { new KeyValuePair<string, string>("", "None") }.Concat(Paint.CargoTextures().Select(name => new KeyValuePair<string, string>(name, name + ".png"))).ToList(), string.IsNullOrEmpty(profile.TextureKey) ? "" : Paint.CargoFile(profile.TextureKey), value => { profile.TextureKey = value; Dirty(); }, _rect.height - 84);
                }
                y += 48;
            }
            if (profile != null) foreach (var item in profile.Items)
            {
                LedgerSkin.Rule(x, y + 45, width);
                LedgerSkin.Label(new Rect(x + 9, y, width - 335, 46), PickupCatalog.Name(item.Id));
                LedgerSkin.Label(new Rect(x + width - 323, y, 175, 46), LedgerSkin.Quantity(item.Chance), LedgerSkin.Small, LedgerSkin.Muted, TextAnchor.MiddleRight);
                item.Chance = LedgerSkin.Number(new Rect(x + width - 139, y + 6, 74, 34), profile.Id + "." + item.Id, item.Chance, 0, 100000, Dirty, true);
                LedgerSkin.Label(new Rect(x + width - 56, y, 14, 46), "%");
                if (LedgerSkin.Icon(new Rect(x + width - 30, y + 8, 30, 30), "circle-minus")) Queue(() => profile.Items.Remove(item)); y += 46;
            }
            if (profile == null || profile.Items.Count == 0) { LedgerSkin.Label(new Rect(x, y, width, 46), "No items. This option spawns no loot.", LedgerSkin.Small, LedgerSkin.Muted); y += 46; }
            if (LedgerSkin.Button(new Rect(x, y, 157, 34), "+  Add game item", profile != null)) Queue(() => { _target = profile; _search = _item = ""; _itemChance = 100; _modalScroll = Vector2.zero; LedgerSkin.ClearNumbers(); _modal = "items"; PickupCatalog.All(); }, false);
            LedgerSkin.Label(new Rect(x, y + 44, width, 20), "Absolute chance per item: 100% = 1 · 150% = 1 + 50% for a second · 200% = 2", LedgerSkin.Small, LedgerSkin.Muted);
        }
        private void DrawLedgerModal()
        {
            LedgerSkin.Fill(_rect, new Color(0, 0, 0, .6f));
            if (_modal == "allowedCargo") { DrawAllowedCargo(); return; }
            int boxH = _modal == "items" ? 468 : _modal == "editTemplate" ? 484 : 330;
            var box = new Rect((_rect.width - 540) / 2, (_rect.height - boxH) / 2, 540, boxH);
            LedgerSkin.Panel(box); float x = box.x + 22, w = box.width - 44;
            if (_modal == "items") { DrawLedgerItems(box); return; }
            if (_modal == "editTemplate") { DrawEditTemplate(box); return; }
            string title = _modal == "reset" ? "Reset to defaults?" : _modal == "newGroup" ? "New " + (_tab == 1 ? "convoy" : "patrol") : _modal == "newLoot" ? "New loot option" : _modal == "rename" ? "Rename" : "Delete?";
            LedgerSkin.Label(new Rect(x, box.y + 22, w, 26), title, LedgerSkin.DialogTitle);
            if (_modal == "reset")
            {
                LedgerSkin.Label(new Rect(x, box.y + 66, w, 24), "Restore shipped patrols, convoys, templates and loot settings.", LedgerSkin.Small);
                LedgerSkin.Label(new Rect(x, box.y + 96, w, 24), "BaseTemplates are backed up to OldTemplatesFolder.", LedgerSkin.Small, LedgerSkin.Muted);
                _deleteCustom = LedgerSkin.Toggle(new Rect(x, box.y + 146, w, 34), _deleteCustom, "Do you want to delete your custom templates?");
                LedgerSkin.Label(new Rect(x, box.y + 186, w, 40), _deleteCustom ? "Custom templates, including favourites, move to the Recycle Bin." : "Your custom templates and favourites will be kept.", LedgerSkin.Small, LedgerSkin.Muted);
                ModalButtons(box, "Reset", () => { EditorStore.Reset(_deleteCustom); LedgerSkin.ClearNumbers(); EnsureSelection(); _status = "Defaults restored; BaseTemplates backed up."; });
            }
            else if (_modal == "rename")
            {
                LedgerSkin.Label(new Rect(x, box.y + 65, w, 22), "Name", LedgerSkin.Small, LedgerSkin.Muted);
                _name = GUI.TextField(new Rect(x, box.y + 92, w, 34), _name, LedgerSkin.Input);
                LedgerSkin.Label(new Rect(x, box.y + 145, w, 44), _target is LootProfile ? "Templates and convoys keep referring to this loot option by its id; only the shown name changes." : "The roster and settings stay as they are.", LedgerSkin.Small, LedgerSkin.Muted);
                ModalButtons(box, "Rename", () =>
                {
                    string name = _name.Trim(); if (name.Length == 0) throw new ArgumentException("Enter a name");
                    var lp = _target as LootProfile; var sg = _target as SpawnGroup;
                    if (lp != null) { var list = EditorStore.Data.TruckLoot.Contains(lp) ? EditorStore.Data.TruckLoot : EditorStore.Data.CarLoot; if (list.Any(o => o != lp && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That loot name exists"); lp.Name = name; }
                    else if (sg != null) { if (Groups.Any(o => o != sg && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That group name exists"); sg.Name = name; }
                }, _name.Trim().Length > 0);
            }
            else if (_modal == "newGroup" || _modal == "newLoot")
            {
                LedgerSkin.Label(new Rect(x, box.y + 65, w, 22), "Name", LedgerSkin.Small, LedgerSkin.Muted);
                _name = GUI.TextField(new Rect(x, box.y + 92, w, 34), _name, LedgerSkin.Input);
                LedgerSkin.Label(new Rect(x, box.y + 145, w, 44), _modal == "newLoot" ? "Add any game pickup item and configure its absolute chance." : "Add one roster entry per vehicle; repeats create repeated vehicles.", LedgerSkin.Small, LedgerSkin.Muted);
                ModalButtons(box, "Create", () =>
                {
                    string name = _name.Trim(); if (name.Length == 0) throw new ArgumentException("Enter a name");
                    if (_target is List<LootProfile>) { var list = (List<LootProfile>)_target; if (list.Any(lp => lp.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That loot name exists"); var p = new LootProfile { Id = Guid.NewGuid().ToString("N"), Name = name }; list.Add(p); _lootSelected[ReferenceEquals(list, EditorStore.Data.TruckLoot) ? 1 : 0] = p.Id; }
                    else { if (Groups.Any(sg => sg.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That group name exists"); var g = new SpawnGroup { Id = Guid.NewGuid().ToString("N"), Name = name, Chance = 10 }; Groups.Add(g); _selectedGroup = EditorSelection.Select(_expanded, g.Id, true); }
                }, _name.Trim().Length > 0);
            }
            else
            {
                LedgerSkin.Label(new Rect(x, box.y + 68, w, 44), _target is CarTemplate ? "Move this custom template to the Recycle Bin?" : _target is LootProfile ? "Delete this loot option? Referencing templates will use no loot." : "Delete this group and its roster? Template files are kept.", LedgerSkin.Small);
                ModalButtons(box, "Delete", () =>
                {
                    if (_target is CarTemplate) CarTemplates.Delete((CarTemplate)_target);
                    else if (_target is SpawnGroup) { Groups.Remove((SpawnGroup)_target); EnsureSelection(); }
                    else if (_target is LootProfile)
                    {
                        var p = (LootProfile)_target; EditorStore.Data.CarLoot.Remove(p); EditorStore.Data.TruckLoot.Remove(p);
                        foreach (var group in EditorStore.Data.Convoys) group.AllowedCargo.RemoveAll(id => id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
                        foreach (var t in CarTemplate.Park.Where(t => string.Equals(t.LootPreset, p.Id, StringComparison.OrdinalIgnoreCase))) { t.LootPreset = ""; if (System.IO.File.Exists(t.SourcePath)) { var file = TemplateFile.FromJson(System.IO.File.ReadAllText(t.SourcePath)); file.lootPreset = ""; EditorStore.AtomicWrite(t.SourcePath, file.ToJson()); } }
                    }
                });
            }
        }
        // the "E" of a template row: driver, passenger and the loot option (cars) / a fixed cargo option (trucks); written into the template file
        private void DrawEditTemplate(Rect box)
        {
            var t = (CarTemplate)_target; float x = box.x + 22, w = box.width - 44, input = x + 150, iw = w - 150, bottom = _rect.height - 84;
            LedgerSkin.Label(new Rect(x, box.y + 22, w, 26), "Edit template · " + t.Name.Replace('_', ' '), LedgerSkin.DialogTitle);
            LedgerSkin.Label(new Rect(x, box.y + 65, 140, 34), "Driver");
            var a = new Rect(input, box.y + 65, iw, 34);
            if (LedgerSkin.Dropdown(a, _editDriver)) _dropdown.Open(a, Options(People), _editDriver, v => _editDriver = v, bottom);
            LedgerSkin.Label(new Rect(x, box.y + 112, 140, 34), "Passenger");
            a = new Rect(input, box.y + 112, iw, 34);
            if (LedgerSkin.Dropdown(a, _editPassenger)) _dropdown.Open(a, Options(People), _editPassenger, v => _editPassenger = v, bottom);
            bool bike = MotorcycleIntegration.IsBody(t.Body);
            LedgerSkin.Label(new Rect(x, box.y + 159, 140, 34), t.IsTruck ? "Truck cargo" : "Car loot");
            a = new Rect(input, box.y + 159, iw, 34);
            if (bike) LedgerSkin.Label(a, "None - a motorcycle carries no loot", null, LedgerSkin.Muted);
            else if (t.IsTruck)
            {
                var current = EditorStore.Loot(true, _editLoot);
                if (LedgerSkin.Dropdown(a, current != null ? current.Name : "Convoy cargo (rolled per convoy)"))
                    _dropdown.Open(a, new[] { new KeyValuePair<string, string>("", "Convoy cargo (rolled per convoy)") }.Concat(EditorStore.Data.TruckLoot.Select(p => new KeyValuePair<string, string>(p.Id, p.Name))).ToList(), current != null ? current.Id : "", v => _editLoot = v, bottom);
            }
            else
            {
                var current = EditorStore.Loot(false, _editLoot);
                if (LedgerSkin.Dropdown(a, current != null ? current.Name : "None"))
                    _dropdown.Open(a, new[] { new KeyValuePair<string, string>("", "None") }.Concat(EditorStore.Data.CarLoot.Select(p => new KeyValuePair<string, string>(p.Id, p.Name))).ToList(), current != null ? current.Id : "", v => _editLoot = v, bottom);
            }
            // human turrets: 3 on a car's roof, 1 on a motorcycle, none on a truck
            int slots = Rider.Slots(t.Body, t.IsTruck, t.IsMotorcycle);
            for (int i = 0; i < 3; i++)
            {
                float yt = box.y + 206 + 47 * i; int k = i;
                LedgerSkin.Label(new Rect(x, yt, 140, 34), "Human turret " + (i + 1));
                a = new Rect(input, yt, iw, 34);
                if (i >= slots) LedgerSkin.Label(a, slots == 0 ? "None - not on trucks" : "None - a motorcycle takes one", null, LedgerSkin.Muted);
                else if (LedgerSkin.Dropdown(a, _editTurrets[i])) _dropdown.Open(a, Options(Rider.TurretChoices), _editTurrets[i], v => _editTurrets[k] = v, bottom);
            }
            LedgerSkin.Label(new Rect(x, box.y + 346, w, 50), (t.IsTruck ? "A fixed cargo option always loads this truck with it; otherwise the convoy's allowed cargo is rolled. " : "Written into the template file; cars spawned from now on use it. ") + (slots > 0 ? "Warboy = Scraffa with blast lances; a gunner crouches on the roof (sits on a motorcycle) and shoots all around. Spots: [Debug] TurretSpotEditor." : ""), LedgerSkin.Small, LedgerSkin.Muted);
            ModalButtons(box, "Save", () =>
            {
                var turrets = new string[3];
                for (int i = 0; i < 3; i++) turrets[i] = i < slots ? _editTurrets[i] : "";
                SaveTemplateEdit(t, _editDriver, _editPassenger, bike ? "" : _editLoot, turrets); _dropdown.Close();
            });
        }
        private void DrawLedgerItems(Rect box)
        {
            var profile = (LootProfile)_target; float x = box.x + 22, w = box.width - 44;
            LedgerSkin.Label(new Rect(x, box.y + 22, w, 26), "Add game pickup item", LedgerSkin.DialogTitle);
            LedgerSkin.Label(new Rect(x, box.y + 63, w, 20), "Find item", LedgerSkin.Small, LedgerSkin.Muted);
            _search = GUI.TextField(new Rect(x, box.y + 87, w, 34), _search, LedgerSkin.Input);
            var items = PickupCatalog.All().Where(i => !profile.Items.Any(row => string.Equals(row.Id, i.Id, StringComparison.OrdinalIgnoreCase)) && (i.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 || i.Id.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            var list = new Rect(x, box.y + 133, w, 180); LedgerSkin.Panel(list);
            _modalScroll = LedgerSkin.Scroll(new Rect(list.x + 4, list.y + 4, list.width - 8, list.height - 8), _modalScroll, items.Length * 34, width =>
            {
                for (int i = 0; i < items.Length; i++) { var item = items[i]; var row = new Rect(0, i * 34, width, 34); bool selected = _item == item.Id; if (selected || row.Contains(Event.current.mousePosition)) LedgerSkin.Fill(row, LedgerSkin.Selected); bool hit = GUI.Button(row, GUIContent.none, GUIStyle.none); LedgerSkin.ButtonLabel(new Rect(9, row.y, width - 18, 34), item.Name, LedgerSkin.Body, selected ? LedgerSkin.Accent : LedgerSkin.Text, TextAnchor.MiddleLeft); if (hit) Queue(() => _item = item.Id, false); }
            });
            LedgerSkin.Label(new Rect(x, box.y + 331, 140, 20), "Absolute chance · %", LedgerSkin.Small, LedgerSkin.Muted);
            _itemChance = LedgerSkin.Number(new Rect(x, box.y + 356, 140, 34), "newItemChance", _itemChance, 0, 100000, () => { });
            LedgerSkin.Label(new Rect(x + 158, box.y + 356, w - 158, 34), LedgerSkin.Quantity(_itemChance), LedgerSkin.Small, LedgerSkin.Muted);
            ModalButtons(box, "Add item", () => { profile.Items.Add(new LootItem { Id = _item, Chance = _itemChance }); }, _item.Length > 0);
        }
        private void ModalButtons(Rect box, string confirm, Action action, bool enabled = true)
        {
            LedgerSkin.Rule(box.x + 22, box.yMax - 73, box.width - 44);
            if (LedgerSkin.Button(new Rect(box.xMax - 219, box.yMax - 55, 86, 34), "Cancel")) Queue(() => _modal = "", false);
            if (LedgerSkin.Button(new Rect(box.xMax - 125, box.yMax - 55, 103, 34), confirm, enabled, true)) Queue(() => { action(); _modal = ""; });
        }
        private void DrawAllowedCargo()
        {
            var group = (SpawnGroup)_target; var box = new Rect((_rect.width - 850) / 2, 135, 850, 474); LedgerSkin.Panel(box);
            LedgerSkin.Label(new Rect(box.x + 22, box.y + 20, box.width - 44, 26), "Allowed cargo · " + group.Name, LedgerSkin.DialogTitle);
            float x = box.x + 22, right = box.center.x + 8, width = box.width / 2 - 38;
            LedgerSkin.Label(new Rect(x, box.y + 68, width, 24), "Allowed cargo", LedgerSkin.Heading);
            LedgerSkin.Label(new Rect(right, box.y + 68, width, 24), "Convoy loot options", LedgerSkin.Heading);
            var leftBox = new Rect(x, box.y + 103, width, 276); var rightBox = new Rect(right, box.y + 103, width, 276); LedgerSkin.Panel(leftBox); LedgerSkin.Panel(rightBox);
            _allowedScroll = LedgerSkin.Scroll(new Rect(leftBox.x + 1, leftBox.y + 1, width - 2, 274), _allowedScroll, group.AllowedCargo.Count * 39, w =>
            {
                for (int i = 0; i < group.AllowedCargo.Count; i++)
                {
                    string id = group.AllowedCargo[i]; var profile = EditorStore.Loot(true, id); float y = i * 39;
                    LedgerSkin.ButtonLabel(new Rect(10, y, w - 50, 39), profile != null ? profile.Name : id, LedgerSkin.Body, LedgerSkin.Text, TextAnchor.MiddleLeft);
                    if (LedgerSkin.Icon(new Rect(w - 31, y + 4, 30, 30), "circle-minus")) Queue(() => group.AllowedCargo.Remove(id));
                    LedgerSkin.Rule(0, y + 38, w);
                }
            });
            _cargoLibraryScroll = LedgerSkin.Scroll(new Rect(rightBox.x + 1, rightBox.y + 1, width - 2, 274), _cargoLibraryScroll, EditorStore.Data.TruckLoot.Count * 39, w =>
            {
                for (int i = 0; i < EditorStore.Data.TruckLoot.Count; i++)
                {
                    var profile = EditorStore.Data.TruckLoot[i]; var row = new Rect(0, i * 39, w, 39); if (_cargoChoice == profile.Id) LedgerSkin.Fill(row, LedgerSkin.Selected);
                    bool hit = GUI.Button(row, GUIContent.none, GUIStyle.none); LedgerSkin.ButtonLabel(new Rect(10, row.y, w - 20, 39), profile.Name, LedgerSkin.Body, _cargoChoice == profile.Id ? LedgerSkin.Accent : LedgerSkin.Text, TextAnchor.MiddleLeft);
                    if (hit) Queue(() => _cargoChoice = profile.Id, false); LedgerSkin.Rule(0, row.yMax - 1, w);
                }
            });
            LedgerSkin.Rule(box.x + 22, box.yMax - 73, box.width - 44);
            if (LedgerSkin.Button(new Rect(x, box.yMax - 55, 92, 34), "Close")) Queue(() => _modal = "", false);
            if (LedgerSkin.Button(new Rect(right, box.yMax - 55, width, 34), "+  Add cargo", _cargoChoice.Length > 0 && !group.AllowedCargo.Contains(_cargoChoice), true)) Queue(() => group.AllowedCargo.Add(_cargoChoice));
        }
    }
}

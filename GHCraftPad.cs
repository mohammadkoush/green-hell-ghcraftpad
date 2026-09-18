// GHCraftPad - craft from the boxes around you, off a pad beside the crafting table.
//
// THE GAP. Green Hell's crafting table has no "choose a recipe" step: you drop items on it and it
// shows what they make. Everything has to be in your backpack first, so crafting at camp means
// walking to each box, taking things out, walking back. No mod on Nexus or the modding hubs changes
// that; the closest, "Utility" by franerd, pulls loose materials off the ground to you.
//
// WHAT THIS DOES. When the table opens, a pad appears beside it listing every recipe the game
// knows, with what you can make right now from three places counted together: what is already on
// the table, what is in your backpack, and what is in every storage box within a radius of you.
// Roll the mouse wheel over a recipe to choose how many; click it, and the ingredients are pulled
// onto the table - table first, then backpack, then the nearest boxes - and the game's own crafting
// starts. Nothing here makes an item: the game makes it, exactly as if you had dragged the parts.
//
// NEVER A DUPLICATE, and checked, not hoped: every pull moves the SAME Item object - out of its
// box or backpack, onto the table - never a copy, never CreateItem. After every pull the count of
// that item across table + backpack + boxes is compared with the count before, and any difference
// is written to the log as an error. The game's own Craft consumes the objects on the table.
//
// WHAT GOES BACK. Every borrowed item remembers its box. Close the table without crafting and the
// borrowed items still on the table go back where they came from, not into your backpack. An item
// you moved into your backpack by hand is yours and stays; an item the craft used is gone. Nothing
// is ever taken out of your backpack.
//
// A CRAFTED ITEM THAT DOES NOT FIT goes on the ground in front of you. The game's own Craft puts an
// item that will not fit back ON THE TABLE, which is easy to miss; a postfix moves it to your feet.
//
// Every class and call used here is public and was read out of Assembly-CSharp before a line was
// written: CraftingManager (m_Items, AddItem, RemoveItem, StartCrafting, m_WantedResultsCount),
// Storage (s_AllStorages, m_Items, RemoveItem, InsertItem), ItemsManager (GetAllInfos,
// m_CraftingLockedItems), ItemInfo (m_Craftable, m_Components).
//
// Language level is C# 5 (stock Framework csc.exe) - no ?., no $"", no ??=.

using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GHCraftPad
{
    [BepInPlugin(Guid, Name, Version)]
    public class GHCraftPadPlugin : BaseUnityPlugin
    {
        public const string Guid    = "com.mohammadkoush.ghcraftpad";
        public const string Name    = "GHCraftPad";
        public const string Version = "1.0.0";

        private static GHCraftPadPlugin s_Self;

        private ConfigEntry<float>  _radius;
        private ConfigEntry<bool>   _showLocked;
        private ConfigEntry<bool>   _groundIfNoRoom;
        private ConfigEntry<string> _padPos;

        // ---- a recipe, as the pad sees it -------------------------------------------------------
        private class Line
        {
            public Enums.ItemID Result;
            public string Label;
            public Dictionary<int, int> Need;      // ItemID -> count per craft
            public int CanMake;                    // from table + backpack + boxes
            public int FromBoxes;                  // how many ingredient units would come from boxes
            public string Missing;                 // "2 Rope" when CanMake == 0
            public int Count = 1;                  // what the wheel dialled
        }

        private readonly List<Line> _lines = new List<Line>();
        private float _linesAt;
        private int _boxesInRange;

        // Borrowed: item -> the box it came from. Only these ever go back.
        private readonly Dictionary<Item, Storage> _borrowed = new Dictionary<Item, Storage>();

        // ---- pad ----------------------------------------------------------------------------------
        private bool _padOpen;
        private Rect _rect = new Rect(60f, 120f, 520f, 640f);
        private Vector2 _scroll;
        private GUIStyle _title, _row, _rowDim, _small, _btn, _btnDim;
        private bool _styled;
        private Texture2D _pixel, _hover;
        private string _notice = "";
        private float _noticeUntil;

        private void Awake()
        {
            s_Self = this;
            _radius = Config.Bind("Pad", "BoxRadiusMetres", 15f,
                new ConfigDescription("How far around you the pad looks for storage boxes. A slider " +
                    "at the top of the pad.", new AcceptableValueRange<float>(3f, 100f)));
            _showLocked = Config.Bind("Pad", "ShowRecipesNotYetLearned", false,
                "List recipes the game has not unlocked for you yet, greyed. Off: only what you know.");
            _groundIfNoRoom = Config.Bind("Pad", "CraftedItemToGroundIfNoRoom", true,
                "A crafted item that does not fit in your backpack is dropped on the ground in " +
                "front of you. Off: the game's own behaviour, which leaves it on the table.");
            _padPos = Config.Bind("Pad", "PadPosition", "", "Where the pad was last dragged. Written automatically.");
            LoadPos();

            try
            {
                Harmony h = new Harmony(Guid);
                h.PatchAll(typeof(GHCraftPadPlugin).Assembly);
                Logger.LogInfo(Name + " " + Version + " loaded. Open the crafting table.");
            }
            catch (Exception ex) { Logger.LogError("could not patch the crafting table: " + ex.Message); }
        }

        // -----------------------------------------------------------------------------------------
        // The table opening and closing
        // -----------------------------------------------------------------------------------------

        [HarmonyPatch(typeof(CraftingManager), "Activate")]
        private static class Patch_Activate
        {
            private static void Postfix()
            {
                if (s_Self == null) return;
                s_Self._padOpen = true;
                s_Self._linesAt = 0f;
            }
        }

        // BEFORE the game empties the table into the backpack: borrowed items go home first.
        [HarmonyPatch(typeof(CraftingManager), "Deactivate")]
        private static class Patch_Deactivate
        {
            private static void Prefix(CraftingManager __instance)
            {
                if (s_Self == null) return;
                try { s_Self.ReturnBorrowed(__instance); }
                catch (Exception ex) { s_Self.Logger.LogWarning("returning borrowed items: " + ex.Message); }
                s_Self._padOpen = false;
            }
        }

        // The game's Craft puts a result that will not fit back on the table. His rule: the ground,
        // in front of him.
        [HarmonyPatch(typeof(CraftingManager), "Craft")]
        private static class Patch_Craft
        {
            private static void Postfix(CraftingManager __instance, Item __result)
            {
                if (s_Self == null || __result == null) return;
                try
                {
                    // Consumed ingredients are gone; forget any borrowed ones the craft used.
                    s_Self.ForgetGone();
                    s_Self._linesAt = 0f;

                    if (!s_Self._groundIfNoRoom.Value) return;
                    if (!__result.m_OnCraftingTable) return;          // it fitted; nothing to do

                    __instance.RemoveItem(__result, false, true);
                    Player p = Player.Get();
                    if (p == null) return;
                    Vector3 pos = p.transform.position + p.transform.forward * 1.5f + Vector3.up * 0.4f;
                    __result.transform.position = pos;
                    __result.transform.rotation = p.transform.rotation;
                    try { __result.UpdatePhx(); } catch (Exception) { }
                    s_Self.Logger.LogInfo("crafted " + __result.m_Info.m_ID + " did not fit the backpack - dropped in front of you");
                    s_Self.Say(Pretty(__result.m_Info.m_ID) + " did not fit - it is on the ground in front of you");
                }
                catch (Exception ex) { s_Self.Logger.LogWarning("crafted item to ground: " + ex.Message); }
            }
        }

        private void ReturnBorrowed(CraftingManager cm)
        {
            if (_borrowed.Count == 0) return;
            int home = 0, dropped = 0;
            List<Item> keys = new List<Item>(_borrowed.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                Item it = keys[i];
                Storage box = _borrowed[it];
                if (it == null) continue;
                // Only what is still on the table. Moved by hand into the backpack = his now.
                if (cm.m_Items == null || !cm.m_Items.Contains(it)) continue;
                cm.RemoveItem(it, false, false);
                InsertResult r = InsertResult.Ok;
                if (box != null) r = box.InsertItem(it, null, null, false, true, false);   // drop if the box is full
                if (box != null && r == InsertResult.Ok) home++; else dropped++;
            }
            _borrowed.Clear();
            if (home + dropped > 0)
                Logger.LogInfo("table closed: " + home + " borrowed item(s) went back to their boxes"
                               + (dropped > 0 ? ", " + dropped + " had no room and lie beside the table" : ""));
        }

        private void ForgetGone()
        {
            List<Item> gone = null;
            foreach (KeyValuePair<Item, Storage> kv in _borrowed)
                if (kv.Key == null) { if (gone == null) gone = new List<Item>(); gone.Add(kv.Key); }
            if (gone != null) foreach (Item g in gone) _borrowed.Remove(g);
        }

        // -----------------------------------------------------------------------------------------
        // Counting: table + backpack + boxes in range
        // -----------------------------------------------------------------------------------------

        private List<Storage> BoxesInRange()
        {
            List<Storage> outl = new List<Storage>();
            Player p = Player.Get();
            if (p == null || Storage.s_AllStorages == null) return outl;
            float r2 = _radius.Value * _radius.Value;
            Vector3 me = p.transform.position;
            for (int i = 0; i < Storage.s_AllStorages.Count; i++)
            {
                Storage s = Storage.s_AllStorages[i];
                if (s == null || s.m_Items == null) continue;
                if ((s.transform.position - me).sqrMagnitude > r2) continue;
                outl.Add(s);
            }
            // Nearest first, so a pull empties the box beside him before the one across camp.
            outl.Sort(delegate (Storage a, Storage b)
            {
                return (a.transform.position - me).sqrMagnitude.CompareTo((b.transform.position - me).sqrMagnitude);
            });
            return outl;
        }

        private static void CountInto(Dictionary<int, int> tally, List<Item> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it == null || it.m_Info == null) continue;
                int id = (int)it.m_Info.m_ID;
                int n; tally.TryGetValue(id, out n); tally[id] = n + 1;
            }
        }

        private void RebuildLines()
        {
            _lines.Clear();
            CraftingManager cm = CraftingManager.Get();
            InventoryBackpack bp = InventoryBackpack.Get();
            ItemsManager im = ItemsManager.Get();
            if (cm == null || bp == null || im == null) return;

            List<Storage> boxes = BoxesInRange();
            _boxesInRange = boxes.Count;

            Dictionary<int, int> have = new Dictionary<int, int>();
            CountInto(have, cm.m_Items);
            CountInto(have, bp.m_Items);
            Dictionary<int, int> inBoxes = new Dictionary<int, int>();
            for (int i = 0; i < boxes.Count; i++) CountInto(inBoxes, boxes[i].m_Items);
            foreach (KeyValuePair<int, int> kv in inBoxes) { int n; have.TryGetValue(kv.Key, out n); have[kv.Key] = n + kv.Value; }

            Dictionary<int, ItemInfo> all = im.GetAllInfos();
            if (all == null) return;
            foreach (KeyValuePair<int, ItemInfo> kv in all)
            {
                ItemInfo info = kv.Value;
                if (info == null || !info.m_Craftable) continue;
                Dictionary<int, int> comps = info.m_Components;
                if (comps == null || comps.Count == 0) continue;
                bool locked = im.m_CraftingLockedItems != null && im.m_CraftingLockedItems.Contains(info.m_ID);
                if (locked && !_showLocked.Value) continue;

                Line ln = new Line();
                ln.Result = info.m_ID;
                ln.Label = Pretty(info.m_ID) + (locked ? "  (not learned yet)" : "");
                ln.Need = comps;
                int can = int.MaxValue;
                string missing = null;
                int fromBoxes = 0;
                foreach (KeyValuePair<int, int> c in comps)
                {
                    int h; have.TryGetValue(c.Key, out h);
                    int b; inBoxes.TryGetValue(c.Key, out b);
                    int per = Mathf.Max(1, c.Value);
                    can = Mathf.Min(can, h / per);
                    if (h < per && missing == null) missing = (per - h) + " " + Pretty((Enums.ItemID)c.Key);
                    fromBoxes += Mathf.Min(b, per);
                }
                ln.CanMake = locked ? 0 : (can == int.MaxValue ? 0 : can);
                ln.FromBoxes = fromBoxes;
                ln.Missing = missing;
                if (ln.Count > ln.CanMake) ln.Count = Mathf.Max(1, ln.CanMake);
                _lines.Add(ln);
            }
            _lines.Sort(delegate (Line a, Line b)
            {
                if ((a.CanMake > 0) != (b.CanMake > 0)) return a.CanMake > 0 ? -1 : 1;
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
        }

        // -----------------------------------------------------------------------------------------
        // The pull, and the craft
        // -----------------------------------------------------------------------------------------

        private void PullAndCraft(Line ln)
        {
            CraftingManager cm = CraftingManager.Get();
            InventoryBackpack bp = InventoryBackpack.Get();
            if (cm == null || bp == null) return;
            int n = Mathf.Clamp(ln.Count, 1, Mathf.Max(1, ln.CanMake));
            List<Storage> boxes = BoxesInRange();

            // Conservation, before.
            Dictionary<int, int> before = Census(cm, bp, boxes);

            int pulledPack = 0, pulledBox = 0;
            foreach (KeyValuePair<int, int> c in ln.Need)
            {
                Enums.ItemID id = (Enums.ItemID)c.Key;
                int want = c.Value * n;

                // Already on the table.
                for (int i = 0; i < cm.m_Items.Count && want > 0; i++)
                    if (cm.m_Items[i] != null && cm.m_Items[i].m_Info != null && cm.m_Items[i].m_Info.m_ID == id) want--;

                // Then the backpack - the same move a drag makes.
                if (want > 0)
                {
                    List<Item> take = new List<Item>();
                    for (int i = 0; i < bp.m_Items.Count && take.Count < want; i++)
                    {
                        Item it = bp.m_Items[i];
                        if (it != null && it.m_Info != null && it.m_Info.m_ID == id && cm.CanAddItem(it)) take.Add(it);
                    }
                    for (int i = 0; i < take.Count; i++)
                    {
                        bp.RemoveItem(take[i], false);
                        cm.AddItem(take[i], true, true);
                        pulledPack++; want--;
                    }
                }

                // Then the boxes, nearest first. The SAME object moves: out of the box, onto the table.
                for (int b = 0; b < boxes.Count && want > 0; b++)
                {
                    Storage box = boxes[b];
                    List<Item> take = new List<Item>();
                    for (int i = 0; i < box.m_Items.Count && take.Count < want; i++)
                    {
                        Item it = box.m_Items[i];
                        if (it != null && it.m_Info != null && it.m_Info.m_ID == id && cm.CanAddItem(it)) take.Add(it);
                    }
                    for (int i = 0; i < take.Count; i++)
                    {
                        box.RemoveItem(take[i], false);
                        cm.AddItem(take[i], true, true);
                        _borrowed[take[i]] = box;
                        pulledBox++; want--;
                    }
                }

                if (want > 0)
                {
                    Logger.LogWarning("pull: still short " + want + " x " + id + " for " + ln.Result + " - the table has what could be found; craft what it offers");
                    Say("Short " + want + " " + Pretty(id) + " - pulled what there was");
                }
            }

            // Conservation, after: every id must total the same across the three places.
            Dictionary<int, int> after = Census(cm, bp, boxes);
            foreach (KeyValuePair<int, int> kv in before)
            {
                int a; after.TryGetValue(kv.Key, out a);
                if (a != kv.Value)
                    Logger.LogError("DUPLICATION CHECK FAILED: " + (Enums.ItemID)kv.Key + " was " + kv.Value + " across table+backpack+boxes, now " + a);
            }

            // The game crafts. AddItem resets the wanted count to 1 each time, so it is set last.
            cm.m_WantedResultsCount = n;
            try { cm.StartCrafting(ln.Result, false); }
            catch (Exception ex) { Logger.LogWarning("StartCrafting: " + ex.Message + " - the parts are on the table, press the game's craft button"); }

            Logger.LogInfo("pad: " + n + " x " + ln.Result + " - pulled " + pulledPack + " from the backpack, " + pulledBox + " from " + boxes.Count + " box(es)");
            Say("Making " + n + " x " + Pretty(ln.Result) + (pulledBox > 0 ? " - " + pulledBox + " part(s) from your boxes" : ""));
            _linesAt = 0f;
        }

        private static Dictionary<int, int> Census(CraftingManager cm, InventoryBackpack bp, List<Storage> boxes)
        {
            Dictionary<int, int> t = new Dictionary<int, int>();
            CountInto(t, cm.m_Items);
            CountInto(t, bp.m_Items);
            for (int i = 0; i < boxes.Count; i++) CountInto(t, boxes[i].m_Items);
            return t;
        }

        // -----------------------------------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------------------------------

        private void Update()
        {
            try
            {
                if (!_padOpen) return;
                CraftingManager cm = CraftingManager.Get();
                if (cm == null || !cm.gameObject.activeSelf) { _padOpen = false; return; }
                if (Time.realtimeSinceStartup - _linesAt > 1.5f) { _linesAt = Time.realtimeSinceStartup; RebuildLines(); }
            }
            catch (Exception ex) { Logger.LogWarning("pad update: " + ex.Message); }
        }

        private void OnGUI()
        {
            try
            {
                BuildStyles();
                if (_notice.Length > 0 && Time.realtimeSinceStartup < _noticeUntil)
                {
                    GUIContent c = new GUIContent(_notice);
                    Vector2 sz = _row.CalcSize(c);
                    Rect r = new Rect((Screen.width - sz.x) * 0.5f - 10f, Screen.height * 0.86f, sz.x + 20f, sz.y + 8f);
                    GUI.DrawTexture(r, _pixel);
                    GUI.Label(new Rect(r.x + 10f, r.y + 4f, sz.x, sz.y), c, _row);
                }
                if (!_padOpen) return;
                _rect = GUI.Window(0x6A0D20, _rect, DrawPad, "");
            }
            catch (Exception ex) { Logger.LogWarning("pad: " + ex.Message); }
        }

        private void DrawPad(int id)
        {
            GUI.DrawTexture(new Rect(0f, 0f, _rect.width, _rect.height), _pixel);
            GUILayout.BeginVertical();
            GUILayout.Label("Crafting pad", _title);
            GUILayout.Label(_boxesInRange + " box" + (_boxesInRange == 1 ? "" : "es") + " within "
                            + Mathf.RoundToInt(_radius.Value) + " m   -   wheel: how many, click: make", _small);

            GUILayout.BeginHorizontal();
            GUILayout.Label("  Boxes within", _small, GUILayout.Width(110f));
            float rv = GUILayout.HorizontalSlider(_radius.Value, 3f, 100f);
            if (Mathf.Abs(rv - _radius.Value) > 0.01f) { _radius.Value = rv; _linesAt = 0f; }
            GUILayout.Label(Mathf.RoundToInt(_radius.Value) + " m", _small, GUILayout.Width(50f));
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll, false, true);
            if (_lines.Count == 0) GUILayout.Label("Nothing to list yet.", _rowDim);
            for (int i = 0; i < _lines.Count; i++)
            {
                Line ln = _lines[i];
                bool can = ln.CanMake > 0;
                string right = can ? (ln.Count + " of " + ln.CanMake) : ("need " + ln.Missing);
                string text = "  " + ln.Label + "    " + right + (can && ln.FromBoxes > 0 ? "   (" + ln.FromBoxes + " from boxes)" : "");
                bool clicked = GUILayout.Button(text, can ? _btn : _btnDim, GUILayout.Height(30f));
                Rect rr = GUILayoutUtility.GetLastRect();

                // The wheel over a row dials how many. ScrollWheel arrives as its own event.
                Event e = Event.current;
                if (can && e.type == EventType.ScrollWheel && rr.Contains(e.mousePosition))
                {
                    ln.Count = Mathf.Clamp(ln.Count - (int)Mathf.Sign(e.delta.y), 1, ln.CanMake);
                    e.Use();
                }
                if (clicked && can)
                {
                    try { PullAndCraft(ln); }
                    catch (Exception ex) { Logger.LogWarning("pull: " + ex.Message); Say("Could not pull - see the log"); }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.Label("Borrowed parts go back to their box if you close the table without crafting.", _small);
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 28f));
            string v = Mathf.RoundToInt(_rect.x) + "," + Mathf.RoundToInt(_rect.y);
            if (v != _padPos.Value) _padPos.Value = v;
        }

        private void Say(string text)
        {
            _notice = text;
            _noticeUntil = Time.realtimeSinceStartup + 4f;
        }

        private static string Pretty(Enums.ItemID id)
        {
            string key = id.ToString();
            try
            {
                string loc = GreenHellGame.Instance.GetLocalization().Get(key, false);
                if (!string.IsNullOrEmpty(loc) && loc.IndexOf("MISSING", StringComparison.Ordinal) < 0) return loc;
            }
            catch (Exception) { }
            return key.Replace('_', ' ');
        }

        private void BuildStyles()
        {
            if (_styled) return;
            _styled = true;
            _pixel = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _pixel.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.10f, 0.92f)); _pixel.Apply();
            _hover = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _hover.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.08f)); _hover.Apply();

            _title = new GUIStyle(GUI.skin.label); _title.fontSize = 18; _title.fontStyle = FontStyle.Bold;
            _title.normal.textColor = new Color(0.96f, 0.97f, 1f);
            _row = new GUIStyle(GUI.skin.label); _row.fontSize = 15; _row.alignment = TextAnchor.MiddleLeft;
            _row.normal.textColor = new Color(0.92f, 0.93f, 0.95f);
            _rowDim = new GUIStyle(_row); _rowDim.normal.textColor = new Color(0.55f, 0.57f, 0.62f);
            _small = new GUIStyle(_row); _small.fontSize = 12; _small.normal.textColor = new Color(0.7f, 0.72f, 0.76f);

            _btn = new GUIStyle(GUI.skin.button); _btn.fontSize = 15; _btn.alignment = TextAnchor.MiddleLeft;
            _btn.normal.background = null; _btn.active.background = null; _btn.focused.background = null;
            _btn.hover.background = _hover;
            _btn.normal.textColor = _row.normal.textColor; _btn.hover.textColor = Color.white;
            _btn.padding = new RectOffset(6, 6, 3, 3);
            _btnDim = new GUIStyle(_btn); _btnDim.normal.textColor = _rowDim.normal.textColor;
            _btnDim.hover.textColor = _rowDim.normal.textColor;
        }

        private void LoadPos()
        {
            try
            {
                string[] p = (_padPos.Value ?? "").Split(',');
                if (p.Length == 2) { float x, y; if (float.TryParse(p[0], out x) && float.TryParse(p[1], out y)) { _rect.x = x; _rect.y = y; } }
            }
            catch (Exception) { }
        }
    }
}

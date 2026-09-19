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
        public const string Version = "2.3.0";

        private static GHCraftPadPlugin s_Self;

        private ConfigEntry<float>  _radius;
        private ConfigEntry<bool>   _showLocked;
        private ConfigEntry<bool>   _groundIfNoRoom;
        private ConfigEntry<bool>   _craftAfterPull;
        private ConfigEntry<string> _listPos;
        private ConfigEntry<int>    _fontSize;
        private ConfigEntry<bool>   _magnify;
        private ConfigEntry<float>  _magnifyScale;
        private ConfigEntry<float>  _magnifyRadius;
        // Where a crafted item goes: the game's own way, a storage box, or the backpack. Config
        // only since 2.0.0 - the arrows went with the window.
        private ConfigEntry<string> _destination;      // "None" | "Storage" | "Backpack"

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
            public int Cat;                        // index into Cats
            public bool Header;                    // a category line, not a recipe
            public int InCat, MadeInCat;           // header only: how many under it, how many makeable
        }

        // ONE CATEGORY OPEN AT A TIME. His words: "put all the recipes under the categories, and
        // clicking one category collapses any other open category" - the whole list ran off the
        // screen. -1 = all folded, which is how the table opens.
        private int _openCat = -1;

        // SMOOTH, NOT JUMPY - his words. Two jumps were in 2.2.0: the swell switched on only when the
        // mouse entered the list's rectangle (gone: it is distance now), and the lines were rebuilt
        // every 1.5 s, which threw their sizes away. So each line's scale is remembered by what it
        // is (its item, or its category) and eased toward the target every repaint.
        private readonly Dictionary<int, float> _scaleMemo = new Dictionary<int, float>();
        private float _lastRepaint;

        private float Eased(Line ln, float target)
        {
            int key = ln.Header ? -(ln.Cat + 1) : (int)ln.Result;
            float cur;
            if (!_scaleMemo.TryGetValue(key, out cur)) cur = target;
            if (Event.current.type == EventType.Repaint)
            {
                float dt = Mathf.Clamp(Time.realtimeSinceStartup - _lastRepaint, 0f, 0.1f);
                cur = Mathf.Lerp(cur, target, 1f - Mathf.Exp(-dt * 18f));
                if (Mathf.Abs(cur - target) < 0.004f) cur = target;
                _scaleMemo[key] = cur;
            }
            return cur;
        }

        // The categories, in the order they are printed. From ItemInfo.m_Type, the game's own.
        private static readonly string[] Cats = new string[] { "Weapons", "Tools", "Armor", "Food and medicine", "Camp", "Other" };

        private static int CatOf(ItemInfo info)
        {
            switch (info.m_Type)
            {
                case Enums.ItemType.Weapon: case Enums.ItemType.Spear: case Enums.ItemType.Bow: case Enums.ItemType.Arrow:
                case Enums.ItemType.Blowpipe: case Enums.ItemType.BlowpipeArrow: case Enums.ItemType.Dynamite:
                    return 0;
                case Enums.ItemType.ItemTool: case Enums.ItemType.Torch:
                    return 1;
                case Enums.ItemType.Armor:
                    return 2;
                case Enums.ItemType.Food: case Enums.ItemType.Herb: case Enums.ItemType.Dressing:
                case Enums.ItemType.Bowl: case Enums.ItemType.LiquidContainer:
                    return 3;
                case Enums.ItemType.Construction: case Enums.ItemType.Trap: case Enums.ItemType.Stand:
                case Enums.ItemType.Trough: case Enums.ItemType.BigStorage: case Enums.ItemType.Form: case Enums.ItemType.FormBaked:
                    return 4;
                default:
                    return 5;
            }
        }

        private readonly List<Line> _lines = new List<Line>();
        private float _linesAt;
        private int _boxesInRange;

        // Borrowed: item -> the box it came from. Only these ever go back.
        private readonly Dictionary<Item, Storage> _borrowed = new Dictionary<Item, Storage>();

        // ---- pad ----------------------------------------------------------------------------------
        private bool _padOpen;
        private GUIStyle _title, _row, _rowDim, _small, _btn, _btnDim, _head;
        private Font _chisel;
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
            _showLocked = Config.Bind("Pad", "ShowNotLearnedRecipes", true,
                "List recipes the game has not unlocked for you yet, greyed and marked. Off: only what " +
                "you know. On by default since 2.1.0 - his ask was every recipe.");
            _groundIfNoRoom = Config.Bind("Pad", "CraftedItemToGroundIfNoRoom", true,
                "A crafted item that does not fit in your backpack is dropped on the ground in " +
                "front of you. Off: the game's own behaviour, which leaves it on the table.");
            _listPos = Config.Bind("Look", "ListPosition", "",
                "Where the list sits, as a fraction of the screen (x,y). Drag its top line to move it; " +
                "written automatically. Empty = over the table.");
            _fontSize = Config.Bind("Look", "FontSize", 16,
                new ConfigDescription("The letters' size. Ctrl + wheel over the list changes it.",
                    new AcceptableValueRange<int>(8, 40)));
            _magnify = Config.Bind("Look", "Magnify", true,
                "Lines swell as the mouse nears them and shrink as it leaves, like the icons on a Mac " +
                "dock - his standard for every panel.");
            _magnifyScale = Config.Bind("Look", "MagnifyScale", 1.6f,
                new ConfigDescription("How big a line gets right under the mouse, as a multiple.",
                    new AcceptableValueRange<float>(1f, 3f)));
            _magnifyRadius = Config.Bind("Look", "MagnifyRadiusPixels", 90f,
                new ConfigDescription("How far from the mouse the swelling reaches.",
                    new AcceptableValueRange<float>(20f, 400f)));
            _craftAfterPull = Config.Bind("Pad", "CraftAfterPull", false,
                "Off (his rule): clicking a recipe only brings its parts to the table, and the game's " +
                "own Craft button makes the item with the count you dialled. On: the crafting starts " +
                "by itself.");
            _destination = Config.Bind("Pad", "CraftedItemGoesTo", "None",
                new ConfigDescription("Where a crafted item goes: Storage (the nearest box in range with " +
                    "room), Backpack, or None for the game's own way.",
                    new AcceptableValueList<string>("None", "Storage", "Backpack")));

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

                    // UP ARROW: send to storage. The game has just put the result in the backpack
                    // (or on the table if it did not fit); the nearest box in range with room takes it.
                    if (s_Self._destination.Value == "Storage" && s_Self.SendToStorage(__instance, __result)) return;

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

        private bool SendToStorage(CraftingManager cm, Item result)
        {
            try
            {
                InventoryBackpack bp = InventoryBackpack.Get();
                List<Storage> boxes = BoxesInRange();
                if (boxes.Count == 0) { Say("No box in range - " + Pretty(result.m_Info.m_ID) + " stays with you"); return false; }

                // Out of wherever the game put it - the same object moves, never a copy.
                if (result.m_OnCraftingTable) cm.RemoveItem(result, false, true);
                else if (bp != null && bp.m_Items != null && bp.m_Items.Contains(result)) bp.RemoveItem(result, false);

                for (int i = 0; i < boxes.Count; i++)
                {
                    InsertResult r = boxes[i].InsertItem(result, null, null, false, false, false);   // no drop: try the next box
                    if (r == InsertResult.Ok)
                    {
                        Logger.LogInfo("crafted " + result.m_Info.m_ID + " sent to storage (" + boxes[i].gameObject.name + ")");
                        Say(Pretty(result.m_Info.m_ID) + " sent to storage");
                        return true;
                    }
                }
                // Every box full: back to the backpack, and if that fails too, the ground.
                InsertResult back = (bp != null) ? bp.InsertItem(result, null, null, true, true, true, true, true) : InsertResult.CantInsert;
                Logger.LogInfo("crafted " + result.m_Info.m_ID + ": every box in range is full - " + (back == InsertResult.Ok ? "kept in the backpack" : "dropped at your feet"));
                Say("Boxes full - " + Pretty(result.m_Info.m_ID) + (back == InsertResult.Ok ? " kept with you" : " is on the ground"));
                return true;
            }
            catch (Exception ex) { Logger.LogWarning("send to storage: " + ex.Message); return false; }
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

            // THE GAME'S OWN LIST, not a filter of mine. "Not all the recipes are there": the first
            // build kept only infos with m_Craftable set, and the game does not - its
            // CraftingManager.InitializeAvailableItems takes every ItemInfo that is not a
            // construction and has components, into m_AvailableItems, and CheckResult matches the
            // table against exactly that list. So that list is read (private, via Traverse), and
            // when it is empty the same rule is applied to GetAllInfos.
            List<ItemInfo> source = null;
            try { source = Traverse.Create(cm).Field("m_AvailableItems").GetValue<List<ItemInfo>>(); } catch (Exception) { }
            if (source == null || source.Count == 0)
            {
                source = new List<ItemInfo>();
                Dictionary<int, ItemInfo> all = im.GetAllInfos();
                if (all == null) return;
                foreach (KeyValuePair<int, ItemInfo> kv in all)
                    if (kv.Value != null && !kv.Value.IsConstruction() && kv.Value.m_Components != null && kv.Value.m_Components.Count > 0)
                        source.Add(kv.Value);
            }
            if (!_sourceReported) { _sourceReported = true; Logger.LogInfo("recipes: " + source.Count + " in the game's list"); }
            for (int si = 0; si < source.Count; si++)
            {
                ItemInfo info = source[si];
                if (info == null) continue;
                Dictionary<int, int> comps = info.m_Components;
                if (comps == null || comps.Count == 0) continue;
                bool locked = im.m_CraftingLockedItems != null && im.m_CraftingLockedItems.Contains(info.m_ID);
                if (locked && !_showLocked.Value) continue;

                Line ln = new Line();
                ln.Result = info.m_ID;
                ln.Cat = CatOf(info);
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
            // Categorised: by category, then makeable first, then name; a header line per category.
            _lines.Sort(delegate (Line a, Line b)
            {
                if (a.Cat != b.Cat) return a.Cat.CompareTo(b.Cat);
                if ((a.CanMake > 0) != (b.CanMake > 0)) return a.CanMake > 0 ? -1 : 1;
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            int lastCat = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].Cat == lastCat) continue;
                lastCat = _lines[i].Cat;
                int made = 0, total = 0;
                for (int k = i; k < _lines.Count && _lines[k].Cat == lastCat; k++) { total++; if (_lines[k].CanMake > 0) made++; }
                Line h = new Line();
                h.Header = true; h.Cat = lastCat; h.InCat = total; h.MadeInCat = made;
                h.Label = Cats[lastCat].ToUpperInvariant();
                _lines.Insert(i, h);
                i++;
            }
        }
        private bool _sourceReported;

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

            // AddItem resets the wanted count to 1 each time, so it is set last; the game's own
            // count buttons and Craft button then work with it. His rule: clicking brings the stuff
            // to the table, nothing more - the game crafts when he presses Craft.
            cm.m_WantedResultsCount = n;
            if (_craftAfterPull.Value)
            {
                try { cm.StartCrafting(ln.Result, false); }
                catch (Exception ex) { Logger.LogWarning("StartCrafting: " + ex.Message + " - the parts are on the table, press the game's craft button"); }
            }

            Logger.LogInfo("pad: " + n + " x " + ln.Result + " - pulled " + pulledPack + " from the backpack, " + pulledBox + " from " + boxes.Count + " box(es)"
                + (_craftAfterPull.Value ? ", crafting" : ", on the table"));
            Say(n + " x " + Pretty(ln.Result) + " on the table" + (pulledBox > 0 ? " - " + pulledBox + " part(s) from your boxes" : "")
                + (_craftAfterPull.Value ? "" : " - press Craft"));
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
                DrawOnTable();
            }
            catch (Exception ex) { Logger.LogWarning("pad: " + ex.Message); }
        }

        // -----------------------------------------------------------------------------------------
        // PRINTED ON THE TABLE. His rule, 2026-09-19: "I don't want a separate window that opens
        // when I want to craft something. It needs to be printed on the crafting table itself.
        // Clicking on it brings the stuff to the table. No window opens, very simple."
        //
        // The table is CraftingManager.m_Table, seen through Inventory3DManager.m_Camera (the
        // camera the crafting view renders with). Its collider's bounds are projected to the screen
        // and the recipe lines are laid inside that rectangle, top down, plain text with a shadow:
        // no box, no title, no drag. A row lights up under the mouse; the wheel over it dials how
        // many; a click brings the parts. More lines than fit: the wheel over the header scrolls.
        // -----------------------------------------------------------------------------------------
        private int _firstLine;
        private bool _drag;
        private Vector2 _dragOff;

        // -----------------------------------------------------------------------------------------
        // WHERE, HOW BIG, AND THE DOCK. His asks, 2026-09-19, with a screenshot of the letters
        // sitting small in a corner of a huge projected slab:
        //   "Give me the option to customize the location and size of the font. Add some
        //    customization tools, moving, resizing. Like hover makes the font bigger - the Mac icon
        //    bar magnification. Make it a rule: the systematic increase and decrease of the font
        //    based on how far the mouse is from the font."
        // So: the list starts over the table's centre; its top line is a handle - drag it anywhere
        // and the place is kept in the cfg as a fraction of the screen. Ctrl + wheel sets the
        // letter size. And every line is drawn at a scale that rises smoothly as the mouse nears
        // it: a raised cosine of the distance, so the swell has no edge, and the lines below shift
        // down to make room exactly as the dock's icons do.
        // -----------------------------------------------------------------------------------------

        private Vector2 ListOrigin(CraftingManager cm, Camera cam)
        {
            try
            {
                string[] p = (_listPos.Value ?? "").Split(',');
                if (p.Length == 2)
                {
                    float fx, fy;
                    if (float.TryParse(p[0], out fx) && float.TryParse(p[1], out fy))
                        return new Vector2(Mathf.Clamp01(fx) * Screen.width, Mathf.Clamp01(fy) * Screen.height);
                }
            }
            catch (Exception) { }
            // Default: the table's centre on screen, a little up and left so the list hangs over it.
            Vector3 sp = cam.WorldToScreenPoint(cm.m_Table.transform.position);
            if (sp.z > 0f) return new Vector2(Mathf.Clamp(sp.x - Screen.width * 0.12f, 0f, Screen.width * 0.7f),
                                              Mathf.Clamp(Screen.height - sp.y - Screen.height * 0.25f, 0f, Screen.height * 0.6f));
            return new Vector2(Screen.width * 0.12f, Screen.height * 0.12f);
        }

        private void SaveOrigin(Vector2 o)
        {
            string v = (o.x / Screen.width).ToString("F3") + "," + (o.y / Screen.height).ToString("F3");
            if (v != _listPos.Value) _listPos.Value = v;
        }

        /// <summary>1 at the mouse, falling to 1 at the edge of the radius by a raised cosine: no edge, no snap.</summary>
        private float Swell(float dist)
        {
            if (!_magnify.Value) return 1f;
            float r = _magnifyRadius.Value;
            if (dist >= r) return 1f;
            float t = 0.5f * (1f + Mathf.Cos(Mathf.PI * dist / r));
            return 1f + (_magnifyScale.Value - 1f) * t;
        }

        private void DrawOnTable()
        {
            CraftingManager cm = CraftingManager.Get();
            if (cm == null || cm.m_Table == null) return;
            Camera cam = null;
            try { Inventory3DManager inv = Inventory3DManager.Get(); if (inv != null) cam = inv.m_Camera; } catch (Exception) { }
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            Event e = Event.current;
            Vector2 origin = ListOrigin(cm, cam);
            int baseSize = _fontSize.Value;
            float rowH = baseSize * 1.55f;
            float width = Mathf.Min(Screen.width - origin.x, Mathf.Max(360f, baseSize * 34f));

            // The handle line: drag to move; it also says what the mouse does.
            Rect head = new Rect(origin.x, origin.y, width, rowH);
            _small.fontSize = Mathf.Max(9, (int)(baseSize * 0.75f));
            Print(head, "::  " + _boxesInRange + " box" + (_boxesInRange == 1 ? "" : "es") + " in reach   -   drag me;  click a heading to open it;  wheel: how many;  click: to the table;  Ctrl+wheel: size", _small, head.Contains(e.mousePosition));
            if (e.type == EventType.MouseDown && e.button == 0 && head.Contains(e.mousePosition)) { _drag = true; _dragOff = e.mousePosition - origin; e.Use(); }
            if (_drag && e.type == EventType.MouseDrag) { origin = e.mousePosition - _dragOff; SaveOrigin(origin); e.Use(); }
            if (_drag && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)) { _drag = false; e.Use(); }

            // The list's whole rectangle, for Ctrl + wheel and for the dock's reach.
            Rect list = new Rect(origin.x, origin.y, width, Screen.height - origin.y);
            bool overList = list.Contains(e.mousePosition);
            if (e.type == EventType.ScrollWheel && e.control && overList)
            {
                _fontSize.Value = Mathf.Clamp(_fontSize.Value - (int)Mathf.Sign(e.delta.y), 8, 40);
                e.Use();
                return;
            }
            bool scrollHere = e.type == EventType.ScrollWheel && !e.control && (head.Contains(e.mousePosition) || (e.shift && overList));

            if (_lines.Count == 0)
            {
                _rowDim.fontSize = baseSize;
                Print(new Rect(origin.x, origin.y + rowH, width, rowH), "Nothing to make from what is in reach.", _rowDim, false);
                return;
            }

            // What is on show: every heading, and the recipes of the open category only.
            List<Line> show = new List<Line>();
            for (int i = 0; i < _lines.Count; i++)
                if (_lines[i].Header || _lines[i].Cat == _openCat) show.Add(_lines[i]);

            // How many fit at rest, and the dock: each visible line's swell from the mouse's distance
            // to where the line would sit at rest - the distance to the LINE, not "is the mouse over
            // it": straight down from the row's centre, plus how far the mouse is beyond the text's
            // end, so the swell follows the pointer in from any side and fades on any side. Then the
            // lines are laid one under the other at their own sizes.
            int fit = Mathf.Max(1, (int)((Screen.height - origin.y - 2f * rowH) / rowH));
            _firstLine = Mathf.Clamp(_firstLine, 0, Mathf.Max(0, show.Count - fit));
            float y = origin.y + rowH;
            float restY = y;
            int shown = 0;
            for (int i = _firstLine; i < show.Count && shown < fit; i++, shown++, restY += rowH)
            {
                Line ln = show[i];
                float textW = ln.Header ? baseSize * 14f : Mathf.Min(width, baseSize * 0.55f * (ln.Label.Length + 14));
                float dx = Mathf.Max(0f, Mathf.Max(origin.x - e.mousePosition.x, e.mousePosition.x - (origin.x + textW)));
                float dy = e.mousePosition.y - (restY + rowH * 0.5f);
                float sc = Eased(ln, Swell(Mathf.Sqrt(dx * dx + dy * dy)));
                float h = rowH * sc;
                if (y + h > Screen.height) break;
                Rect rr = new Rect(origin.x, y, width, h);
                bool hover = rr.Contains(e.mousePosition);
                int size = Mathf.RoundToInt(baseSize * sc);
                if (ln.Header)
                {
                    bool open = ln.Cat == _openCat;
                    _head.fontSize = size + 1;
                    string hl = ln.Label + (open ? "" : "   (" + ln.InCat + (ln.MadeInCat > 0 ? ", " + ln.MadeInCat + " you can make" : "") + ")");
                    if (hover && e.type == EventType.Repaint) GUI.DrawTexture(rr, _hover);
                    Print(rr, hl, _head, hover);
                    if (hover && e.type == EventType.ScrollWheel && !e.control) scrollHere = true;
                    if (hover && e.type == EventType.MouseUp && e.button == 0 && !_drag)
                    {
                        _openCat = open ? -1 : ln.Cat;       // one open at a time
                        _firstLine = 0;
                        e.Use();
                    }
                    y += h;
                    continue;
                }
                bool can = ln.CanMake > 0;
                string right = can ? (ln.Count + " of " + ln.CanMake) : ("need " + ln.Missing);
                string text = "    " + ln.Label + "    " + right + (can && ln.FromBoxes > 0 ? "   (" + ln.FromBoxes + " from boxes)" : "");
                GUIStyle st = can ? _row : _rowDim;
                st.fontSize = size;
                if (hover && e.type == EventType.Repaint) GUI.DrawTexture(rr, _hover);
                Print(rr, text, st, hover);

                if (can && hover && e.type == EventType.ScrollWheel && !e.control && !e.shift)
                {
                    ln.Count = Mathf.Clamp(ln.Count - (int)Mathf.Sign(e.delta.y), 1, ln.CanMake);
                    e.Use();
                }
                if (can && hover && e.type == EventType.MouseUp && e.button == 0 && !_drag)
                {
                    e.Use();
                    try { PullAndCraft(ln); }
                    catch (Exception ex) { Logger.LogWarning("pull: " + ex.Message); Say("Could not pull - see the log"); }
                }
                y += h;
            }
            _row.fontSize = baseSize; _rowDim.fontSize = baseSize; _head.fontSize = baseSize + 1;
            if (e.type == EventType.Repaint) _lastRepaint = Time.realtimeSinceStartup;
            if (_firstLine + shown < show.Count)
                Print(new Rect(origin.x, y, width, rowH), "... " + (show.Count - _firstLine - shown) + " more (wheel over a heading, or Shift + wheel)", _small, false);
            if (scrollHere)
            {
                _firstLine = Mathf.Clamp(_firstLine + (int)Mathf.Sign(e.delta.y) * 3, 0, Mathf.Max(0, show.Count - fit));
                e.Use();
            }
        }

        /// <summary>The table's collider bounds, projected to a GUI rectangle. False when it is off screen.</summary>
        private static bool TableOnScreen(CraftingManager cm, Camera cam, out Rect area)
        {
            area = new Rect();
            Bounds b;
            if (cm.m_TableCollider != null) b = cm.m_TableCollider.bounds;
            else b = new Bounds(cm.m_Table.transform.position, new Vector3(1.2f, 0.2f, 0.8f));
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int behind = 0;
            for (int k = 0; k < 8; k++)
            {
                Vector3 c = new Vector3((k & 1) == 0 ? b.min.x : b.max.x, (k & 2) == 0 ? b.min.y : b.max.y, (k & 4) == 0 ? b.min.z : b.max.z);
                Vector3 sp = cam.WorldToScreenPoint(c);
                if (sp.z <= 0f) { behind++; continue; }
                float gx = sp.x, gy = Screen.height - sp.y;
                if (gx < minX) minX = gx; if (gx > maxX) maxX = gx;
                if (gy < minY) minY = gy; if (gy > maxY) maxY = gy;
            }
            if (behind == 8 || maxX <= minX || maxY <= minY) return false;
            // Inset a little so the text sits on the wood, not on its edge; never wider than the screen.
            float inX = (maxX - minX) * 0.06f, inY = (maxY - minY) * 0.06f;
            area = new Rect(minX + inX, minY + inY, maxX - minX - 2f * inX, maxY - minY - 2f * inY);
            area.xMin = Mathf.Max(area.xMin, 0f); area.xMax = Mathf.Min(area.xMax, Screen.width);
            area.yMin = Mathf.Max(area.yMin, 0f); area.yMax = Mathf.Min(area.yMax, Screen.height);
            return area.width > 120f && area.height > 60f;
        }

        /// <summary>
        /// CHISELLED. His words: "the font imposed like chiselled into the rock". An engraved
        /// letter is a groove: its upper-left wall is in shadow, its lower-right wall catches the
        /// light, and the floor is darker than the surface. So: a dark copy up-left, a pale copy
        /// down-right, and the letter itself in a dark, slightly transparent ink over the wood.
        /// Hovered: the ink lightens, as if the groove were freshly cut.
        /// </summary>
        private static void Print(Rect r, string text, GUIStyle style, bool bright)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Label(new Rect(r.x - 1f, r.y - 1f, r.width, r.height), text, style);
            GUI.color = new Color(1f, 0.95f, 0.85f, 0.55f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);
            GUI.color = bright ? new Color(0.55f, 0.45f, 0.35f, 1f) : new Color(0.16f, 0.11f, 0.07f, 0.92f);
            GUI.Label(r, text, style);
            GUI.color = old;
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

            // A serif, bold, for the chisel: open-source faces first, then what Windows ships.
            // Whichever is found is named in the log; none found = the skin's own font.
            string[] faces = new string[] { "Linux Libertine O", "Liberation Serif", "DejaVu Serif", "Noto Serif", "Georgia", "Times New Roman" };
            for (int i = 0; i < faces.Length && _chisel == null; i++)
            {
                try
                {
                    Font f = Font.CreateDynamicFontFromOSFont(faces[i], 16);
                    if (f != null && f.fontNames != null && f.fontNames.Length > 0) { _chisel = f; Logger.LogInfo("chisel font: " + faces[i]); }
                }
                catch (Exception) { }
            }

            _title = new GUIStyle(GUI.skin.label); _title.fontSize = 18; _title.fontStyle = FontStyle.Bold;
            _title.normal.textColor = new Color(0.96f, 0.97f, 1f);
            // The lines on the table: white text, coloured by GUI.color in Print() - the chisel.
            _row = new GUIStyle(GUI.skin.label); _row.fontSize = 16; _row.fontStyle = FontStyle.Bold; _row.alignment = TextAnchor.MiddleLeft;
            if (_chisel != null) _row.font = _chisel;
            _row.normal.textColor = Color.white;
            _rowDim = new GUIStyle(_row); _rowDim.normal.textColor = new Color(1f, 1f, 1f, 0.55f);
            _head = new GUIStyle(_row); _head.fontSize = 17;
            _small = new GUIStyle(_row); _small.fontSize = 12; _small.fontStyle = FontStyle.Normal; _small.normal.textColor = new Color(1f, 1f, 1f, 0.8f);

            _btn = new GUIStyle(GUI.skin.button); _btn.fontSize = 15; _btn.alignment = TextAnchor.MiddleLeft;
            _btn.normal.background = null; _btn.active.background = null; _btn.focused.background = null;
            _btn.hover.background = _hover;
            _btn.normal.textColor = _row.normal.textColor; _btn.hover.textColor = Color.white;
            _btn.padding = new RectOffset(6, 6, 3, 3);
            _btnDim = new GUIStyle(_btn); _btnDim.normal.textColor = _rowDim.normal.textColor;
            _btnDim.hover.textColor = _rowDim.normal.textColor;
        }

    }
}

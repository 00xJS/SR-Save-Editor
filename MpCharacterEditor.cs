// Save editor for Saints Reborn (unofficial), Windows version.
//
// Story saves (game.dat) are handled in Core.cs. This file has the window and
// the multiplayer character file the game keeps at
//   dist\game\545107D1\profile\<player name>\storage\3_MPStorage
// (27,724 bytes, big-endian):
//   0x0004  u32  cash in cents
//   0x0414  u32  number of worn items, then 16-byte records (room for 128):
//                item id, variant id, slot, wear option (MeshID); see WardrobeTables in Core.cs
//   0x0C18  u32  number of owned items, then 12-byte records:
//                item id, variant id, 0          (room for 2048)
// Item names come from the player's own game files (customization_items.xtbl,
// customization_stores.xtbl and US_Strings.txt inside misc.vpp_xbox2); nothing
// from the game is included in this program.
//
// The Mac version (mac/) has the same features; change both together.
//
// Build: build.bat (uses the C# compiler that comes with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace MpCharacterEditor
{
    class Variant { public int Id; public string Name, Title; }

    class Item
    {
        public int Id;
        public string Name, Title, Category;
        public Dictionary<int, Variant> Variants = new Dictionary<int, Variant>();
        public Dictionary<int, string> Wear = new Dictionary<int, string>();
    }

    struct Pair
    {
        public int Item, Variant;
        public Pair(int item, int variant) { Item = item; Variant = variant; }
        public long Key { get { return ((long)Item << 32) | (uint)Variant; } }
    }

    // The game's customization tables, read from the player's own packfile.
    class Tables
    {
        public readonly Dictionary<int, Item> Items = new Dictionary<int, Item>();
        public readonly List<Pair> Store = new List<Pair>();   // sold in multiplayer
        readonly Dictionary<string, string> strings;
        const string MpStore = "Multiplayer Purchase Clothing";

        public Tables(GameData game)
        {
            Packfile pack = game.Pack;
            strings = game.Strings;

            Dictionary<string, Item> byName = new Dictionary<string, Item>();
            XmlDocument doc = Load(pack.Read("customization_items.xtbl"));
            foreach (XmlNode n in doc.SelectNodes("/root/Table/Customization_Item"))
            {
                Item item = new Item();
                item.Id = Int(n, "ID");
                item.Name = Str(n, "Name");
                item.Title = Lookup(Str(n, "DisplayName"), item.Name);
                item.Category = Str(n, "Category");
                foreach (XmlNode v in n.SelectNodes("Variants/Variant"))
                {
                    Variant variant = new Variant();
                    variant.Id = Int(v, "VariantID");
                    variant.Name = Str(v, "Variant_Name");
                    variant.Title = Lookup(Str(v, "Name"), variant.Name);
                    item.Variants[variant.Id] = variant;
                }
                foreach (XmlNode w in n.SelectNodes("Wear_Options/Wear_Option"))
                    if (Str(w, "MeshID").Length > 0) item.Wear[Int(w, "MeshID")] = Lookup(Str(w, "Name"), Str(w, "Name"));
                Items[item.Id] = item;
                byName[item.Name] = item;
            }

            HashSet<long> seen = new HashSet<long>();
            doc = Load(pack.Read("customization_stores.xtbl"));
            foreach (XmlNode store in doc.SelectNodes("/root/Table/Store"))
            {
                if (Str(store, "Name") != MpStore) continue;
                foreach (XmlNode si in store.SelectNodes("Items/Store_Item"))
                {
                    Item item;
                    if (!byName.TryGetValue(Str(si, "Item"), out item)) continue;
                    HashSet<string> wanted = new HashSet<string>();
                    foreach (XmlNode v in si.SelectNodes("Variants/Item_Variant/Variant")) wanted.Add(v.InnerText.Trim());
                    foreach (Variant variant in item.Variants.Values)
                    {
                        if (wanted.Count > 0 && !wanted.Contains(variant.Name)) continue;
                        Pair p = new Pair(item.Id, variant.Id);
                        if (Rejected(p)) continue;
                        if (seen.Add(p.Key)) Store.Add(p);
                    }
                }
            }
        }

        // Listed in the multiplayer store table, but the game throws these three
        // Football Jersey styles out of the owned list when it loads the character.
        static bool Rejected(Pair p)
        {
            return p.Item == 235 && (p.Variant == 0 || p.Variant == 512 || p.Variant == 555);
        }

        static XmlDocument Load(byte[] bytes)
        {
            XmlDocument doc = new XmlDocument();
            using (MemoryStream ms = new MemoryStream(bytes)) doc.Load(ms);
            return doc;
        }
        static string Str(XmlNode n, string child)
        {
            XmlNode c = n[child];
            return c == null ? "" : c.InnerText.Trim();
        }
        static int Int(XmlNode n, string child)
        {
            int v;
            return int.TryParse(Str(n, child), out v) ? v : -1;
        }
        string Lookup(string key, string fallback)
        {
            string s;
            return key.Length > 0 && strings.TryGetValue(key, out s) && s.Length > 0 ? s : fallback;
        }

        public string ItemTitle(int id)
        {
            Item item;
            return Items.TryGetValue(id, out item) ? item.Title : "Item " + id;
        }
        public string VariantTitle(int id, int variant)
        {
            Item item; Variant v;
            if (Items.TryGetValue(id, out item) && item.Variants.TryGetValue(variant, out v)) return v.Title;
            return "Style " + variant;
        }
        public string Category(int id)
        {
            Item item;
            return Items.TryGetValue(id, out item) ? item.Category : "";
        }
        public string WearTitle(int id, int mesh)
        {
            Item item; string s;
            return Items.TryGetValue(id, out item) && item.Wear.TryGetValue(mesh, out s) ? s : "";
        }
    }

    class Character
    {
        public const int Size = 27724;
        const int CashAt = 0x4, WornCountAt = 0x414, WornAt = 0x418, WornMax = 128;
        const int OwnedCountAt = 0xC18, OwnedAt = 0xC1C;
        public const int OwnedMax = 2048;

        public readonly string Path;
        readonly byte[] data;
        public long Cents;
        public readonly List<Pair> Owned = new List<Pair>();
        // What the character wears, one record per body slot.
        public List<WornItem> Outfit = new List<WornItem>();

        public Character(string path)
        {
            Path = path;
            data = File.ReadAllBytes(path);
            if (data.Length != Size)
                throw new InvalidDataException("This file is " + data.Length + " bytes; a multiplayer character file is " + Size + ".");
            Cents = Be.U32(data, CashAt);
            Outfit = WardrobeTables.Read(data, WornCountAt, WornAt, WornMax);
            int n = (int)Math.Min(Be.U32(data, OwnedCountAt), (uint)OwnedMax);
            for (int k = 0; k < n; k++)
                Owned.Add(new Pair((int)Be.U32(data, OwnedAt + 12 * k), (int)Be.U32(data, OwnedAt + 12 * k + 4)));
        }

        // Writes cash, the outfit and the owned list; returns the name of the backup.
        public string Save(long cents, List<Pair> owned)
        {
            if (owned.Count > OwnedMax) throw new InvalidOperationException("The file has room for " + OwnedMax + " owned items.");
            Be.Put(data, CashAt, (uint)cents);
            WardrobeTables.Write(data, WornCountAt, WornAt, WornMax, Outfit);
            Be.Put(data, OwnedCountAt, (uint)owned.Count);
            Array.Clear(data, OwnedAt, OwnedMax * 12);
            for (int k = 0; k < owned.Count; k++)
            {
                Be.Put(data, OwnedAt + 12 * k, (uint)owned[k].Item);
                Be.Put(data, OwnedAt + 12 * k + 4, (uint)owned[k].Variant);
            }
            string backup = StorySave.Backup(Path);
            File.WriteAllBytes(Path, data);
            return backup;
        }
    }

    static class Game
    {
        const string Probe = @"game\packfiles\misc.vpp_xbox2";
        static string SettingsFile
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    @"SaintsRebornMpEditor\folder.txt");
            }
        }

        public static bool IsDist(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, Probe)); }
            catch { return false; }
        }

        // The folder with saintsrow.exe: remembered, next to this program, or
        // one of the usual install folders.
        public static string FindDist()
        {
            List<string> tries = new List<string>();
            try { if (File.Exists(SettingsFile)) tries.Add(File.ReadAllText(SettingsFile).Trim()); } catch { }
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && dir != null; i++)
            {
                tries.Add(dir);
                tries.Add(Path.Combine(dir, "dist"));
                DirectoryInfo parent = Directory.GetParent(dir.TrimEnd('\\'));
                dir = parent == null ? null : parent.FullName;
            }
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                tries.Add(Path.Combine(drive.Name, @"Games\SaintsReborn\dist"));
                tries.Add(Path.Combine(drive.Name, @"SaintsReborn\dist"));
            }
            foreach (string t in tries) if (IsDist(t)) return t;
            return null;
        }

        public static void Remember(string dist)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, dist);
            }
            catch { }
        }

        public static bool Running { get { return Process.GetProcessesByName("saintsrow").Length > 0; } }

        // Player names that have a multiplayer character file.
        public static List<string> Players(string dist)
        {
            List<string> names = new List<string>();
            string root = Path.Combine(dist, @"game\545107D1\profile");
            if (Directory.Exists(root))
                foreach (string d in Directory.GetDirectories(root))
                    if (File.Exists(Path.Combine(d, @"storage\3_MPStorage"))) names.Add(Path.GetFileName(d));
            return names;
        }
        // Story saves (game.dat), newest first.
        public static List<string> StorySaves(string dist)
        {
            List<string> files = new List<string>();
            string root = Path.Combine(dist, "game");
            if (!Directory.Exists(root)) return files;
            foreach (string profile in Directory.GetDirectories(root))
            {
                string slots = Path.Combine(profile, @"545107D1\00000001");
                if (Path.GetFileName(profile).Length != 16 || !Directory.Exists(slots)) continue;
                foreach (string slot in Directory.GetDirectories(slots))
                    if (File.Exists(Path.Combine(slot, "game.dat"))) files.Add(Path.Combine(slot, "game.dat"));
            }
            files.Sort(delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            return files;
        }
        public static string CharacterFile(string dist, string player)
        {
            return Path.Combine(dist, @"game\545107D1\profile\" + player + @"\storage\3_MPStorage");
        }
    }

    // The weapon wheel, drawn here (no game artwork): eight slots around a
    // stick, and four quick slots around a d-pad.
    class WheelPanel : Panel
    {
        public readonly string[] Labels = new string[8];
        public readonly bool[] Filled = new bool[8];
        public readonly double[] Ammo = new double[8];   // 0..1, below 0 = no ammo arc
        public readonly string[] Quick = new string[4];
        public readonly Image[] Icons = new Image[8], QuickIcons = new Image[4];
        public int Selected = 2;
        public event Action<int> SlotClicked, QuickClicked;

        public WheelPanel()
        {
            DoubleBuffered = true; ResizeRedraw = true;
            for (int i = 0; i < 8; i++) { Labels[i] = GameData.SlotNames[i]; Ammo[i] = -1; }
        }

        float Side { get { return Math.Max(40, Math.Min(ClientSize.Width, ClientSize.Height)); } }
        PointF Origin { get { return new PointF((ClientSize.Width - Side) / 2f, (ClientSize.Height - Side) / 2f); } }
        PointF SlotCentre(int i)
        {
            double angle = i * Math.PI / 4 - Math.PI / 2;
            return new PointF(Origin.X + Side * 0.42f + Side * 0.30f * (float)Math.Cos(angle), Origin.Y + Side * 0.44f + Side * 0.30f * (float)Math.Sin(angle));
        }
        PointF QuickCentre(int i)
        {
            int[] dx = { 0, 1, 0, -1 }, dy = { -1, 0, 1, 0 };
            return new PointF(Origin.X + Side * 0.81f + dx[i] * Side * 0.115f, Origin.Y + Side * 0.81f + dy[i] * Side * 0.115f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(BackColor);
            float side = Side;
            RectangleF area = new RectangleF(Origin.X, Origin.Y, side, side);
            using (LinearGradientBrush back = new LinearGradientBrush(area, Color.FromArgb(115, 128, 219), Color.FromArgb(148, 158, 230), 90f))
                g.FillRectangle(back, area);
            for (int i = 0; i < 8; i++) DrawSlot(g, SlotCentre(i), side * 0.165f, Labels[i], Icons[i], Filled[i], Ammo[i], i == Selected && i != 0);
            for (int i = 0; i < 4; i++) DrawSlot(g, QuickCentre(i), side * 0.11f, Quick[i] ?? "", QuickIcons[i], Quick[i] != null, -1, false);

            PointF c = new PointF(Origin.X + side * 0.42f, Origin.Y + side * 0.44f);
            float d = side * 0.13f;
            RectangleF stick = new RectangleF(c.X - d / 2, c.Y - d / 2, d, d);
            using (LinearGradientBrush b = new LinearGradientBrush(stick, Color.FromArgb(140, 140, 140), Color.FromArgb(56, 56, 56), 90f)) g.FillEllipse(b, stick);
            using (Pen p = new Pen(Color.FromArgb(230, 0, 0, 0), 5f)) g.DrawEllipse(p, stick);
            DrawText(g, "LS", stick, side * 0.036f, Color.FromArgb(230, 230, 230));

            PointF pad = new PointF(Origin.X + side * 0.81f, Origin.Y + side * 0.81f);
            float arm = side * 0.035f, thick = side * 0.022f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(89, 89, 89)))
            {
                g.FillRectangle(b, pad.X - arm, pad.Y - thick / 2, arm * 2, thick);
                g.FillRectangle(b, pad.X - thick / 2, pad.Y - arm, thick, arm * 2);
            }
        }

        void DrawSlot(Graphics g, PointF c, float size, string label, Image icon, bool filled, double ammo, bool selected)
        {
            RectangleF r = new RectangleF(c.X - size / 2, c.Y - size / 2, size, size);
            if (selected)
                using (Pen glow = new Pen(Color.FromArgb(150, 115, 184, 255), size * 0.07f))
                    g.DrawEllipse(glow, RectangleF.Inflate(r, size * 0.07f, size * 0.07f));
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using (PathGradientBrush fill = new PathGradientBrush(path))
                {
                    fill.CenterColor = Color.FromArgb(107, 120, 204);
                    fill.SurroundColors = new Color[] { Color.FromArgb(77, 87, 168) };
                    g.FillEllipse(fill, r);
                }
            }
            using (Pen rim = new Pen(Color.FromArgb(235, 0, 0, 0), size * 0.08f)) g.DrawEllipse(rim, r);
            if (ammo >= 0)
                using (Pen arc = new Pen(Color.FromArgb(255, 140, 20), size * 0.07f))
                    g.DrawArc(arc, r, 100f, (float)(115.2 * Math.Max(0.04, Math.Min(1.0, ammo))));
            if (icon != null)
            {
                float iw = size * 0.72f, ih = iw * icon.Height / icon.Width;
                if (ih > size * 0.55f) { ih = size * 0.55f; iw = ih * icon.Width / icon.Height; }
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new RectangleF(c.X - iw / 2, c.Y - ih / 2, iw, ih));
            }
            else
                DrawText(g, label, RectangleF.Inflate(r, -size * 0.07f, -size * 0.07f), Math.Max(7f, size * 0.105f),
                         filled ? Color.White : Color.FromArgb(140, 255, 255, 255));
        }

        static void DrawText(Graphics g, string text, RectangleF r, float size, Color colour)
        {
            using (Font f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            using (SolidBrush b = new SolidBrush(colour))
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, f, b, r, sf);
            }
        }

        static bool Inside(PointF c, float size, Point p)
        {
            float dx = p.X - c.X, dy = p.Y - c.Y;
            return dx * dx + dy * dy <= size * size / 4;
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 1; i < 8; i++)
                if (Inside(SlotCentre(i), Side * 0.165f, e.Location)) { if (SlotClicked != null) SlotClicked(i); return; }
            for (int i = 0; i < 4; i++)
                if (Inside(QuickCentre(i), Side * 0.11f, e.Location)) { if (QuickClicked != null) QuickClicked(i); return; }
        }
    }

    // The wardrobe: body slots on the left, what can go in the chosen slot on the
    // right. Used for the story save and for the online character.
    class WardrobePanel : Panel
    {
        readonly WardrobeTables tables;
        readonly HashSet<long> allowed;   // online: only clothing sold in multiplayer; null = everything
        readonly Action changed;
        List<WornItem> outfit = new List<WornItem>();
        int slot = 12;
        bool filling;
        readonly ListView slotList = new ListView(), choiceList = new ListView();
        readonly Label title = new Label(), note = new Label();
        readonly ComboBox wearBox = new ComboBox();
        readonly Button takeOff = new Button();
        readonly TextBox search = new TextBox();

        public WardrobePanel(WardrobeTables tables, HashSet<long> allowed, Action changed)
        {
            this.tables = tables; this.allowed = allowed; this.changed = changed;
            Dock = DockStyle.Fill;

            slotList.View = View.Details; slotList.FullRowSelect = true; slotList.HideSelection = false; slotList.MultiSelect = false;
            slotList.Columns.Add("Slot", 130); slotList.Columns.Add("Wearing", 190);
            slotList.Dock = DockStyle.Left; slotList.Width = 330;
            foreach (BodySlot b in WardrobeTables.Slots) { ListViewItem row = new ListViewItem(new string[] { b.Title, "" }); row.Tag = b.Id; slotList.Items.Add(row); }
            slotList.ItemSelectionChanged += delegate(object o, ListViewItemSelectionChangedEventArgs e)
            {
                if (filling || !e.IsSelected) return;
                slot = (int)e.Item.Tag; ShowSlot();
            };

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top; top.AutoSize = true; top.Padding = new Padding(6, 6, 6, 2); top.WrapContents = false;
            title.AutoSize = true; title.Margin = new Padding(6, 7, 12, 0); title.Font = new Font(Font, FontStyle.Bold);
            top.Controls.Add(title);
            Label wornAs = new Label(); wornAs.Text = "Worn as"; wornAs.AutoSize = true; wornAs.Margin = new Padding(6, 7, 2, 0);
            top.Controls.Add(wornAs);
            wearBox.DropDownStyle = ComboBoxStyle.DropDownList; wearBox.Width = 220;
            wearBox.SelectedIndexChanged += delegate
            {
                WornItem w = WardrobeTables.WornIn(outfit, slot);
                if (filling || w == null || wearBox.SelectedItem == null) return;
                WardrobeTables.Put(outfit, w.Item, w.Variant, ((WearOption)wearBox.SelectedItem).Mesh, slot);
                Refresh(); changed();
            };
            top.Controls.Add(wearBox);
            takeOff.Text = "Take off"; takeOff.AutoSize = true;
            takeOff.Click += delegate { WardrobeTables.TakeOff(outfit, slot); Refresh(); changed(); };
            top.Controls.Add(takeOff);

            FlowLayoutPanel filter = new FlowLayoutPanel();
            filter.Dock = DockStyle.Top; filter.AutoSize = true; filter.Padding = new Padding(6, 2, 6, 4); filter.WrapContents = false;
            Label caption = new Label(); caption.Text = "Search"; caption.AutoSize = true; caption.Margin = new Padding(6, 7, 2, 0);
            filter.Controls.Add(caption);
            search.Width = 200; search.TextChanged += delegate { ShowChoices(); };
            filter.Controls.Add(search);
            note.AutoSize = true; note.Margin = new Padding(12, 7, 0, 0); note.ForeColor = SystemColors.GrayText;
            filter.Controls.Add(note);

            choiceList.View = View.Details; choiceList.FullRowSelect = true; choiceList.HideSelection = false; choiceList.MultiSelect = false;
            choiceList.Columns.Add("Item", 230); choiceList.Columns.Add("Colour / style", 230); choiceList.Columns.Add("Category", 120);
            choiceList.Dock = DockStyle.Fill;
            // Refilled once the window exists - after OnHandleCreated has finished, since changing
            // the items inside it crashes .NET's ListView.
            choiceList.HandleCreated += delegate { BeginInvoke((Action)ShowChoices); };
            slotList.HandleCreated += delegate { BeginInvoke((Action)Refresh); };
            choiceList.ItemSelectionChanged += delegate(object o, ListViewItemSelectionChangedEventArgs e)
            {
                if (filling || !e.IsSelected) return;
                WornItem pick = (WornItem)e.Item.Tag;
                WornItem now = WardrobeTables.WornIn(outfit, slot);
                // Keep the way it is worn when only the colour changes.
                int mesh = now != null && now.Item == pick.Item ? now.Mesh : pick.Mesh;
                WardrobeTables.Put(outfit, pick.Item, pick.Variant, mesh, slot);
                Refresh(); changed();
            };

            Panel right = new Panel(); right.Dock = DockStyle.Fill;
            right.Controls.Add(choiceList); right.Controls.Add(filter); right.Controls.Add(top);
            Controls.Add(right); Controls.Add(slotList);
            Visible = true;
        }

        // Shows an outfit; changes are made to the list itself.
        public void Show(List<WornItem> outfit)
        {
            this.outfit = outfit;
            Refresh();
        }
        public new void Refresh()
        {
            filling = true;
            foreach (ListViewItem row in slotList.Items)
            {
                WornItem w = WardrobeTables.WornIn(outfit, (int)row.Tag);
                row.SubItems[1].Text = w == null ? "" : tables.Title(w) + (tables.Style(w).Length > 0 ? " - " + tables.Style(w) : "");
                if (slotList.IsHandleCreated) row.Selected = (int)row.Tag == slot;
            }
            int others = 0;
            foreach (WornItem w in outfit) if (WardrobeTables.Slot(w.Slot) == null) others++;
            note.Text = others > 0 ? others + " worn in slots this editor can't change yet" : "";
            filling = false;
            ShowSlot();
        }

        void ShowSlot()
        {
            BodySlot b = WardrobeTables.Slot(slot);
            WornItem w = WardrobeTables.WornIn(outfit, slot);
            title.Text = (b != null ? b.Title : "Slot " + slot) + ": " + (w == null ? "nothing" : tables.Title(w) + " " + tables.Style(w));
            filling = true;
            wearBox.Items.Clear();
            WardrobeItem item = w == null ? null : tables.Item(w.Item);
            if (item != null)
                foreach (WearOption o in item.OptionsIn(slot)) { wearBox.Items.Add(o); if (o.Mesh == w.Mesh) wearBox.SelectedItem = o; }
            wearBox.Enabled = wearBox.Items.Count > 1;
            takeOff.Enabled = w != null;
            filling = false;
            ShowChoices();
        }

        void ShowChoices()
        {
            WornItem now = WardrobeTables.WornIn(outfit, slot);
            string q = search.Text.Trim().ToLowerInvariant();
            filling = true;
            choiceList.BeginUpdate();
            choiceList.Items.Clear();
            // Selecting a row before the list has its window makes .NET keep a stale "saved
            // selection" after the next Clear(), and it crashes in OnHandleCreated when the tab is
            // first shown. So the row is selected only once the window exists (HandleCreated refills).
            ListViewItem chosen = null;
            foreach (WardrobeItem item in tables.Choices(slot))
            {
                List<WearOption> options = item.OptionsIn(slot);
                List<WardrobeVariant> variants = item.Variants;
                if (variants.Count == 0) { WardrobeVariant none = new WardrobeVariant(); none.Id = 0; none.Title = ""; variants = new List<WardrobeVariant>(); variants.Add(none); }
                foreach (WardrobeVariant v in variants)
                {
                    if (allowed != null && !allowed.Contains(new Pair(item.Id, v.Id).Key)) continue;
                    if (q.Length > 0 && (item.Title + " " + v.Title + " " + item.Category).ToLowerInvariant().IndexOf(q) < 0) continue;
                    ListViewItem row = new ListViewItem(new string[] { item.Title, v.Title, item.Category });
                    row.Tag = new WornItem(item.Id, v.Id, slot, options[0].Mesh);
                    if (now != null && now.Item == item.Id && now.Variant == v.Id) chosen = row;
                    choiceList.Items.Add(row);
                }
            }
            choiceList.EndUpdate();
            if (chosen != null && choiceList.IsHandleCreated) { chosen.Selected = true; choiceList.EnsureVisible(chosen.Index); }
            filling = false;
        }
    }

    // A tickable list of clothing: the owned list of the story save.
    class OwnedPanel : Panel
    {
        readonly WardrobeTables tables;
        readonly List<Pair> rows;
        readonly Action changed;
        List<Pair> owned = new List<Pair>();          // the save's list, edited in place
        HashSet<long> locked = new HashSet<long>();   // worn right now: owned whatever the tick says
        bool filling;
        readonly ListView list = new ListView();
        readonly TextBox search = new TextBox();
        readonly ComboBox categoryBox = new ComboBox();
        readonly CheckBox ownedOnly = new CheckBox();
        readonly Label note = new Label();

        public OwnedPanel(WardrobeTables tables, List<Pair> rows, Action changed)
        {
            this.tables = tables; this.rows = rows; this.changed = changed;
            Dock = DockStyle.Fill;
            FlowLayoutPanel filter = new FlowLayoutPanel();
            filter.Dock = DockStyle.Top; filter.AutoSize = true; filter.Padding = new Padding(6, 6, 6, 4); filter.WrapContents = false;
            Label caption = new Label(); caption.Text = "Search"; caption.AutoSize = true; caption.Margin = new Padding(6, 7, 2, 0);
            filter.Controls.Add(caption);
            search.Width = 180; search.TextChanged += delegate { Fill(); };
            filter.Controls.Add(search);
            categoryBox.DropDownStyle = ComboBoxStyle.DropDownList; categoryBox.Width = 140;
            SortedSet<string> categories = new SortedSet<string>();
            foreach (Pair p in rows) categories.Add(tables.Item(p.Item).Category);
            categoryBox.Items.Add("All categories");
            foreach (string c in categories) categoryBox.Items.Add(c);
            categoryBox.SelectedIndex = 0;
            categoryBox.SelectedIndexChanged += delegate { Fill(); };
            filter.Controls.Add(categoryBox);
            ownedOnly.Text = "Owned only"; ownedOnly.AutoSize = true; ownedOnly.Margin = new Padding(8, 7, 8, 0);
            ownedOnly.CheckedChanged += delegate { Fill(); };
            filter.Controls.Add(ownedOnly);
            Button all = new Button(), none = new Button();
            all.Text = "Tick all shown"; all.AutoSize = true; all.Click += delegate { SetShown(true); };
            none.Text = "Untick all shown"; none.AutoSize = true; none.Click += delegate { SetShown(false); };
            filter.Controls.Add(all); filter.Controls.Add(none);
            note.AutoSize = true; note.Margin = new Padding(12, 7, 0, 0); note.ForeColor = SystemColors.GrayText;
            filter.Controls.Add(note);

            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.CheckBoxes = true; list.HideSelection = false;
            list.Columns.Add("Item", 250); list.Columns.Add("Colour / style", 250); list.Columns.Add("Category", 110); list.Columns.Add("", 110);
            list.ItemCheck += delegate(object o, ItemCheckEventArgs e)
            {
                if (filling) return;
                Pair p = (Pair)list.Items[e.Index].Tag;
                if (e.NewValue != CheckState.Checked && locked.Contains(p.Key)) { e.NewValue = CheckState.Checked; return; }
                Set(p, e.NewValue == CheckState.Checked);
                changed();
            };
            list.HandleCreated += delegate { BeginInvoke((Action)Fill); };   // after OnHandleCreated, see WardrobePanel
            Controls.Add(list); Controls.Add(filter);
        }

        public void Show(List<Pair> owned, List<WornItem> outfit)
        {
            this.owned = owned;
            locked.Clear();
            foreach (WornItem w in outfit) if (tables.IsClothing(w.Item)) locked.Add(new Pair(w.Item, w.Variant).Key);
            Fill();
        }
        bool Has(Pair p)
        {
            if (locked.Contains(p.Key)) return true;
            foreach (Pair q in owned) if (q.Key == p.Key) return true;
            return false;
        }
        void Set(Pair p, bool on)
        {
            owned.RemoveAll(delegate(Pair q) { return q.Key == p.Key; });
            if (on) owned.Add(p);
        }
        void SetShown(bool on)
        {
            foreach (ListViewItem row in list.Items)
            {
                Pair p = (Pair)row.Tag;
                if (on || !locked.Contains(p.Key)) Set(p, on);
            }
            changed(); Fill();
        }
        void Fill()
        {
            string q = search.Text.Trim().ToLowerInvariant();
            string category = categoryBox.SelectedIndex > 0 ? (string)categoryBox.SelectedItem : null;
            int count = 0;
            filling = true;
            list.BeginUpdate(); list.Items.Clear();
            foreach (Pair p in rows)
            {
                WardrobeItem item = tables.Item(p.Item);
                bool has = Has(p);
                if (has) count++;
                if (ownedOnly.Checked && !has) continue;
                if (category != null && item.Category != category) continue;
                string style = "";
                foreach (WardrobeVariant v in item.Variants) if (v.Id == p.Variant) style = v.Title;
                if (q.Length > 0 && (item.Title + " " + style + " " + item.Category).ToLowerInvariant().IndexOf(q) < 0) continue;
                ListViewItem row = new ListViewItem(new string[] { item.Title, style, item.Category, locked.Contains(p.Key) ? "wearing" : "" });
                row.Tag = p;
                list.Items.Add(row);
                // A tick set before the list has its window crashes .NET's ListView when the window
                // is made (or remade); the list is refilled on HandleCreated instead.
                if (list.IsHandleCreated) row.Checked = has;
            }
            list.EndUpdate();
            filling = false;
            note.Text = "Owns " + count + " of " + rows.Count;
        }
    }

    class MainForm : Form
    {
        string dist;
        GameData game;
        GameIcons icons;
        readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>();
        Tables tables;
        WardrobeTables wardrobe;
        WardrobePanel storyWardrobe, onlineWardrobe;
        OwnedPanel storyOwned;
        readonly ListView garageList = new ListView(), optionList = new ListView();
        readonly ComboBox modelBox = new ComboBox(), partBox = new ComboBox(), paletteBox = new ComboBox(), colourBox = new ComboBox();
        readonly Label garageNote = new Label(), optionTitle = new Label();
        readonly Button stockButton = new Button();
        // The variant row of the garage: variant, one box per part group, the material bytes.
        readonly FlowLayoutPanel variantRow = Row();
        readonly TextBox variantBytes = new TextBox();
        readonly TabControl wardrobeTabs = new TabControl();

        // Story save.
        StorySave story;
        bool storyDirty;
        readonly CarriedWeapon[] slots = new CarriedWeapon[8];   // one weapon per wheel slot; 0 is the fists
        readonly List<CarriedWeapon> extras = new List<CarriedWeapon>();
        readonly string[] quick = new string[4];
        readonly List<string> extraFood = new List<string>();
        readonly List<string> storyFiles = new List<string>();
        readonly TabControl sections = new TabControl(), storyTabs = new TabControl();
        readonly ComboBox storyBox = new ComboBox(), ownerBox = new ComboBox();
        readonly Button storySave = new Button(), storyReload = new Button(), storyFolder = new Button(), storyRestore = new Button();
        readonly NumericUpDown storyCash = new NumericUpDown(), respectBox = new NumericUpDown();
        readonly ComboBox startBox = new ComboBox();
        readonly NumericUpDown loadedBox = new NumericUpDown(), spareBox = new NumericUpDown();
        readonly Label respectNote = new Label(), ammoTitle = new Label(), stashNote = new Label(), hoodNote = new Label();
        readonly Button fillAmmo = new Button();
        readonly WheelPanel wheel = new WheelPanel();
        readonly CheckedListBox stashList = new CheckedListBox(), homieList = new CheckedListBox();
        readonly ListView hoodList = new ListView(), cheatList = new ListView();
        readonly TextBox cheatSearch = new TextBox();

        // Online character.
        Character character;
        readonly HashSet<long> owned = new HashSet<long>();
        readonly HashSet<long> worn = new HashSet<long>();
        readonly List<Pair> rows = new List<Pair>();   // everything that can be shown
        bool filling, dirty;
        public bool Quiet;   // testing: don't take the focus
        protected override bool ShowWithoutActivation { get { return Quiet; } }

        readonly ComboBox playerBox = new ComboBox();
        readonly NumericUpDown cashBox = new NumericUpDown();
        readonly Button saveButton = new Button(), reloadButton = new Button(), folderButton = new Button();
        readonly Button restoreButton = new Button();
        readonly TextBox searchBox = new TextBox();
        readonly ComboBox categoryBox = new ComboBox();
        readonly CheckBox ownedOnly = new CheckBox();
        readonly Button tickAll = new Button(), untickAll = new Button();
        readonly ListView itemList = new ListView();
        readonly TabPage wornPage = new TabPage("Wearing"), wardrobePage = new TabPage("Wardrobe");
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel statusText = new ToolStripStatusLabel();

        public MainForm()
        {
            Text = "Saints Reborn - Save Editor (unofficial)";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(900, 680);
            MinimumSize = new Size(700, 420);
            StartPosition = FormStartPosition.CenterScreen;

            FlowLayoutPanel top = Row();
            top.Controls.Add(Caption("Player"));
            playerBox.DropDownStyle = ComboBoxStyle.DropDownList; playerBox.Width = 150;
            playerBox.SelectedIndexChanged += delegate { if (!filling) LoadCharacter(); };
            top.Controls.Add(playerBox);
            top.Controls.Add(Caption("Cash  $"));
            cashBox.Maximum = 21000000; cashBox.DecimalPlaces = 2; cashBox.ThousandsSeparator = true;
            cashBox.Increment = 1000; cashBox.Width = 130; cashBox.TextAlign = HorizontalAlignment.Right;
            cashBox.ValueChanged += delegate { if (!filling) Changed(); };
            top.Controls.Add(cashBox);
            saveButton.Text = "Save"; saveButton.AutoSize = true; saveButton.Click += delegate { SaveCharacter(); };
            reloadButton.Text = "Reload"; reloadButton.AutoSize = true; reloadButton.Click += delegate { LoadCharacter(); };
            folderButton.Text = "Game folder..."; folderButton.AutoSize = true;
            folderButton.Click += delegate { if (ChooseFolder()) LoadGame(); };
            restoreButton.Text = "Restore backup..."; restoreButton.AutoSize = true;
            restoreButton.Click += delegate { RestoreBackup(); };
            top.Controls.Add(saveButton); top.Controls.Add(reloadButton); top.Controls.Add(restoreButton);
            top.Controls.Add(folderButton);

            FlowLayoutPanel filter = Row();
            filter.Controls.Add(Caption("Search"));
            searchBox.Width = 180; searchBox.TextChanged += delegate { FillItems(); };
            filter.Controls.Add(searchBox);
            categoryBox.DropDownStyle = ComboBoxStyle.DropDownList; categoryBox.Width = 140;
            categoryBox.SelectedIndexChanged += delegate { if (!filling) FillItems(); };
            filter.Controls.Add(categoryBox);
            ownedOnly.Text = "Owned only"; ownedOnly.AutoSize = true; ownedOnly.Margin = new Padding(8, 7, 8, 0);
            ownedOnly.CheckedChanged += delegate { FillItems(); };
            filter.Controls.Add(ownedOnly);
            tickAll.Text = "Tick all shown"; tickAll.AutoSize = true; tickAll.Click += delegate { SetShown(true); };
            untickAll.Text = "Untick all shown"; untickAll.AutoSize = true; untickAll.Click += delegate { SetShown(false); };
            filter.Controls.Add(tickAll); filter.Controls.Add(untickAll);

            SetUpList(itemList, true, "Item", 250, "Colour / style", 250, "Category", 110, "", 110);
            itemList.ItemCheck += ItemCheck;
            itemList.HandleCreated += delegate { BeginInvoke((Action)FillItems); };

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            TabPage itemsPage = new TabPage("Items (tick = owned)");
            itemsPage.Controls.Add(itemList); itemsPage.Controls.Add(filter);
            tabs.TabPages.Add(itemsPage); tabs.TabPages.Add(wornPage);

            status.Items.Add(statusText);
            TabPage onlinePage = new TabPage("Online character");
            onlinePage.Controls.Add(tabs); onlinePage.Controls.Add(top);
            sections.Dock = DockStyle.Fill;
            sections.TabPages.Add(BuildStoryPage()); sections.TabPages.Add(onlinePage);
            sections.SelectedIndexChanged += delegate { ShowStatus(); };
            Controls.Add(sections); Controls.Add(status);

            Shown += delegate { Start(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if ((dirty || storyDirty) && MessageBox.Show(this, "You have changes that are not saved. Close anyway?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No) e.Cancel = true;
            };
        }

        static FlowLayoutPanel Row()
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.Dock = DockStyle.Top; p.AutoSize = true; p.Padding = new Padding(6, 6, 6, 4); p.WrapContents = false;
            return p;
        }
        static Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = true; l.Margin = new Padding(6, 7, 2, 0);
            return l;
        }
        static void SetUpList(ListView list, bool checks, params object[] columns)
        {
            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true;
            list.CheckBoxes = checks; list.HideSelection = false;
            for (int i = 0; i < columns.Length; i += 2) list.Columns.Add((string)columns[i], (int)columns[i + 1]);
        }

        void Start()
        {
            dist = Game.FindDist();
            if (dist == null)
            {
                MessageBox.Show(this, "Saints Reborn was not found in the usual places.\n\nChoose the folder that contains saintsrow.exe " +
                    "(the \"dist\" folder inside your Saints Reborn folder).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (!ChooseFolder()) { Close(); return; }
            }
            LoadGame();
        }

        bool ChooseFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose the folder that contains saintsrow.exe (the \"dist\" folder).";
                dlg.ShowNewFolderButton = false;
                if (dist != null) dlg.SelectedPath = dist;
                while (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string pick = dlg.SelectedPath;
                    if (!Game.IsDist(pick) && Game.IsDist(Path.Combine(pick, "dist"))) pick = Path.Combine(pick, "dist");
                    if (Game.IsDist(pick)) { dist = pick; Game.Remember(dist); return true; }
                    MessageBox.Show(this, "That folder has no game\\packfiles\\misc.vpp_xbox2 in it.", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            return false;
        }

        void LoadGame()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                game = new GameData(dist);
                tables = new Tables(game);
                icons = GameIcons.Open(game, dist);
                foreach (Image i in iconCache.Values) if (i != null) i.Dispose();
                iconCache.Clear();
                wardrobe = new WardrobeTables(game);
                HashSet<long> sold = new HashSet<long>();
                foreach (Pair p in tables.Store) sold.Add(p.Key);
                wardrobeTabs.TabPages.Clear(); wornPage.Controls.Clear();
                storyWardrobe = new WardrobePanel(wardrobe, null, delegate { StoryChanged(); if (story != null) storyOwned.Show(story.Owned, story.Outfit); });
                storyOwned = new OwnedPanel(wardrobe, wardrobe.Clothing, delegate { StoryChanged(); });
                onlineWardrobe = new WardrobePanel(wardrobe, sold, delegate { WornChanged(); });
                wardrobeTabs.TabPages.Add(Page("Wearing", storyWardrobe)); wardrobeTabs.TabPages.Add(Page("Clothing owned", storyOwned));
                wornPage.Controls.Add(onlineWardrobe);
                Game.Remember(dist);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "The game's item tables could not be read.\n\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Cursor = Cursors.Default;
            LoadStoryList();

            filling = true;
            playerBox.Items.Clear();
            foreach (string name in Game.Players(dist)) playerBox.Items.Add(name);
            filling = false;
            if (playerBox.Items.Count == 0)
            {
                character = null;
                itemList.Items.Clear();
                statusText.Text = "No multiplayer character yet. In the game, open MULTIPLAYER, change something in the wardrobe and save, then come back.";
                saveButton.Enabled = false;
                restoreButton.Enabled = false;
                return;
            }
            // Newest file first: the player who played last.
            int best = 0; DateTime newest = DateTime.MinValue;
            for (int i = 0; i < playerBox.Items.Count; i++)
            {
                DateTime t = File.GetLastWriteTime(Game.CharacterFile(dist, (string)playerBox.Items[i]));
                if (t > newest) { newest = t; best = i; }
            }
            playerBox.SelectedIndex = best;   // loads the character
        }

        // ---- Story save ----

        static Panel Fill(Control body, Control top)
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill; body.Dock = DockStyle.Fill;
            p.Controls.Add(body);
            if (top != null) p.Controls.Add(top);
            return p;
        }
        static TabPage Page(string title, Control content)
        {
            TabPage page = new TabPage(title);
            page.Controls.Add(content);
            return page;
        }
        static void Number(NumericUpDown box, decimal max, int width)
        {
            box.Maximum = max; box.ThousandsSeparator = true; box.Width = width; box.TextAlign = HorizontalAlignment.Right;
        }

        TabPage BuildStoryPage()
        {
            FlowLayoutPanel top = Row();
            top.Controls.Add(Caption("Save"));
            storyBox.DropDownStyle = ComboBoxStyle.DropDownList; storyBox.Width = 230;
            storyBox.SelectedIndexChanged += delegate { if (!filling) LoadStory(); };
            top.Controls.Add(storyBox);
            storySave.Text = "Save"; storySave.AutoSize = true; storySave.Click += delegate { SaveStory(); };
            storyReload.Text = "Reload"; storyReload.AutoSize = true; storyReload.Click += delegate { LoadStoryList(); };
            storyFolder.Text = "Game folder..."; storyFolder.AutoSize = true;
            storyFolder.Click += delegate { if (ChooseFolder()) LoadGame(); };
            storyRestore.Text = "Restore backup..."; storyRestore.AutoSize = true;
            storyRestore.Click += delegate { RestoreStory(); };
            top.Controls.Add(storySave); top.Controls.Add(storyReload); top.Controls.Add(storyRestore); top.Controls.Add(storyFolder);

            // Player
            FlowLayoutPanel player = Row();
            player.Dock = DockStyle.Fill; player.FlowDirection = FlowDirection.TopDown;
            FlowLayoutPanel cashRow = Row(), respectRow = Row();
            cashRow.Controls.Add(Caption("Cash  $"));
            Number(storyCash, 20000000, 130); storyCash.Increment = 1000;
            storyCash.ValueChanged += delegate { StoryChanged(); };
            cashRow.Controls.Add(storyCash);
            respectRow.Controls.Add(Caption("Respect"));
            Number(respectBox, 100000000, 130); respectBox.Increment = 100;
            respectBox.ValueChanged += delegate { StoryChanged(); };
            respectRow.Controls.Add(respectBox);
            respectNote.AutoSize = true; respectNote.Margin = new Padding(8, 7, 0, 0);
            respectRow.Controls.Add(respectNote);
            // Where the player starts: the save spot the game last saved at.
            FlowLayoutPanel startRow = Row();
            startRow.Controls.Add(Caption("Start at"));
            startBox.DropDownStyle = ComboBoxStyle.DropDownList; startBox.Width = 300;
            startBox.SelectedIndexChanged += delegate
            {
                SavePoint p = startBox.SelectedItem as SavePoint;
                if (filling || story == null || p == null) return;
                story.StartPoint = p.Hash; StoryChanged();
            };
            startRow.Controls.Add(startBox);
            Label startNote = new Label(); startNote.AutoSize = true; startNote.Margin = new Padding(8, 7, 0, 0);
            startNote.Text = "Pick a crib or save point you have unlocked.";
            startNote.ForeColor = SystemColors.GrayText;
            startRow.Controls.Add(startNote);
            player.Controls.Add(cashRow); player.Controls.Add(respectRow); player.Controls.Add(startRow);

            // Weapons
            FlowLayoutPanel ammo = Row();
            ammo.Dock = DockStyle.Bottom;
            ammoTitle.AutoSize = true; ammoTitle.Margin = new Padding(6, 7, 12, 0);
            ammoTitle.Font = new Font(Font, FontStyle.Bold);
            ammo.Controls.Add(ammoTitle);
            ammo.Controls.Add(Caption("Loaded"));
            Number(loadedBox, 0, 70); loadedBox.ValueChanged += delegate { AmmoChanged(); };
            ammo.Controls.Add(loadedBox);
            ammo.Controls.Add(Caption("Spare"));
            Number(spareBox, 0, 80); spareBox.ValueChanged += delegate { AmmoChanged(); };
            ammo.Controls.Add(spareBox);
            fillAmmo.Text = "Fill"; fillAmmo.AutoSize = true;
            fillAmmo.Click += delegate
            {
                CarriedWeapon w = slots[wheel.Selected];
                WeaponInfo info = w == null || game == null ? null : game.Weapon(w.Name);
                if (info == null) return;
                w.Clip = (uint)info.Magazine; w.Reserve = (uint)info.MaxRounds;
                StoryChanged(); ShowWheel();
            };
            ammo.Controls.Add(fillAmmo);
            wheel.BackColor = SystemColors.Control;
            wheel.SlotClicked += SlotMenu;
            wheel.QuickClicked += QuickMenu;

            // Crib weapons
            FlowLayoutPanel stashTop = Row();
            stashNote.AutoSize = true; stashNote.Margin = new Padding(6, 7, 12, 0);
            Button stashAll = new Button();
            stashAll.Text = "Unlock all"; stashAll.AutoSize = true;
            stashAll.Click += delegate { TickAll(stashList); };
            stashTop.Controls.Add(stashNote); stashTop.Controls.Add(stashAll);
            stashList.CheckOnClick = true; stashList.IntegralHeight = false;
            stashList.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                if (filling || story == null) return;
                story.SetStash(e.Index, e.NewValue == CheckState.Checked);
                StoryChanged(); ShowCounts();
            };

            // Neighbourhoods
            FlowLayoutPanel hoodTop = Row();
            hoodNote.AutoSize = true; hoodNote.Margin = new Padding(6, 7, 12, 0);
            hoodTop.Controls.Add(hoodNote);
            hoodTop.Controls.Add(Caption("Owner of the selected"));
            ownerBox.DropDownStyle = ComboBoxStyle.DropDownList; ownerBox.Width = 150;
            foreach (string g in StorySave.Gangs) ownerBox.Items.Add(g);
            ownerBox.SelectionChangeCommitted += delegate
            {
                if (story == null || ownerBox.SelectedIndex < 0) return;
                foreach (int i in hoodList.SelectedIndices) story.SetOwner(i, (uint)ownerBox.SelectedIndex);
                StoryChanged(); ShowHoods();
            };
            hoodTop.Controls.Add(ownerBox);
            Button hoodAll = new Button();
            hoodAll.Text = "Give all to the Saints"; hoodAll.AutoSize = true;
            hoodAll.Click += delegate
            {
                if (story == null) return;
                for (int i = 0; i < story.Hoods.Count; i++) story.SetOwner(i, 0);
                StoryChanged(); ShowHoods();
            };
            hoodTop.Controls.Add(hoodAll);
            SetUpList(hoodList, false, "Neighbourhood", 260, "Owner", 200);

            // Homies
            FlowLayoutPanel homieTop = Row();
            homieTop.Controls.Add(Caption("Homies you can call on the phone"));
            Button homieAll = new Button();
            homieAll.Text = "Unlock all"; homieAll.AutoSize = true; homieAll.Margin = new Padding(12, 3, 3, 3);
            homieAll.Click += delegate { TickAll(homieList); };
            homieTop.Controls.Add(homieAll);
            homieList.CheckOnClick = true; homieList.IntegralHeight = false;
            homieList.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                if (filling || story == null) return;
                story.SetHomie(e.Index, e.NewValue == CheckState.Checked);
                StoryChanged();
            };

            // Cheat codes
            FlowLayoutPanel cheatTop = Row();
            cheatTop.Controls.Add(Caption("Dial these on the phone in the game. Nothing here changes your save.   Search"));
            cheatSearch.Width = 160; cheatSearch.TextChanged += delegate { ShowCheats(); };
            cheatTop.Controls.Add(cheatSearch);
            SetUpList(cheatList, false, "Cheat", 320, "Number", 110, "Kind", 180);

            storyTabs.Dock = DockStyle.Fill;
            storyTabs.TabPages.Add(Page("Player", player));
            storyTabs.TabPages.Add(Page("Weapons", Fill(wheel, ammo)));
            wardrobeTabs.Dock = DockStyle.Fill;
            wardrobePage.Controls.Add(wardrobeTabs);
            storyTabs.TabPages.Add(wardrobePage);
            storyTabs.TabPages.Add(Page("Garage", BuildGarage()));
            storyTabs.TabPages.Add(Page("Crib weapons", Fill(stashList, stashTop)));
            storyTabs.TabPages.Add(Page("Neighbourhoods", Fill(hoodList, hoodTop)));
            storyTabs.TabPages.Add(Page("Homies", Fill(homieList, homieTop)));
            storyTabs.TabPages.Add(Page("Cheat codes", Fill(cheatList, cheatTop)));

            TabPage page = new TabPage("Story save");
            page.Controls.Add(storyTabs); page.Controls.Add(top);
            return page;
        }

        // Testing: which tab a picture shows.
        public void ShowPage(int section, int storyTab)
        {
            sections.SelectedIndex = section;
            storyTabs.SelectedIndex = storyTab;
        }

        static string SaveTitle(string file)
        {
            // Folder names look like 23@2425788128@2@10_05_26@03_47_57 (date and time at the end).
            string folder = Path.GetFileName(Path.GetDirectoryName(file));
            string[] parts = folder.Split('@');
            return parts.Length >= 5 ? parts[3].Replace('_', '/') + " " + parts[4].Replace('_', ':') : folder;
        }

        // Testing ("--ui-check"): opens every story save and clicks every car, reporting any exception.
        public string UiCheck()
        {
            StringBuilder sb = new StringBuilder();
            // Give every list its window first, one by one, naming the one that fails.
            List<Control> all = new List<Control>();
            Stack<Control> stack = new Stack<Control>(); stack.Push(this);
            while (stack.Count > 0) { Control c = stack.Pop(); all.Add(c); foreach (Control k in c.Controls) stack.Push(k); }
            string trace = Path.Combine(Path.GetTempPath(), "ui-check-lists.txt");
            File.WriteAllText(trace, "");
            foreach (Control c in all)
            {
                ListView lv = c as ListView;
                if (lv == null) continue;
                string what = "ListView '" + (lv.Columns.Count > 0 ? lv.Columns[0].Text : "?") + "' (" + lv.Items.Count + " rows, parent " + (lv.Parent == null ? "none" : lv.Parent.GetType().Name) + ")";
                lv.HandleDestroyed += delegate { File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff") + " destroyed " + what + "\r\n" + Environment.StackTrace + "\r\n"); };
                lv.HandleCreated += delegate { File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff") + " created " + what + " (" + lv.Items.Count + " rows)\r\n"); };
                try { IntPtr x = lv.Handle; sb.AppendLine(what + " ok"); }
                catch (Exception ex) { sb.AppendLine(what + " EXCEPTION: " + ex.Message); }
            }
            IntPtr h = garageList.Handle; h = optionList.Handle; h = variantRow.Handle;   // the garage tab is not shown: make the controls real
            // Visit every tab the way a click would, so each control gets its window.
            for (int s = 0; s < sections.TabPages.Count; s++)
            {
                try { sections.SelectedIndex = s; Application.DoEvents(); sb.AppendLine("section " + s + " " + sections.TabPages[s].Text + " ok"); }
                catch (Exception ex) { sb.AppendLine("section " + s + " EXCEPTION: " + ex); }
                if (s != 0) continue;
                for (int t = 0; t < storyTabs.TabPages.Count; t++)
                {
                    try { storyTabs.SelectedIndex = t; Application.DoEvents(); sb.AppendLine("  story tab " + t + " " + storyTabs.TabPages[t].Text + " ok"); }
                    catch (Exception ex) { sb.AppendLine("  story tab " + t + " " + storyTabs.TabPages[t].Text + " EXCEPTION: " + ex); }
                    for (int w = 0; storyTabs.TabPages[t] == wardrobePage && w < wardrobeTabs.TabPages.Count; w++)
                    {
                        try { wardrobeTabs.SelectedIndex = w; Application.DoEvents(); sb.AppendLine("    wardrobe tab " + w + " ok"); }
                        catch (Exception ex) { sb.AppendLine("    wardrobe tab " + w + " EXCEPTION: " + ex); }
                    }
                }
            }
            for (int i = 0; i < storyBox.Items.Count; i++)
            {
                try
                {
                    storyBox.SelectedIndex = i; LoadStory();
                    sb.AppendLine("save " + storyBox.Items[i] + ": " + (story == null ? "not loaded" : story.Vehicles.Count + " cars, start at '" + startBox.Text + "' of " + startBox.Items.Count));
                    if (story == null) continue;
                    for (int k = 0; k < garageList.Items.Count; k++)
                    {
                        garageList.Items[k].Selected = true;
                        ShowOptions();
                        sb.AppendLine("  car " + garageList.Items[k].SubItems[1].Text + ": variant row " + (variantRow.Controls.Count > 0 ? variantRow.Controls.Count + " controls, bytes '" + variantBytes.Text + "'" : "hidden") + ", option rows " + optionList.Items.Count + ", selected " + garageList.SelectedIndices.Count
                            + ", section " + story.Vehicles[k].SectionLength + " bytes, variants " + game.Variants.Variants(game.Vehicle(story.Vehicles[k].Hash).Name).Count + " " + game.Variants.LastError);
                        for (int o = 0; o < optionList.Items.Count; o++) { optionList.Items[o].Selected = true; ShowPicker(); }
                    }
                }
                catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
            }
            return sb.ToString();
        }

        void LoadStoryList()
        {
            storyFiles.Clear();
            if (dist != null) storyFiles.AddRange(Game.StorySaves(dist));
            filling = true;
            storyBox.Items.Clear();
            foreach (string f in storyFiles) storyBox.Items.Add(SaveTitle(f));
            filling = false;
            ShowCheats();
            if (storyFiles.Count == 0)
            {
                story = null; storyDirty = false;
                storySave.Enabled = false; storyRestore.Enabled = false;
                ShowStatus();
                return;
            }
            storyBox.SelectedIndex = 0;   // loads the save
        }

        void LoadStory()
        {
            if (storyBox.SelectedIndex < 0) return;
            try { story = new StorySave(storyFiles[storyBox.SelectedIndex]); }
            catch (Exception ex)
            {
                story = null;
                MessageBox.Show(this, "That save could not be read.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            for (int i = 0; i < 8; i++) slots[i] = null;
            extras.Clear(); extraFood.Clear();
            foreach (CarriedWeapon w in story.Weapons)
            {
                WeaponInfo info = game == null ? null : game.Weapon(w.Name);
                if (info != null && info.Slot >= 1 && info.Slot <= 7 && slots[info.Slot] == null) slots[info.Slot] = w;
                else extras.Add(w);
            }
            for (int i = 0; i < 4; i++) quick[i] = i < story.Food.Count ? story.Food[i] : null;
            for (int i = 4; i < story.Food.Count; i++) extraFood.Add(story.Food[i]);

            filling = true;
            storyCash.Value = Math.Max(0, Math.Min(storyCash.Maximum, story.Cash));
            respectBox.Value = Math.Min(respectBox.Maximum, story.Respect);
            respectNote.Text = "Next bar at " + story.RespectNeeded + ".";
            startBox.Items.Clear();
            if (game != null) foreach (SavePoint p in game.SavePoints) startBox.Items.Add(p);
            SavePoint start = game == null ? null : game.SavePointOf(story.StartPoint);
            if (start == null)
            {
                start = new SavePoint(); start.Hash = story.StartPoint; start.Title = "Unknown spot (" + story.StartPoint.ToString("x8") + ")";
                startBox.Items.Insert(0, start);
            }
            startBox.SelectedItem = start;
            stashList.Items.Clear();
            foreach (StashWeapon s in story.Stash)
            {
                string name = game == null ? null : game.NameOfHash(s.Hash);
                stashList.Items.Add(name != null ? game.Title(name) : "Weapon " + s.Hash.ToString("X8"), s.Unlocked);
            }
            homieList.Items.Clear();
            foreach (Homie h in story.Homies)
            {
                string title;
                if (game == null || !game.HomieTitles.TryGetValue(h.Name, out title)) title = h.Name;
                homieList.Items.Add(title, h.Unlocked);
            }
            filling = false;
            storyDirty = false;
            storySave.Enabled = true; storyRestore.Enabled = true;
            if (storyWardrobe != null) { storyWardrobe.Show(story.Outfit); storyOwned.Show(story.Owned, story.Outfit); }
            ShowHoods(); ShowWheel(); ShowCounts(); ShowGarage(); ShowStatus();
        }

        // ---- Garage ----

        Control BuildGarage()
        {
            FlowLayoutPanel top = Row();
            garageNote.AutoSize = true; garageNote.Margin = new Padding(6, 7, 12, 0);
            top.Controls.Add(garageNote);
            Button another = new Button(), remove = new Button(), repair = new Button();
            repair.Text = "Repair"; repair.AutoSize = true;
            repair.Click += delegate
            {
                if (story == null || garageList.SelectedIndices.Count == 0) return;
                GarageVehicle v = story.Vehicles[garageList.SelectedIndices[0]];
                VehicleInfo info = game.Vehicle(v.Hash);
                v.Repair(info != null ? info.MaxHitpoints : 50000); StoryChanged(); ShowGarage();
            };
            top.Controls.Add(repair);
            another.Text = "Another like it"; another.AutoSize = true;
            another.Click += delegate { if (story != null && garageList.SelectedIndices.Count > 0) { story.DuplicateVehicle(garageList.SelectedIndices[0]); StoryChanged(); ShowGarage(); } };
            remove.Text = "Remove"; remove.AutoSize = true;
            remove.Click += delegate { if (story != null && garageList.SelectedIndices.Count > 0) { story.Vehicles.RemoveAt(garageList.SelectedIndices[0]); StoryChanged(); ShowGarage(); } };
            top.Controls.Add(another); top.Controls.Add(remove);
            top.Controls.Add(Caption("Change to"));
            modelBox.DropDownStyle = ComboBoxStyle.DropDownList; modelBox.Width = 260;
            modelBox.SelectedIndexChanged += delegate
            {
                if (filling || story == null || garageList.SelectedIndices.Count == 0 || modelBox.SelectedItem == null) return;
                story.Vehicles[garageList.SelectedIndices[0]].Hash = ((VehicleInfo)modelBox.SelectedItem).Hash;
                StoryChanged(); ShowGarage();
            };
            top.Controls.Add(modelBox);
            SetUpList(garageList, false, "Car", 60, "Vehicle", 180, "Condition", 150, "Kind", 160, "Model name", 180);
            garageList.MultiSelect = false;
            garageList.SelectedIndexChanged += delegate
            {
                if (story == null || garageList.SelectedIndices.Count == 0) return;
                filling = true;
                modelBox.SelectedItem = game.Vehicle(story.Vehicles[garageList.SelectedIndices[0]].Hash);
                filling = false;
            };
            garageList.Dock = DockStyle.Top; garageList.Height = 150;
            garageList.SelectedIndexChanged += delegate { ShowOptions(); };

            // Paint and parts of the selected car: a row per slot of the model's own table.
            FlowLayoutPanel pick = Row();
            optionTitle.AutoSize = true; optionTitle.Margin = new Padding(6, 7, 12, 0); optionTitle.Font = new Font(Font, FontStyle.Bold);
            pick.Controls.Add(optionTitle);
            partBox.DropDownStyle = ComboBoxStyle.DropDownList; partBox.Width = 200;
            partBox.SelectedIndexChanged += delegate { if (!filling) OptionPicked(); };
            paletteBox.DropDownStyle = ComboBoxStyle.DropDownList; paletteBox.Width = 160;
            paletteBox.SelectedIndexChanged += delegate { if (!filling) { FillColours(0); OptionPicked(); } };
            colourBox.DropDownStyle = ComboBoxStyle.DropDownList; colourBox.Width = 200;
            colourBox.SelectedIndexChanged += delegate { if (!filling) OptionPicked(); };
            stockButton.Text = "Stock"; stockButton.AutoSize = true;
            stockButton.Click += delegate
            {
                GarageVehicle v = SelectedCar(); CustomSlot s = SelectedSlot();
                if (v == null || s == null) return;
                v.ClearCustom(s.Id); StoryChanged(); ShowOptions();
            };
            pick.Controls.Add(partBox); pick.Controls.Add(paletteBox); pick.Controls.Add(colourBox); pick.Controls.Add(stockButton);
            SetUpList(optionList, false, "Slot", 180, "Choice", 420);
            optionList.MultiSelect = false;
            optionList.SelectedIndexChanged += delegate { ShowPicker(); };
            Panel lower = Fill(optionList, pick);
            // The random looks (flashers, light bars, hoods...) of models with a variant table, above the paint row.
            variantRow.WrapContents = true;
            lower.Controls.Add(variantRow);

            Panel p = new Panel(); p.Dock = DockStyle.Fill;
            p.Controls.Add(lower); p.Controls.Add(garageList); p.Controls.Add(top);
            return p;
        }

        GarageVehicle SelectedCar() { return story != null && garageList.SelectedIndices.Count > 0 ? story.Vehicles[garageList.SelectedIndices[0]] : null; }
        CustomSlot SelectedSlot() { return optionList.SelectedIndices.Count > 0 ? (CustomSlot)optionList.SelectedItems[0].Tag : null; }

        void ShowOptions()
        {
            int keep = optionList.SelectedIndices.Count > 0 ? optionList.SelectedIndices[0] : -1;
            optionList.BeginUpdate(); optionList.Items.Clear();
            GarageVehicle v = SelectedCar();
            VehicleCustomTables custom = game == null ? null : game.VehicleCustom;
            VehicleInfo info = v == null || game == null ? null : game.Vehicle(v.Hash);
            if (v != null && custom != null && info != null)
            {
                List<CustomSlot> slots = custom.Slots(info.Name);
                foreach (CustomSlot s in slots)
                {
                    CustomEntry e = v.Entry(s.Id);
                    string choice = "Stock";
                    if (e != null)
                    {
                        choice = e.Component < s.Components.Count ? s.Components[e.Component].Name : "part " + e.Component;
                        if (e.Colors.Count > 0) choice = custom.ColorName(e.Colors[0]);
                    }
                    ListViewItem row = new ListViewItem(new string[] { s.Name, choice }); row.Tag = s;
                    optionList.Items.Add(row);
                }
                optionTitle.Text = slots.Count > 0 ? info.Title + ": pick a slot" : "The shops don't take a " + info.Title + ".";
            }
            else optionTitle.Text = v == null ? "Click a car for its paint and parts." : "The paint and parts tables were not found (misc2.vpp_xbox2).";
            if (keep >= 0 && keep < optionList.Items.Count && optionList.IsHandleCreated) optionList.Items[keep].Selected = true;
            optionList.EndUpdate();
            ShowPicker();
            ShowVariant();
        }

        // The variant section of the selected car: the variant, a box per part group (with "None"
        // for a group the game may leave out), and the material bytes as hex for now - the police
        // unit number is in there, but which byte is which digit is not known yet.
        void ShowVariant()
        {
            GarageVehicle v = SelectedCar();
            VehicleInfo info = v == null || game == null ? null : game.Vehicle(v.Hash);
            List<VehicleVariant> variants = info == null ? new List<VehicleVariant>() : game.Variants.Variants(info.Name);
            filling = true;
            variantRow.SuspendLayout();
            variantRow.Controls.Clear();
            bool show = variants.Count > 0 && v.SectionLength > 0;
            variantRow.Visible = show;
            if (show)
            {
                int which = Math.Min(v.SectionByte(0), variants.Count - 1);
                VehicleVariant variant = variants[which];
                variantRow.Controls.Add(Caption("Variant"));
                ComboBox box = new ComboBox(); box.DropDownStyle = ComboBoxStyle.DropDownList; box.Width = 130;
                foreach (VehicleVariant x in variants) box.Items.Add(x);
                box.SelectedIndex = which;
                box.SelectedIndexChanged += delegate
                {
                    if (filling || SelectedCar() == null) return;
                    SelectedCar().SetSectionByte(0, (byte)box.SelectedIndex); StoryChanged(); ShowVariant();
                };
                variantRow.Controls.Add(box);
                int at = 1;
                foreach (VariantGroup g in variant.Parts)
                {
                    int index = at++;
                    if (index >= v.SectionLength) break;
                    variantRow.Controls.Add(Caption(g.Name));
                    ComboBox pick = new ComboBox(); pick.DropDownStyle = ComboBoxStyle.DropDownList; pick.Width = 120;
                    foreach (string c in g.Choices) pick.Items.Add(c);
                    pick.Items.Add("None");
                    int now = v.SectionByte(index);
                    if (now > g.Choices.Count) pick.Items.Add("value " + now);
                    pick.SelectedIndex = Math.Min(now, pick.Items.Count - 1);
                    pick.SelectedIndexChanged += delegate
                    {
                        if (filling || SelectedCar() == null) return;
                        SelectedCar().SetSectionByte(index, (byte)pick.SelectedIndex); StoryChanged();
                    };
                    variantRow.Controls.Add(pick);
                }
                at += variant.Materials.Count;   // one pick per material group; the choices are rarely more than one
                variantRow.Controls.Add(Caption("Material bytes"));
                variantBytes.Width = 200; variantBytes.Text = v.SectionHex(Math.Min(at, v.SectionLength));
                int first = at;
                variantBytes.Tag = first;
                variantRow.Controls.Add(variantBytes);
                Button apply = new Button(); apply.Text = "Apply"; apply.AutoSize = true;
                apply.Click += delegate
                {
                    GarageVehicle car = SelectedCar();
                    if (car == null) return;
                    string[] parts = variantBytes.Text.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int k = 0; k < parts.Length && first + k < car.SectionLength; k++)
                    {
                        byte b;
                        if (byte.TryParse(parts[k], System.Globalization.NumberStyles.HexNumber, null, out b)) car.SetSectionByte(first + k, b);
                    }
                    StoryChanged(); ShowVariant();
                };
                variantRow.Controls.Add(apply);
                if (GarageVehicle.HasUnitNumber(info.Name, v.SectionLength))
                {
                    variantRow.Controls.Add(Caption("Unit number"));
                    NumericUpDown unit = new NumericUpDown(); unit.Minimum = 0; unit.Maximum = 99; unit.Width = 50;
                    unit.Value = v.UnitNumber;
                    unit.ValueChanged += delegate
                    {
                        if (filling || SelectedCar() == null) return;
                        SelectedCar().UnitNumber = (int)unit.Value; StoryChanged();
                        filling = true; variantBytes.Text = SelectedCar().SectionHex((int)variantBytes.Tag); filling = false;
                    };
                    variantRow.Controls.Add(unit);
                }
            }
            variantRow.ResumeLayout();
            filling = false;
        }

        void ShowPicker()
        {
            GarageVehicle v = SelectedCar(); CustomSlot s = SelectedSlot();
            filling = true;
            partBox.Items.Clear(); paletteBox.Items.Clear(); colourBox.Items.Clear();
            bool colour = s != null && s.IsColor;
            partBox.Visible = s != null && !colour; paletteBox.Visible = colour; colourBox.Visible = colour; stockButton.Visible = s != null;
            if (v != null && s != null)
            {
                CustomEntry e = v.Entry(s.Id);
                if (colour)
                {
                    foreach (CustomPalette p in game.VehicleCustom.Palettes) paletteBox.Items.Add(p);
                    if (e != null && e.Colors.Count > 0 && e.Colors[0].Palette < paletteBox.Items.Count)
                    {
                        paletteBox.SelectedIndex = e.Colors[0].Palette;
                        FillColours(e.Colors[0].Color);
                    }
                }
                else
                {
                    foreach (CustomComponent c in s.Components) partBox.Items.Add(c);
                    if (e != null && e.Component < partBox.Items.Count) partBox.SelectedIndex = e.Component;
                }
                optionTitle.Text = s.Name;
            }
            filling = false;
        }
        void FillColours(int select)
        {
            bool was = filling; filling = true;
            colourBox.Items.Clear();
            CustomPalette p = paletteBox.SelectedItem as CustomPalette;
            if (p != null) { foreach (string c in p.Colors) colourBox.Items.Add(c); if (select < colourBox.Items.Count) colourBox.SelectedIndex = select; }
            filling = was;
        }
        void OptionPicked()
        {
            GarageVehicle v = SelectedCar(); CustomSlot s = SelectedSlot();
            if (v == null || s == null) return;
            if (s.IsColor)
            {
                if (paletteBox.SelectedIndex < 0 || colourBox.SelectedIndex < 0) return;
                List<CustomColor> colors = new List<CustomColor>();
                for (int k = 0; k < Math.Max(1, s.Components[0].ColorSlots.Count); k++) colors.Add(new CustomColor(paletteBox.SelectedIndex, colourBox.SelectedIndex));
                v.SetCustom(s.Id, 0, colors);
            }
            else
            {
                if (partBox.SelectedIndex < 0) return;
                v.SetCustom(s.Id, partBox.SelectedIndex, new List<CustomColor>());
            }
            StoryChanged(); ShowOptions();
        }

        void ShowGarage()
        {
            if (story == null) return;
            int selected = garageList.SelectedIndices.Count > 0 ? garageList.SelectedIndices[0] : -1;
            if (modelBox.Items.Count == 0 && game != null)
                foreach (VehicleInfo v in game.Vehicles) modelBox.Items.Add(v);
            garageList.BeginUpdate();
            garageList.Items.Clear();
            foreach (GarageVehicle v in story.Vehicles)
            {
                VehicleInfo info = game == null ? null : game.Vehicle(v.Hash);
                uint max = info != null ? info.MaxHitpoints : 50000;
                string condition = !v.Damaged(max) ? "Mint" : (100 * v.Hitpoints / max) + "%" + (v.RepairBill > 0 ? ", $" + v.RepairBill.ToString("0") + " to fix" : "");
                garageList.Items.Add(new ListViewItem(new string[] { "#" + v.Id, info != null ? info.Title : "Unknown " + v.Hash.ToString("X8"),
                    condition, info != null ? info.Category : "", info != null ? info.Name : "" }));
            }
            if (selected >= 0 && selected < garageList.Items.Count && garageList.IsHandleCreated) garageList.Items[selected].Selected = true;
            garageList.EndUpdate();
            garageNote.Text = story.Vehicles.Count + " in the garage";
            ShowOptions();
        }

        void StoryChanged()
        {
            if (filling || story == null) return;
            storyDirty = true;
            ShowStatus();
        }

        void TickAll(CheckedListBox list)
        {
            for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true);
        }

        void ShowCounts()
        {
            if (story == null) return;
            int n = 0;
            foreach (StashWeapon s in story.Stash) if (s.Unlocked) n++;
            stashNote.Text = n + " of " + story.Stash.Count + " weapons in the crib's weapon cache";
        }

        void ShowHoods()
        {
            if (story == null) return;
            List<int> selected = new List<int>();
            foreach (int i in hoodList.SelectedIndices) selected.Add(i);
            int saints = 0;
            hoodList.BeginUpdate();
            hoodList.Items.Clear();
            foreach (Neighbourhood h in story.Hoods)
            {
                if (h.Owner == 0) saints++;
                hoodList.Items.Add(new ListViewItem(new string[] { h.Name, h.Owner < StorySave.Gangs.Length ? StorySave.Gangs[h.Owner] : "?" }));
            }
            foreach (int i in selected) if (i < hoodList.Items.Count && hoodList.IsHandleCreated) hoodList.Items[i].Selected = true;
            hoodList.EndUpdate();
            hoodNote.Text = saints + " of " + story.Hoods.Count + " held by the Saints";
        }

        void ShowCheats()
        {
            cheatList.BeginUpdate();
            cheatList.Items.Clear();
            string search = cheatSearch.Text.Trim().ToLowerInvariant();
            if (game != null)
                foreach (CheatInfo c in game.Cheats)
                    if (search.Length == 0 || (c.Name + " " + c.Category).ToLowerInvariant().IndexOf(search) >= 0)
                        cheatList.Items.Add(new ListViewItem(new string[] { c.Name, c.Number, c.Category }));
            cheatList.EndUpdate();
        }

        // The game's icon for an item, as a picture (null when the game files have none).
        Image IconOf(string item)
        {
            Image cached;
            if (iconCache.TryGetValue(item, out cached)) return cached;
            Image result = null;
            RgbaImage img = icons == null ? null : icons.Icon(item);
            if (img != null)
            {
                Bitmap bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                byte[] row = new byte[img.Width * 4];
                for (int y = 0; y < img.Height; y++)
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        int q = (y * img.Width + x) * 4;   // RGBA -> BGRA
                        row[x * 4] = img.Pixels[q + 2]; row[x * 4 + 1] = img.Pixels[q + 1]; row[x * 4 + 2] = img.Pixels[q]; row[x * 4 + 3] = img.Pixels[q + 3];
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
                }
                bmp.UnlockBits(bd);
                result = bmp;
            }
            iconCache[item] = result;
            return result;
        }

        // Puts the slots on the wheel and the selected weapon's ammo under it.
        void ShowWheel()
        {
            wheel.Labels[0] = "Fists"; wheel.Filled[0] = true; wheel.Ammo[0] = -1; wheel.Icons[0] = IconOf("fists");
            for (int i = 1; i < 8; i++)
            {
                CarriedWeapon w = slots[i];
                WeaponInfo info = w == null || game == null ? null : game.Weapon(w.Name);
                wheel.Labels[i] = w == null ? GameData.SlotNames[i] : game != null ? game.Title(w.Name) : w.Name;
                wheel.Filled[i] = w != null;
                wheel.Ammo[i] = info != null && info.MaxRounds > 0 ? (double)w.Reserve / info.MaxRounds : -1;
                wheel.Icons[i] = w == null ? null : IconOf(w.Name);
            }
            for (int i = 0; i < 4; i++)
            {
                wheel.Quick[i] = quick[i] == null ? null : game != null ? game.Title(quick[i]) : quick[i];
                wheel.QuickIcons[i] = quick[i] == null ? null : IconOf(quick[i]);
            }
            wheel.Invalidate();

            CarriedWeapon sel = slots[wheel.Selected];
            WeaponInfo selInfo = sel == null || game == null ? null : game.Weapon(sel.Name);
            bool hasAmmo = selInfo != null && selInfo.MaxRounds > 0;
            bool was = filling;
            filling = true;
            ammoTitle.Text = sel == null ? "Click a slot to choose a weapon. Click a quick slot to choose food."
                : GameData.SlotNames[wheel.Selected] + ": " + (selInfo != null ? selInfo.Title : sel.Name);
            loadedBox.Enabled = spareBox.Enabled = fillAmmo.Enabled = hasAmmo;
            loadedBox.Maximum = hasAmmo ? Math.Max(selInfo.Magazine, (int)sel.Clip) : 0;
            spareBox.Maximum = hasAmmo ? Math.Max(selInfo.MaxRounds, (int)sel.Reserve) : 0;
            loadedBox.Value = hasAmmo ? sel.Clip : 0;
            spareBox.Value = hasAmmo ? sel.Reserve : 0;
            filling = was;
        }

        void AmmoChanged()
        {
            if (filling || story == null || slots[wheel.Selected] == null) return;
            slots[wheel.Selected].Clip = (uint)loadedBox.Value;
            slots[wheel.Selected].Reserve = (uint)spareBox.Value;
            StoryChanged();
            ShowWheel();
        }

        // The drop-down of a wheel slot: the other weapons for that slot, or empty.
        void SlotMenu(int slot)
        {
            if (story == null || game == null) return;
            wheel.Selected = slot;
            ShowWheel();
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ImageScalingSize = new Size(42, 32);
            foreach (WeaponInfo info in game.Choices(slot))
            {
                WeaponInfo pick = info;
                ToolStripMenuItem item = new ToolStripMenuItem(info.Title, IconOf(info.Name));
                item.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                item.Checked = slots[slot] != null && string.Equals(slots[slot].Name, info.Name, StringComparison.OrdinalIgnoreCase);
                item.Click += delegate
                {
                    slots[slot] = new CarriedWeapon(pick.Name, (uint)pick.Magazine, (uint)pick.MaxRounds);
                    StoryChanged(); ShowWheel();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem empty = new ToolStripMenuItem("Empty");
            empty.Checked = slots[slot] == null;
            empty.Click += delegate { slots[slot] = null; StoryChanged(); ShowWheel(); };
            menu.Items.Add(empty);
            menu.Show(Cursor.Position);
        }

        void QuickMenu(int index)
        {
            if (story == null || game == null) return;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ImageScalingSize = new Size(42, 32);
            foreach (string food in game.Food)
            {
                string pick = food;
                ToolStripMenuItem item = new ToolStripMenuItem(game.Title(food), IconOf(food));
                item.ImageScaling = ToolStripItemImageScaling.SizeToFit;
                item.Checked = quick[index] == food;
                item.Click += delegate { quick[index] = pick; StoryChanged(); ShowWheel(); };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem empty = new ToolStripMenuItem("Empty");
            empty.Checked = quick[index] == null;
            empty.Click += delegate { quick[index] = null; StoryChanged(); ShowWheel(); };
            menu.Items.Add(empty);
            menu.Show(Cursor.Position);
        }

        // Puts an earlier copy of the save back; the current one is kept as a backup first.
        void RestoreStory()
        {
            if (story == null || !GameClosed()) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose the backup to restore";
                dlg.InitialDirectory = Path.GetDirectoryName(story.Path);
                dlg.Filter = "Save backups (game.dat.*)|game.dat.*|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    new StorySave(dlg.FileName);   // checks that it is a save
                    string keep = StorySave.Backup(story.Path);
                    File.Copy(dlg.FileName, story.Path, true);
                    int selected = storyBox.SelectedIndex;
                    LoadStory();
                    MessageBox.Show(this, "Restored.\n\nThe save you had just now is kept as\n" + Path.GetFileName(keep), Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The backup could not be restored.\n\n" + ex.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void SaveStory()
        {
            if (story == null || !GameClosed()) return;
            story.Cash = storyCash.Value;
            story.Respect = (uint)respectBox.Value;
            if (game != null)
            {
                story.Weapons = new List<CarriedWeapon>();
                for (int i = 1; i < 8; i++) if (slots[i] != null) story.Weapons.Add(slots[i]);
                story.Weapons.AddRange(extras);
                story.Food = new List<string>();
                foreach (string f in quick) if (f != null) story.Food.Add(f);
                story.Food.AddRange(extraFood);
            }
            try
            {
                if (wardrobe != null) story.OwnWorn(wardrobe);
                string backup = story.Save();
                int keep = storyBox.SelectedIndex;
                LoadStory();
                storyBox.SelectedIndex = keep;
                MessageBox.Show(this, "Saved.\n\nYour previous save is kept as\n" + Path.GetFileName(backup) +
                    "\nin the same folder. To undo, delete game.dat and rename that file to game.dat.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The save could not be written.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---- Online character ----

        void LoadCharacter()
        {
            if (playerBox.SelectedItem == null) return;
            try { character = new Character(Game.CharacterFile(dist, (string)playerBox.SelectedItem)); }
            catch (Exception ex)
            {
                character = null;
                MessageBox.Show(this, "That character file could not be read.\n\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            owned.Clear(); worn.Clear(); rows.Clear();
            foreach (Pair p in character.Owned) owned.Add(p.Key);
            foreach (WornItem w in character.Outfit) worn.Add(new Pair(w.Item, w.Variant).Key);

            HashSet<long> listed = new HashSet<long>();
            foreach (Pair p in tables.Store) if (listed.Add(p.Key)) rows.Add(p);
            foreach (Pair p in character.Owned) if (listed.Add(p.Key)) rows.Add(p);   // owned, not sold: keep

            filling = true;
            cashBox.Value = Math.Min(cashBox.Maximum, character.Cents / 100m);
            string keep = categoryBox.SelectedItem as string;
            SortedSet<string> categories = new SortedSet<string>();
            foreach (Pair p in rows) categories.Add(tables.Category(p.Item));
            categoryBox.Items.Clear();
            categoryBox.Items.Add("All categories");
            foreach (string c in categories) if (c.Length > 0) categoryBox.Items.Add(c);
            categoryBox.SelectedItem = keep != null && categoryBox.Items.Contains(keep) ? keep : "All categories";
            filling = false;

            if (onlineWardrobe != null) onlineWardrobe.Show(character.Outfit);

            dirty = false;
            saveButton.Enabled = true;
            restoreButton.Enabled = true;
            FillItems();
        }

        void FillItems()
        {
            if (character == null || filling) return;
            string search = searchBox.Text.Trim().ToLowerInvariant();
            string category = categoryBox.SelectedIndex > 0 ? (string)categoryBox.SelectedItem : null;
            filling = true;
            itemList.BeginUpdate();
            itemList.Items.Clear();
            foreach (Pair p in rows)
            {
                bool has = owned.Contains(p.Key);
                if (ownedOnly.Checked && !has) continue;
                string cat = tables.Category(p.Item);
                if (category != null && cat != category) continue;
                string title = tables.ItemTitle(p.Item), style = tables.VariantTitle(p.Item, p.Variant);
                if (search.Length > 0 && (title + " " + style + " " + cat).ToLowerInvariant().IndexOf(search) < 0) continue;
                ListViewItem row = new ListViewItem(new string[] { title, style, cat, worn.Contains(p.Key) ? "wearing" : "" });
                row.Tag = p;
                itemList.Items.Add(row);
                if (itemList.IsHandleCreated) row.Checked = has;   // see OwnedPanel.Fill
            }
            itemList.EndUpdate();
            filling = false;
            ShowStatus();
        }

        void ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (filling) return;
            Pair p = (Pair)itemList.Items[e.Index].Tag;
            if (e.NewValue != CheckState.Checked && worn.Contains(p.Key))
            {
                e.NewValue = CheckState.Checked;   // the character is wearing it
                return;
            }
            if (e.NewValue == CheckState.Checked) owned.Add(p.Key); else owned.Remove(p.Key);
            Changed();
        }

        void SetShown(bool tick)
        {
            if (character == null) return;
            foreach (ListViewItem row in itemList.Items)
            {
                Pair p = (Pair)row.Tag;
                if (tick) owned.Add(p.Key);
                else if (!worn.Contains(p.Key)) owned.Remove(p.Key);
            }
            dirty = true;
            FillItems();
        }

        void Changed() { dirty = true; ShowStatus(); }

        // The outfit changed: what is worn is owned too, and stays ticked.
        void WornChanged()
        {
            worn.Clear();
            foreach (WornItem w in character.Outfit)
            {
                long key = new Pair(w.Item, w.Variant).Key;
                worn.Add(key);
                if (tables.Items.ContainsKey(w.Item)) owned.Add(key);
            }
            dirty = true;
            FillItems();
        }

        void ShowStatus()
        {
            if (sections.SelectedIndex == 0)
            {
                statusText.Text = story == null ? "No story save found. Save the game once, then press Reload."
                    : (storyDirty ? "Not saved yet   |   " : "") + story.Path;
                return;
            }
            if (character == null) return;
            statusText.Text = "Owns " + owned.Count + " of " + rows.Count + " items   |   showing " + itemList.Items.Count +
                (dirty ? "   |   not saved yet" : "") + "   |   " + character.Path;
        }

        bool GameClosed()
        {
            if (!Game.Running) return true;
            MessageBox.Show(this, "Saints Reborn is running. Close the game first: it would write over your changes.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Puts an earlier copy of the character back; the current one is kept
        // as a backup first.
        void RestoreBackup()
        {
            if (character == null || !GameClosed()) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose the backup to restore";
                dlg.InitialDirectory = Path.GetDirectoryName(character.Path);
                dlg.Filter = "Character backups (3_MPStorage.*)|3_MPStorage.*|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (new FileInfo(dlg.FileName).Length != Character.Size)
                        throw new InvalidDataException("That file is not a multiplayer character file.");
                    string keep = StorySave.Backup(character.Path);
                    File.Copy(dlg.FileName, character.Path, true);
                    LoadCharacter();
                    MessageBox.Show(this, "Restored.\n\nThe character you had just now is kept as\n" + Path.GetFileName(keep), Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The backup could not be restored.\n\n" + ex.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void SaveCharacter()
        {
            if (character == null || !GameClosed()) return;
            // Keep the order the game wrote, then add the new ones.
            List<Pair> list = new List<Pair>();
            HashSet<long> done = new HashSet<long>();
            foreach (Pair p in character.Owned) if (owned.Contains(p.Key) && done.Add(p.Key)) list.Add(p);
            foreach (Pair p in rows) if (owned.Contains(p.Key) && done.Add(p.Key)) list.Add(p);
            try
            {
                string backup = character.Save((long)Math.Round(cashBox.Value * 100m), list);
                LoadCharacter();
                MessageBox.Show(this, "Saved.\n\nYour previous character is kept as\n" + Path.GetFileName(backup) +
                    "\nin the same folder. To undo, delete 3_MPStorage and rename that file to 3_MPStorage.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The character could not be saved.\n\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static class Program
    {
        // "--check <dist folder> <report file>": reads the game tables and every
        // character without opening a window, for testing.
        static int Check(string dist, string report)
        {
            StringBuilder sb = new StringBuilder();
            GameData game = new GameData(dist);
            Tables tables = new Tables(game);
            sb.AppendLine("weapons " + game.Weapons.Count + ", offered " + game.Offered.Count + ", food " + game.Food.Count + ", cheats " + game.Cheats.Count);
            sb.AppendLine("items " + tables.Items.Count + ", sold in multiplayer " + tables.Store.Count);
            sb.AppendLine("save spots " + game.SavePoints.Count);
            foreach (SavePoint p in game.SavePoints) sb.AppendLine("  " + p.Hash.ToString("x8") + "  " + p.Title + "  [" + p.Name + "]");
            foreach (string player in Game.Players(dist))
            {
                Character c = new Character(Game.CharacterFile(dist, player));
                sb.AppendLine("player " + player + ": cash " + (c.Cents / 100m).ToString("0.00") + ", wearing " + c.Outfit.Count + ", owned " + c.Owned.Count);
                foreach (WornItem w in c.Outfit)
                    sb.AppendLine("  slot " + w.Slot + "  " + w.Item + "/" + w.Variant + "  " + tables.ItemTitle(w.Item) + " - " +
                        tables.VariantTitle(w.Item, w.Variant) + "  (" + tables.WearTitle(w.Item, w.Mesh) + ")");
            }
            File.WriteAllText(report, sb.ToString());
            return 0;
        }

        // "--story <game.dat> <report file> [weapon:<name>=<loaded>/<spare>|none] [food=<a,b>] [stash=1] [cash=N]":
        // reads a story save, applies the changes if any, and writes what it holds, for testing.
        static int Story(string[] args)
        {
            StorySave s = new StorySave(args[1]);
            bool changed = false;
            for (int i = 3; i < args.Length; i++)
            {
                int eq = args[i].IndexOf('=');
                if (eq < 0) continue;
                string key = args[i].Substring(0, eq), value = args[i].Substring(eq + 1);
                changed = true;
                if (key == "cash") s.Cash = decimal.Parse(value);
                else if (key == "start") s.StartPoint = GameData.Hash(value);   // start=<save spot name>, e.g. saintsrow_$crib_save000
                else if (key == "food") s.Food = new List<string>(value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (key == "stash" || key.StartsWith("stash:"))
                {
                    string name = key == "stash" ? "all" : key.Substring(6);
                    for (int k = 0; k < s.Stash.Count; k++) if (name == "all" || s.Stash[k].Hash == GameData.Hash(name)) s.SetStash(k, value == "1");
                }
                else if (key.StartsWith("wear:")) Dress(s.Outfit, key, value, args[1]);
                else if (key.StartsWith("custom:"))
                {
                    int at = -1;
                    for (int k = 0; k < s.Vehicles.Count; k++) if (s.Vehicles[k].Id.ToString() == key.Substring(7)) at = k;
                    string[] p = value.Split('/');
                    if (at < 0 || p.Length < 2) continue;
                    int slot = int.Parse(p[0]);
                    if (p[1] == "none") s.Vehicles[at].ClearCustom(slot);
                    else
                    {
                        List<CustomColor> colors = new List<CustomColor>();
                        if (p.Length == 4)
                        {
                            GameData gd = GameNear(args[1]);
                            VehicleInfo info = gd.Vehicle(s.Vehicles[at].Hash);
                            int n = 1;
                            foreach (CustomSlot cs in gd.VehicleCustom.Slots(info == null ? null : info.Name)) if (cs.Id == slot && cs.Components.Count > 0) n = Math.Max(1, cs.Components[0].ColorSlots.Count);
                            for (int k = 0; k < n; k++) colors.Add(new CustomColor(int.Parse(p[2]), int.Parse(p[3])));
                        }
                        s.Vehicles[at].SetCustom(slot, int.Parse(p[1]), colors);
                    }
                }
                else if (key.StartsWith("unit:"))   // unit:<car>=<number>  (police unit number)
                {
                    for (int k = 0; k < s.Vehicles.Count; k++) if (s.Vehicles[k].Id.ToString() == key.Substring(5)) s.Vehicles[k].UnitNumber = int.Parse(value);
                }
                else if (key.StartsWith("variant:"))   // variant:<car>=<byte index>:<value>
                {
                    string[] p = value.Split(':');
                    for (int k = 0; k < s.Vehicles.Count; k++)
                        if (s.Vehicles[k].Id.ToString() == key.Substring(8) && p.Length == 2) s.Vehicles[k].SetSectionByte(int.Parse(p[0]), byte.Parse(p[1]));
                }
                else if (key.StartsWith("vehicle:"))
                {
                    string which = key.Substring(8);
                    int at = -1;
                    for (int k = 0; k < s.Vehicles.Count; k++) if (s.Vehicles[k].Id.ToString() == (which == "add" ? value : which)) at = k;
                    if (at < 0) continue;
                    if (which == "add") s.DuplicateVehicle(at);
                    else if (value == "none") s.Vehicles.RemoveAt(at);
                    else if (value == "repair") { VehicleInfo v = GameNear(args[1]).Vehicle(s.Vehicles[at].Hash); s.Vehicles[at].Repair(v != null ? v.MaxHitpoints : 50000); }
                    else { VehicleInfo v = GameNear(args[1]).VehicleNamed(value); if (v != null) s.Vehicles[at].Hash = v.Hash; }
                }
                else if (key.StartsWith("own:"))
                {
                    string[] ids = key.Substring(4).Split('/');
                    Pair p = new Pair(int.Parse(ids[0]), int.Parse(ids[1]));
                    s.Owned.RemoveAll(delegate(Pair q) { return q.Key == p.Key; });
                    if (value == "1") s.Owned.Add(p);
                }
                else if (key.StartsWith("weapon:"))
                {
                    string name = key.Substring(7);
                    s.Weapons.RemoveAll(delegate(CarriedWeapon w) { return w.Name == name; });
                    string[] ammo = value.Split('/');
                    if (ammo.Length == 2) s.Weapons.Add(new CarriedWeapon(name, uint.Parse(ammo[0]), uint.Parse(ammo[1])));
                }
            }
            StringBuilder sb = new StringBuilder();
            if (changed)
            {
                if (cliWardrobe == null) cliWardrobe = new WardrobeTables(GameNear(args[1]));
                s.OwnWorn(cliWardrobe);
                sb.AppendLine("saved; previous file kept as " + Path.GetFileName(s.Save()));
            }
            sb.AppendLine("cash " + s.Cash.ToString("0.00") + ", respect " + s.Respect);
            SavePoint startAt = GameNear(args[1]).SavePointOf(s.StartPoint);
            sb.AppendLine("start " + s.StartPoint.ToString("x8") + (startAt != null ? " " + startAt.Name + " (" + startAt.Title + ")" : " unknown"));
            foreach (CarriedWeapon w in s.Weapons) sb.AppendLine("weapon " + w.Name + " " + w.Clip + "/" + w.Reserve);
            sb.AppendLine("food " + string.Join(",", s.Food.ToArray()));
            sb.AppendLine("outfit " + WardrobeTables.Describe(s.Outfit));
            sb.AppendLine("owned " + s.Owned.Count);
            StringBuilder g = new StringBuilder();
            foreach (GarageVehicle v in s.Vehicles) g.Append('#').Append(v.Id).Append(' ').Append(v.Hash.ToString("x8")).Append(' ');
            sb.AppendLine("garage " + g);
            foreach (GarageVehicle v in s.Vehicles)
            {
                VehicleInfo info = GameNear(args[1]).Vehicle(v.Hash);
                sb.AppendLine("variant #" + v.Id + " " + v.SectionHex(0) + (info != null && GarageVehicle.HasUnitNumber(info.Name, v.SectionLength) ? "  unit " + v.UnitNumber.ToString("00") : ""));
            }
            int unlocked = 0;
            foreach (StashWeapon w in s.Stash) if (w.Unlocked) unlocked++;
            sb.AppendLine("stash " + unlocked + "/" + s.Stash.Count + ", homies " + s.Homies.Count + ", neighbourhoods " + s.Hoods.Count);
            File.WriteAllText(args[2], sb.ToString());
            return 0;
        }

        // wear:<item id>=<variant id>/<mesh id> puts an item on (the slot follows from the mesh); wear:<slot>=none takes one off.
        static GameData cliGame;
        static WardrobeTables cliWardrobe;
        // The game files: the dist folder above a save, or the usual places.
        static GameData GameNear(string file)
        {
            if (cliGame != null) return cliGame;
            string dir = Path.GetDirectoryName(Path.GetFullPath(file));
            while (dir != null && !Game.IsDist(dir)) dir = Path.GetDirectoryName(dir);
            return cliGame = new GameData(dir ?? Game.FindDist());
        }
        static void Dress(List<WornItem> outfit, string key, string value, string nearFile)
        {
            int n = int.Parse(key.Substring(5));
            if (value == "none") { WardrobeTables.TakeOff(outfit, n); return; }
            string[] ids = value.Split('/');
            if (cliWardrobe == null) cliWardrobe = new WardrobeTables(GameNear(nearFile));
            int mesh = int.Parse(ids[1]), slot = cliWardrobe.SlotOfMesh(mesh);
            if (slot >= 0) WardrobeTables.Put(outfit, n, int.Parse(ids[0]), mesh, slot);
        }

        // "--mp <3_MPStorage> <report file> [cash=N] [wear:...]": reads an online character, applies
        // the changes if any, and writes what it holds, for testing.
        static int Mp(string[] args)
        {
            Character c = new Character(args[1]);
            long cents = (long)c.Cents;
            bool changed = false;
            for (int i = 3; i < args.Length; i++)
            {
                int eq = args[i].IndexOf('=');
                if (eq < 0) continue;
                string key = args[i].Substring(0, eq), value = args[i].Substring(eq + 1);
                changed = true;
                if (key == "cash") cents = (long)(decimal.Parse(value) * 100m);
                else if (key.StartsWith("wear:")) Dress(c.Outfit, key, value, args[1]);
            }
            StringBuilder sb = new StringBuilder();
            if (changed)
            {
                // What is worn and sold in multiplayer is owned too.
                List<Pair> owned = new List<Pair>(c.Owned);
                HashSet<long> have = new HashSet<long>();
                foreach (Pair p in owned) have.Add(p.Key);
                HashSet<long> sold = new HashSet<long>();
                foreach (Pair p in new Tables(GameNear(args[1])).Store) sold.Add(p.Key);
                foreach (WornItem w in c.Outfit)
                {
                    Pair p = new Pair(w.Item, w.Variant);
                    if (sold.Contains(p.Key) && have.Add(p.Key)) owned.Add(p);
                }
                sb.AppendLine("saved; previous file kept as " + Path.GetFileName(c.Save(cents, owned)));
            }
            sb.AppendLine("cash " + (c.Cents / 100m).ToString("0.00") + ", owned " + c.Owned.Count + ", wearing " + c.Outfit.Count);
            sb.AppendLine("outfit " + WardrobeTables.Describe(c.Outfit));
            File.WriteAllText(args[2], sb.ToString());
            return 0;
        }

        // "--picture <png file> [section] [story tab]": draws the window to a picture without putting
        // it on screen, for testing.
        static int Picture(string png, int section, int storyTab)
        {
            MainForm form = new MainForm();
            form.Quiet = true;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.Show();
            Application.DoEvents();
            form.ShowPage(section, storyTab);
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Dispose();
            return 0;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--check") return Check(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--resave")
            {
                // Testing: load a character file and save it unchanged.
                Character c = new Character(args[1]);
                c.Save(c.Cents, c.Owned);
                return 0;
            }
            if (args.Length >= 3 && args[0] == "--story") return Story(args);
            if (args.Length >= 3 && args[0] == "--mp") return Mp(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--picture")
                return Picture(args[1], args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? int.Parse(args[3]) : 0);
            // An unexpected error is written to %TEMP%\MpCharacterEditor.log and shown, instead of closing the program.
            string log = Path.Combine(Path.GetTempPath(), "MpCharacterEditor.log");
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss.fff") + "\r\n" + e.Exception + "\r\n\r\n");
                MessageBox.Show("Something went wrong (details in " + log + "):\n\n" + e.Exception.Message, "Saints Reborn - Save Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            MainForm main = new MainForm();
            if (args.Length == 2 && args[0] == "--ui-check")
            {
                // Testing: load every save, click every car, write a report, and close.
                main.Quiet = true;
                main.Shown += delegate { File.WriteAllText(args[1], main.UiCheck()); main.Close(); };
            }
            if (args.Length == 3 && args[0] == "--tab")
            {
                // Testing: open on a given tab.
                int section = int.Parse(args[1]), storyTab = int.Parse(args[2]);
                main.Shown += delegate { main.ShowPage(section, storyTab); };
            }
            Application.Run(main);
            return 0;
        }
    }
}

// Multiplayer character editor for Saints Reborn (unofficial).
//
// Edits the multiplayer character file the game keeps at
//   dist\game\545107D1\profile\<player name>\storage\3_MPStorage
// (27,724 bytes, big-endian):
//   0x0004  u32  cash in cents
//   0x0414  u32  number of worn items, then 16-byte records:
//                item id, variant id, slot, wear option (MeshID)
//   0x0C18  u32  number of owned items, then 12-byte records:
//                item id, variant id, 0          (room for 2048)
// Item names come from the player's own game files (customization_items.xtbl,
// customization_stores.xtbl and US_Strings.txt inside misc.vpp_xbox2); nothing
// from the game is included in this program.
//
// Build: build.bat (uses the C# compiler that comes with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace MpCharacterEditor
{
    static class Be
    {
        public static uint U32(byte[] d, int o)
        {
            return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
        }
        public static void Put(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }
    }

    // Saints Row (Xbox 360) packfile, version 3. Sections are aligned to 2048
    // bytes; flags at 0x14C: bit 0 compressed, bit 1 condensed (one zlib
    // stream for all files).
    class Packfile
    {
        class Entry { public string Name; public int At, Size, StoredSize; }
        const int Align = 2048;
        readonly byte[] data;
        readonly bool compressed, condensed;
        readonly int dataAt;
        readonly byte[] blob;
        readonly List<Entry> entries = new List<Entry>();

        static int Up(long x) { return (int)((x + Align - 1) & ~(long)(Align - 1)); }

        public Packfile(string path)
        {
            data = File.ReadAllBytes(path);
            if (data.Length < 0x800 || Be.U32(data, 0) != 0x51890ACE || Be.U32(data, 4) != 3)
                throw new InvalidDataException(Path.GetFileName(path) + " is not a Saints Row packfile.");
            uint flags = Be.U32(data, 0x14C);
            compressed = (flags & 1) != 0;
            condensed = (flags & 2) != 0;
            int count = (int)Be.U32(data, 0x154);
            int namesAt = Up(0x800 + (long)Be.U32(data, 0x15C));
            dataAt = Up(namesAt + (long)Be.U32(data, 0x160));
            int stored = dataAt;
            for (int i = 0; i < count; i++)
            {
                int e = 0x800 + i * 28;
                int nameAt = namesAt + (int)Be.U32(data, e);
                int end = nameAt;
                while (end < data.Length && data[end] != 0) end++;
                Entry entry = new Entry();
                entry.Name = Encoding.ASCII.GetString(data, nameAt, end - nameAt);
                entry.Size = (int)Be.U32(data, e + 16);
                entry.StoredSize = (int)Be.U32(data, e + 20);
                if (compressed && !condensed) { entry.At = stored; stored = Up(stored + (long)entry.StoredSize); }
                else entry.At = (int)Be.U32(data, e + 8);
                entries.Add(entry);
            }
            if (condensed)
            {
                int blobSize = (int)Be.U32(data, 0x164);
                blob = compressed ? Inflate(data, dataAt, (int)Be.U32(data, 0x168), blobSize) : Slice(data, dataAt, blobSize);
            }
        }

        static byte[] Slice(byte[] src, int at, int size)
        {
            byte[] r = new byte[size];
            Buffer.BlockCopy(src, at, r, 0, size);
            return r;
        }

        // zlib stream: 2-byte header, then deflate data.
        static byte[] Inflate(byte[] src, int at, int storedSize, int size)
        {
            byte[] r = new byte[size];
            using (MemoryStream ms = new MemoryStream(src, at + 2, Math.Max(0, storedSize - 2)))
            using (DeflateStream z = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < size)
                {
                    int n = z.Read(r, got, size - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            return r;
        }

        public byte[] Read(string name)
        {
            foreach (Entry e in entries)
            {
                if (!string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (condensed) return Slice(blob, e.At, e.Size);
                if (compressed) return Inflate(data, e.At, e.StoredSize, e.Size);
                return Slice(data, dataAt + e.At, e.Size);
            }
            throw new FileNotFoundException(name + " was not found in the packfile.");
        }
    }

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
        readonly Dictionary<string, string> strings = new Dictionary<string, string>();
        const string MpStore = "Multiplayer Purchase Clothing";

        public Tables(string distFolder)
        {
            Packfile pack = new Packfile(Path.Combine(distFolder, @"game\packfiles\misc.vpp_xbox2"));

            string text = Encoding.Unicode.GetString(pack.Read("US_Strings.txt"));
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim('\r', '﻿', ' ');
                int eq = line.IndexOf('=');
                if (eq > 0) strings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

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

    class Worn { public int Item, Variant, Slot, Mesh; }

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
        public readonly List<Worn> Wearing = new List<Worn>();

        public Character(string path)
        {
            Path = path;
            data = File.ReadAllBytes(path);
            if (data.Length != Size)
                throw new InvalidDataException("This file is " + data.Length + " bytes; a multiplayer character file is " + Size + ".");
            Cents = Be.U32(data, CashAt);
            int n = (int)Math.Min(Be.U32(data, WornCountAt), (uint)WornMax);
            for (int k = 0; k < n; k++)
            {
                int o = WornAt + 16 * k;
                Worn w = new Worn();
                w.Item = (int)Be.U32(data, o); w.Variant = (int)Be.U32(data, o + 4);
                w.Slot = (int)Be.U32(data, o + 8); w.Mesh = (int)Be.U32(data, o + 12);
                Wearing.Add(w);
            }
            n = (int)Math.Min(Be.U32(data, OwnedCountAt), (uint)OwnedMax);
            for (int k = 0; k < n; k++)
                Owned.Add(new Pair((int)Be.U32(data, OwnedAt + 12 * k), (int)Be.U32(data, OwnedAt + 12 * k + 4)));
        }

        // Writes cash and the owned list; returns the name of the backup.
        public string Save(long cents, List<Pair> owned)
        {
            if (owned.Count > OwnedMax) throw new InvalidOperationException("The file has room for " + OwnedMax + " owned items.");
            Be.Put(data, CashAt, (uint)cents);
            Be.Put(data, OwnedCountAt, (uint)owned.Count);
            Array.Clear(data, OwnedAt, OwnedMax * 12);
            for (int k = 0; k < owned.Count; k++)
            {
                Be.Put(data, OwnedAt + 12 * k, (uint)owned[k].Item);
                Be.Put(data, OwnedAt + 12 * k + 4, (uint)owned[k].Variant);
            }
            string backup = Path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(Path, backup, true);
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
        public static string CharacterFile(string dist, string player)
        {
            return Path.Combine(dist, @"game\545107D1\profile\" + player + @"\storage\3_MPStorage");
        }
    }

    class MainForm : Form
    {
        string dist;
        Tables tables;
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
        readonly ListView itemList = new ListView(), wornList = new ListView();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel statusText = new ToolStripStatusLabel();

        public MainForm()
        {
            Text = "Saints Reborn - Multiplayer Character Editor (unofficial)";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(860, 600);
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
            SetUpList(wornList, false, "Item", 230, "Colour / style", 230, "Category", 110, "Worn as", 150);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            TabPage itemsPage = new TabPage("Items (tick = owned)");
            itemsPage.Controls.Add(itemList); itemsPage.Controls.Add(filter);
            TabPage wornPage = new TabPage("Wearing now");
            wornPage.Controls.Add(wornList);
            tabs.TabPages.Add(itemsPage); tabs.TabPages.Add(wornPage);

            status.Items.Add(statusText);
            Controls.Add(tabs); Controls.Add(top); Controls.Add(status);

            Shown += delegate { Start(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (dirty && MessageBox.Show(this, "You have changes that are not saved. Close anyway?", Text,
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
                tables = new Tables(dist);
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

            filling = true;
            playerBox.Items.Clear();
            foreach (string name in Game.Players(dist)) playerBox.Items.Add(name);
            filling = false;
            if (playerBox.Items.Count == 0)
            {
                character = null;
                itemList.Items.Clear(); wornList.Items.Clear();
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
            foreach (Worn w in character.Wearing) worn.Add(new Pair(w.Item, w.Variant).Key);

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

            wornList.BeginUpdate();
            wornList.Items.Clear();
            foreach (Worn w in character.Wearing)
                wornList.Items.Add(new ListViewItem(new string[] { tables.ItemTitle(w.Item), tables.VariantTitle(w.Item, w.Variant),
                    tables.Category(w.Item), tables.WearTitle(w.Item, w.Mesh) }));
            wornList.EndUpdate();

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
                row.Checked = has;
                itemList.Items.Add(row);
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

        void ShowStatus()
        {
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
                    string keep = character.Path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                    File.Copy(character.Path, keep, true);
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
            Tables tables = new Tables(dist);
            sb.AppendLine("items " + tables.Items.Count + ", sold in multiplayer " + tables.Store.Count);
            foreach (string player in Game.Players(dist))
            {
                Character c = new Character(Game.CharacterFile(dist, player));
                sb.AppendLine("player " + player + ": cash " + (c.Cents / 100m).ToString("0.00") + ", wearing " + c.Wearing.Count + ", owned " + c.Owned.Count);
                foreach (Worn w in c.Wearing)
                    sb.AppendLine("  slot " + w.Slot + "  " + w.Item + "/" + w.Variant + "  " + tables.ItemTitle(w.Item) + " - " +
                        tables.VariantTitle(w.Item, w.Variant) + "  (" + tables.WearTitle(w.Item, w.Mesh) + ")");
            }
            File.WriteAllText(report, sb.ToString());
            return 0;
        }

        // "--picture <png file>": draws the window to a picture without putting
        // it on screen, for testing.
        static int Picture(string png)
        {
            MainForm form = new MainForm();
            form.Quiet = true;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.Show();
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--picture") return Picture(args[1]);
            Application.Run(new MainForm());
            return 0;
        }
    }
}

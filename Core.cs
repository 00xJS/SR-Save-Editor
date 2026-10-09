// Shared, window-free code: packfile reader, the game's own tables, and the
// story save (game.dat). The Mac version (mac/Sources/Core) does the same things.
//
// Story save: big-endian. A fixed header (0x7000 bytes) holds counts and
// pointers; the lists they point to follow it. Pointers are offsets from the
// start of the file.
//   0x0090  respect            0x00A0  cash in cents
//   0x0144  carried weapons: count, pointer to 16-byte records (name, count, loaded, spare)
//   0x0240  carried food: count, pointer to names
//   0x064C  outfit: count, then 16-byte records in the header (item, variant, slot, wear option)
//   0x0E50  clothing owned: count, then 12-byte records in the header (item, variant, 0); room for 2048
//   0x6E88  homies: count, pointer to 12-byte records (name, 0, unlocked)
//   0x6E90  neighbourhoods: count, pointer to 12-byte records (name, owner, flags)
//   0x6F8C  crib weapon cache: count, pointer to 8-byte records (name hash, flags)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
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

    class WeaponInfo { public string Name, Title, Class; public int Magazine, MaxRounds, Slot; }
    class CheatInfo { public string Name, Number, Category; }

    // Names and limits read from the player's own game files (misc.vpp_xbox2).
    class GameData
    {
        // Wheel slots, clockwise from the top.
        public static readonly string[] SlotNames = { "Unarmed", "Melee", "Pistol", "SMG", "Shotgun", "Rifle", "Launcher", "Thrown" };
        public readonly Packfile Pack;
        public readonly Dictionary<string, string> Strings = new Dictionary<string, string>();
        public readonly Dictionary<string, WeaponInfo> Weapons = new Dictionary<string, WeaponInfo>();   // by lower-case name
        public readonly List<string> Offered = new List<string>();   // sold in stores or kept at the crib
        public readonly List<string> Food = new List<string>();
        public readonly List<CheatInfo> Cheats = new List<CheatInfo>();
        public readonly Dictionary<string, string> HomieTitles = new Dictionary<string, string>();
        readonly Dictionary<uint, string> hashed = new Dictionary<uint, string>();

        static int SlotOfClass(string c)
        {
            switch (c)
            {
                case "bat": case "knife": case "nightstick": case "pimp slap": return 1;
                case "pistol": return 2;
                case "smg": return 3;
                case "shotgun": return 4;
                case "rifle": return 5;
                case "launcher": return 6;
                case "thrown": return 7;
            }
            return -1;
        }

        public GameData(string distFolder)
        {
            Pack = new Packfile(Path.Combine(distFolder, @"game\packfiles\misc.vpp_xbox2"));

            string text = Encoding.Unicode.GetString(Pack.Read("US_Strings.txt"));
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim('\r', '\uFEFF', ' ');
                int eq = line.IndexOf('=');
                if (eq > 0) Strings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            foreach (string block in Blocks("weapons.xtbl", "Weapon"))
            {
                string name = Tag(block, "Name");
                if (name == null) continue;
                WeaponInfo w = new WeaponInfo();
                w.Name = name; w.Title = Title(name);
                w.Class = (Tag(block, "Weapon_Class") ?? "").ToLowerInvariant();
                w.Magazine = Number(Tag(block, "Magazine_Size")); w.MaxRounds = Number(Tag(block, "Max_Rounds"));
                w.Slot = SlotOfClass(w.Class);
                Weapons[name.ToLowerInvariant()] = w;
            }
            HashSet<string> seen = new HashSet<string>();
            foreach (string file in new string[] { "store_weapons.xtbl", "crib_weapons.xtbl" })
                foreach (string block in Blocks(file, "Entry"))
                {
                    string w = Tag(block, "Weapon");
                    if (w == null) continue;
                    w = w.ToLowerInvariant();
                    if (!Weapons.ContainsKey(w) || !seen.Add(w)) continue;
                    Offered.Add(w);
                    hashed[Hash(w)] = w;
                }
            foreach (string block in Blocks("drugs.xtbl", "Drug"))
            {
                string n = Tag(block, "Name");
                if (n != null) Food.Add(n);
            }
            foreach (string block in Blocks("homies.xtbl", "Homie"))
            {
                string n = Tag(block, "Name"), t = Tag(block, "Display_Name"), s;
                if (n != null && t != null) HomieTitles[n] = Strings.TryGetValue(t, out s) ? s : t;
            }
            foreach (string block in Blocks("cheats.xtbl", "Cheats"))
            {
                string n = Tag(block, "Name"), number = Tag(block, "Number"), s;
                if (n == null || number == null) continue;
                CheatInfo c = new CheatInfo();
                c.Name = Strings.TryGetValue(Tag(block, "DisplayName") ?? "", out s) ? s : n;
                c.Number = number;
                c.Category = (Tag(block, "Category") ?? "").Replace("Entries:", "");
                Cheats.Add(c);
            }
        }

        public static int Number(string s)
        {
            int v;
            return s != null && int.TryParse(s, out v) ? v : 0;
        }

        // The top-level <element> blocks of one of the game's tables.
        public List<string> Blocks(string file, string element)
        {
            List<string> list = new List<string>();
            string text;
            try { text = Encoding.GetEncoding(28591).GetString(Pack.Read(file)); }
            catch (FileNotFoundException) { return list; }
            string[] parts = text.Split(new string[] { "<" + element + ">" }, StringSplitOptions.None);
            for (int i = 1; i < parts.Length; i++)
            {
                int end = parts[i].IndexOf("</" + element + ">", StringComparison.Ordinal);
                list.Add(end < 0 ? parts[i] : parts[i].Substring(0, end));
            }
            return list;
        }
        public static string Tag(string block, string name)
        {
            int a = block.IndexOf("<" + name + ">", StringComparison.Ordinal);
            if (a < 0) return null;
            a += name.Length + 2;
            int b = block.IndexOf("</" + name + ">", a, StringComparison.Ordinal);
            if (b < 0) return null;
            string v = block.Substring(a, b - a).Trim();
            return v.Length == 0 || v.IndexOf('<') >= 0 ? null : v;
        }

        public string Title(string name)
        {
            string s;
            if ((Strings.TryGetValue(name, out s) || Strings.TryGetValue(name.ToLowerInvariant(), out s)) && s.Length > 0) return s;
            string[] words = name.Replace('_', ' ').Split(' ');
            for (int i = 0; i < words.Length; i++)
                if (words[i].Length > 0) words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1).ToLowerInvariant();
            return string.Join(" ", words);
        }
        public WeaponInfo Weapon(string name)
        {
            WeaponInfo w;
            return Weapons.TryGetValue(name.ToLowerInvariant(), out w) ? w : null;
        }
        public List<WeaponInfo> Choices(int slot)
        {
            List<WeaponInfo> list = new List<WeaponInfo>();
            foreach (string n in Offered) if (Weapons[n].Slot == slot) list.Add(Weapons[n]);
            return list;
        }
        public string NameOfHash(uint hash)
        {
            string s;
            return hashed.TryGetValue(hash, out s) ? s : null;
        }

        // The game's name hash: CRC-32 (reflected, start 0, no final inversion) of the lower-case name.
        public static uint Hash(string name)
        {
            uint c = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            {
                c ^= b;
                for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }
            return c;
        }
    }

    class RgbaImage { public int Width, Height; public byte[] Pixels; }

    // The game's own HUD icons, read from the player's files: bitmap_sheets.xtbl
    // (misc.vpp_xbox2) says where each icon sits on a sheet, and the sheets are
    // textures in interface-backend.peg_xbox2 (pegfiles.vpp_xbox2).
    //
    // The texture decoding (Xenos tiling, DXT3/DXT5) is adapted from
    // tools/glyphgen in Saints Reborn by Whompay (Apache License 2.0; see
    // THIRD_PARTY_NOTICES.md).
    class GameIcons
    {
        class Sprite { public string Sheet; public int X, Y, Width, Height; }
        class Texture { public int Offset, Width, Height, Format; }
        readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
        readonly Dictionary<string, RgbaImage> decoded = new Dictionary<string, RgbaImage>();
        readonly Dictionary<string, string> bitmapOf = new Dictionary<string, string>();   // item name (lower case) -> icon name
        readonly byte[] peg;

        // Returns null when the files are not there.
        public static GameIcons Open(GameData game, string distFolder)
        {
            try { return new GameIcons(game, distFolder); }
            catch (Exception) { return null; }
        }

        GameIcons(GameData game, string distFolder)
        {
            string text = Encoding.GetEncoding(28591).GetString(game.Pack.Read("bitmap_sheets.xtbl"));
            string[] sheets = text.Split(new string[] { "<BitmapSheets>" }, StringSplitOptions.None);
            for (int i = 1; i < sheets.Length; i++)
            {
                string name = GameData.Tag(sheets[i], "Name");
                if (name == null) continue;
                string[] images = sheets[i].Split(new string[] { "<Properties>" }, StringSplitOptions.None);
                for (int k = 1; k < images.Length; k++)
                {
                    string n = GameData.Tag(images[k], "Name");
                    if (n == null) continue;
                    Sprite s = new Sprite();
                    s.Sheet = name; s.X = GameData.Number(GameData.Tag(images[k], "StartX")); s.Y = GameData.Number(GameData.Tag(images[k], "StartY"));
                    s.Width = GameData.Number(GameData.Tag(images[k], "ImageWidth")); s.Height = GameData.Number(GameData.Tag(images[k], "ImageHeight"));
                    if (s.Width > 0 && s.Height > 0) sprites[n] = s;
                }
            }
            Packfile pegs = new Packfile(Path.Combine(distFolder, @"game\packfiles\pegfiles.vpp_xbox2"));
            peg = pegs.Read("interface-backend.peg_xbox2");
            // A peg file: u16 texture count at 0x10, then 0x48-byte entries from 0x18:
            // u32 data offset, u16 width, u16 height, u16 format, ..., name at +0x16.
            int count = (peg[0x10] << 8) | peg[0x11];
            for (int j = 0; j < count; j++)
            {
                int e = 0x18 + j * 0x48;
                if (e + 0x48 > peg.Length) break;
                Texture t = new Texture();
                t.Offset = (int)Be.U32(peg, e); t.Width = (peg[e + 4] << 8) | peg[e + 5]; t.Height = (peg[e + 6] << 8) | peg[e + 7]; t.Format = (peg[e + 8] << 8) | peg[e + 9];
                StringBuilder nm = new StringBuilder();
                for (int c = e + 0x16; c < e + 0x46 && peg[c] != 0; c++) if (peg[c] >= 32) nm.Append((char)peg[c]);
                textures[nm.ToString().Replace(".tga", "")] = t;
            }
            foreach (string[] table in new string[][] { new string[] { "items_inventory.xtbl", "Inventory_Item" }, new string[] { "drugs.xtbl", "Drug" } })
                foreach (string block in game.Blocks(table[0], table[1]))
                {
                    string n = GameData.Tag(block, "Name"), b = GameData.Tag(block, "Bitmap");
                    if (n != null && b != null) bitmapOf[n.ToLowerInvariant()] = b;
                }
            if (!bitmapOf.ContainsKey("the_fist")) bitmapOf["the_fist"] = "ui_hud_inv_burger";
        }

        // The icon for a weapon or food item, by the game's item name; "fists" for the hands.
        public RgbaImage Icon(string item)
        {
            string key = item.ToLowerInvariant();
            if (key == "fists") key = "fist";
            else if (bitmapOf.ContainsKey(key)) key = bitmapOf[key];
            Sprite s;
            if (!sprites.TryGetValue(key, out s)) return null;
            RgbaImage sheet = DecodeSheet(s.Sheet);
            if (sheet == null) return null;
            RgbaImage r = new RgbaImage();
            r.Width = s.Width; r.Height = s.Height; r.Pixels = new byte[s.Width * s.Height * 4];
            for (int y = 0; y < s.Height && s.Y + y < sheet.Height; y++)
            {
                int n = Math.Min(s.Width, sheet.Width - s.X) * 4;
                if (n <= 0) break;
                Buffer.BlockCopy(sheet.Pixels, ((s.Y + y) * sheet.Width + s.X) * 4, r.Pixels, y * s.Width * 4, n);
            }
            return r;
        }

        RgbaImage DecodeSheet(string name)
        {
            RgbaImage d;
            if (decoded.TryGetValue(name, out d)) return d;
            Texture t;
            if (!textures.TryGetValue(name, out t) || (t.Format != 0x191 && t.Format != 0x192) || t.Offset >= peg.Length) return null;
            d = Decode(peg, t.Offset, t.Width, t.Height, t.Format);
            decoded[name] = d;
            return d;
        }

        // ---- Xenos tiling and DXT (from glyphgen) ----
        static long Tiled(long x, long y, long w, int lb)
        {
            long aw = (w + 31) & ~31L;
            long macro = ((x >> 5) + (y >> 5) * (aw >> 5)) << (lb + 7);
            long micro = ((x & 7) + ((y & 6) << 2)) << lb;
            long off = macro + ((micro & ~15L) << 1) + (micro & 15) + ((y & 8) << (3 + lb)) + ((y & 1) << 4);
            return (((off & ~511L) << 3) + ((off & 448) << 2) + (off & 63) + ((y & 16) << 7) + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6)) >> lb;
        }
        static void C565(int c, int[] o) { o[0] = ((c >> 11) & 31) * 255 / 31; o[1] = ((c >> 5) & 63) * 255 / 63; o[2] = (c & 31) * 255 / 31; }

        static RgbaImage Decode(byte[] data, int start, int w, int h, int fmt)
        {
            RgbaImage img = new RgbaImage();
            img.Width = w; img.Height = h; img.Pixels = new byte[w * h * 4];
            int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4);
            byte[] b = new byte[16];
            int[][] p = { new int[3], new int[3], new int[3], new int[3] };
            int[] al = new int[16], pal = new int[8];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    long o = start + Tiled(bx, by, bw, 4) * 16;
                    if (o + 16 > data.Length) return null;
                    for (int i = 0; i < 16; i += 2) { b[i] = data[o + i + 1]; b[i + 1] = data[o + i]; }   // byte-swapped u16s
                    int c0 = b[8] | (b[9] << 8), c1 = b[10] | (b[11] << 8);
                    uint idx = (uint)b[12] | ((uint)b[13] << 8) | ((uint)b[14] << 16) | ((uint)b[15] << 24);
                    C565(c0, p[0]); C565(c1, p[1]);
                    for (int k = 0; k < 3; k++) { p[2][k] = (2 * p[0][k] + p[1][k]) / 3; p[3][k] = (p[0][k] + 2 * p[1][k]) / 3; }
                    if (fmt == 0x191)
                    {
                        ulong a = 0;
                        for (int i = 0; i < 8; i++) a |= (ulong)b[i] << (8 * i);
                        for (int i = 0; i < 16; i++) al[i] = (int)((a >> (4 * i)) & 15) * 17;
                    }
                    else
                    {
                        int a0 = b[0], a1 = b[1];
                        ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
                        pal[0] = a0; pal[1] = a1;
                        if (a0 > a1) for (int i = 0; i < 6; i++) pal[2 + i] = ((6 - i) * a0 + (i + 1) * a1) / 7;
                        else { for (int i = 0; i < 4; i++) pal[2 + i] = ((4 - i) * a0 + (i + 1) * a1) / 5; pal[6] = 0; pal[7] = 255; }
                        for (int i = 0; i < 16; i++) al[i] = pal[(int)((bits >> (3 * i)) & 7)];
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                        if (x >= w || y >= h) continue;
                        int[] c = p[(int)((idx >> (2 * i)) & 3)];
                        int q = (y * w + x) * 4;
                        img.Pixels[q] = (byte)c[0]; img.Pixels[q + 1] = (byte)c[1]; img.Pixels[q + 2] = (byte)c[2]; img.Pixels[q + 3] = (byte)al[i];
                    }
                }
            return img;
        }
    }

    // One worn thing: the slot number comes from the wear option (MeshID), see WardrobeTables.
    class WornItem
    {
        public int Item, Variant, Slot, Mesh;
        public WornItem(int item, int variant, int slot, int mesh) { Item = item; Variant = variant; Slot = slot; Mesh = mesh; }
        public string Key { get { return Item + "/" + Variant + "/" + Slot + "/" + Mesh; } }
    }
    class WardrobeVariant { public int Id; public string Title; }
    class WearOption { public int Mesh, Slot; public string Title; public override string ToString() { return Title; } }   // Slot -1 = a slot this editor does not know
    class WardrobeItem
    {
        public int Id; public string Name, Title, Category;
        public List<WardrobeVariant> Variants = new List<WardrobeVariant>();
        public List<WearOption> Options = new List<WearOption>();
        public List<WearOption> OptionsIn(int slot)
        {
            List<WearOption> r = new List<WearOption>();
            foreach (WearOption o in Options) if (o.Slot == slot) r.Add(o);
            return r;
        }
    }
    class BodySlot
    {
        public int Id; public string Name, Title;
        public BodySlot(int id, string name, string title) { Id = id; Name = name; Title = title; }
    }

    // Clothing, hair and jewellery from the player's own game files, by body slot.
    // The story save and the online character keep what is worn as 16-byte records:
    // item id, variant id, slot number, wear option (MeshID). The wear option decides
    // the slot: player_cust.xtbl lists every mesh under its slot.
    class WardrobeTables
    {
        // The slots this editor can dress, in the order shown. The numbers were read from
        // saves; the coat's (7) is the one gap between the shirts and the underwear.
        public static readonly BodySlot[] Slots = {
            new BodySlot(0, "hair head", "Hair"), new BodySlot(4, "hair eyebrows", "Eyebrows"), new BodySlot(53, "eyes", "Eyes"),
            new BodySlot(3, "hair sideburns", "Sideburns"), new BodySlot(1, "hair mustache", "Mustache"), new BodySlot(2, "hair beard", "Beard"),
            new BodySlot(12, "headwear", "Hat"), new BodySlot(24, "eyewear", "Eyewear"),
            new BodySlot(5, "undershirt", "Undershirt"), new BodySlot(6, "overshirt", "Overshirt"), new BodySlot(7, "coat", "Coat"),
            new BodySlot(19, "necklace high", "Necklace, high"), new BodySlot(18, "necklace medium", "Necklace, medium"), new BodySlot(17, "necklace low", "Necklace, low"),
            new BodySlot(21, "medallion medium", "Medallion, medium"), new BodySlot(20, "medallion low", "Medallion, low"), new BodySlot(22, "pennant high", "Pendant"),
            new BodySlot(15, "left wrist", "Left wrist"), new BodySlot(16, "right wrist", "Right wrist"),
            new BodySlot(25, "finger left index", "Left index finger"), new BodySlot(27, "finger left ring", "Left ring finger"),
            new BodySlot(26, "finger right index", "Right index finger"), new BodySlot(28, "finger right ring", "Right ring finger"),
            new BodySlot(8, "underwear", "Underwear"), new BodySlot(9, "pants", "Pants"), new BodySlot(10, "socks", "Socks"), new BodySlot(11, "shoes", "Shoes"),
        };
        public const int MaxWorn = 128;
        // Hair, faces and tattoos are not clothing: the game does not list them as owned.
        static readonly HashSet<string> Appearance = new HashSet<string>(new string[] { "Hair", "Eyebrows", "Eyes", "Sideburns", "Mustache", "Beard", "Skin", "Tattoo" });
        public bool IsClothing(int item)
        {
            WardrobeItem i;
            return Items.TryGetValue(item, out i) && !Appearance.Contains(i.Category);
        }
        // Every piece of clothing in the game files, one entry per colour, for the owned list of the story save.
        public List<Pair> Clothing
        {
            get
            {
                List<WardrobeItem> all = new List<WardrobeItem>(Items.Values);
                all.Sort(delegate(WardrobeItem a, WardrobeItem b) { return string.CompareOrdinal(a.Title, b.Title); });
                List<Pair> r = new List<Pair>();
                foreach (WardrobeItem i in all)
                    if (!Appearance.Contains(i.Category)) foreach (WardrobeVariant v in i.Variants) r.Add(new Pair(i.Id, v.Id));
                return r;
            }
        }
        public static BodySlot Slot(int id)
        {
            foreach (BodySlot s in Slots) if (s.Id == id) return s;
            return null;
        }

        public readonly Dictionary<int, WardrobeItem> Items = new Dictionary<int, WardrobeItem>();
        readonly Dictionary<int, string> meshSlot = new Dictionary<int, string>();   // MeshID -> the game's slot name

        public WardrobeTables(GameData game)
        {
            XmlDocument doc = new XmlDocument();
            using (MemoryStream ms = new MemoryStream(game.Pack.Read("player_cust.xtbl"))) doc.Load(ms);
            foreach (XmlNode s in doc.SelectNodes("/root/Table/Custom_player_slot"))
            {
                string slot = Str(s, "Slot");
                foreach (XmlNode m in s.SelectNodes("Components/Component/MeshID"))
                {
                    int id;
                    if (int.TryParse(m.InnerText.Trim(), out id)) meshSlot[id] = slot;
                }
            }
            Dictionary<string, int> numbers = new Dictionary<string, int>();
            foreach (BodySlot b in Slots) numbers[b.Name] = b.Id;
            doc = new XmlDocument();
            using (MemoryStream ms = new MemoryStream(game.Pack.Read("customization_items.xtbl"))) doc.Load(ms);
            foreach (XmlNode n in doc.SelectNodes("/root/Table/Customization_Item"))
            {
                int id;
                if (!int.TryParse(Str(n, "ID"), out id)) continue;
                WardrobeItem item = new WardrobeItem();
                item.Id = id; item.Name = Str(n, "Name");
                item.Title = Lookup(game, Str(n, "DisplayName"), item.Name);
                item.Category = Str(n, "Category");
                foreach (XmlNode v in n.SelectNodes("Variants/Variant"))
                {
                    int vid;
                    if (!int.TryParse(Str(v, "VariantID"), out vid)) continue;
                    WardrobeVariant variant = new WardrobeVariant();
                    variant.Id = vid; variant.Title = Lookup(game, Str(v, "Name"), Str(v, "Variant_Name"));
                    item.Variants.Add(variant);
                }
                foreach (XmlNode w in n.SelectNodes("Wear_Options/Wear_Option"))
                {
                    int mesh;
                    if (!int.TryParse(Str(w, "MeshID"), out mesh) || Str(w, "Disabled").ToLowerInvariant() == "yes") continue;
                    WearOption o = new WearOption();
                    o.Mesh = mesh; o.Title = Lookup(game, Str(w, "Name"), "Normal");
                    string slotName; int number;
                    o.Slot = meshSlot.TryGetValue(mesh, out slotName) && numbers.TryGetValue(slotName, out number) ? number : -1;
                    item.Options.Add(o);
                }
                Items[id] = item;
            }
        }
        static string Str(XmlNode n, string child)
        {
            XmlNode c = n[child];
            return c == null ? "" : c.InnerText.Trim();
        }
        static string Lookup(GameData game, string key, string fallback)
        {
            string s;
            if (key.Length > 0 && game.Strings.TryGetValue(key, out s) && s.Length > 0) return s;
            return game.Title(fallback);
        }

        // Items that can go in a slot, in the game's order.
        public List<WardrobeItem> Choices(int slot)
        {
            List<WardrobeItem> r = new List<WardrobeItem>();
            foreach (WardrobeItem i in Items.Values) if (i.OptionsIn(slot).Count > 0) r.Add(i);
            r.Sort(delegate(WardrobeItem a, WardrobeItem b) { return a.Id.CompareTo(b.Id); });
            return r;
        }
        public WardrobeItem Item(int id)
        {
            WardrobeItem i;
            return Items.TryGetValue(id, out i) ? i : null;
        }
        public string Title(WornItem w) { WardrobeItem i = Item(w.Item); return i != null ? i.Title : "Item " + w.Item; }
        public string Style(WornItem w)
        {
            WardrobeItem i = Item(w.Item);
            if (i != null) foreach (WardrobeVariant v in i.Variants) if (v.Id == w.Variant) return v.Title;
            return "Style " + w.Variant;
        }
        public string Wear(WornItem w)
        {
            WardrobeItem i = Item(w.Item);
            if (i != null) foreach (WearOption o in i.Options) if (o.Mesh == w.Mesh) return o.Title;
            return "";
        }
        // The slot the save will put a wear option in, or -1 when this editor does not know it.
        public int SlotOfMesh(int mesh)
        {
            string name;
            if (!meshSlot.TryGetValue(mesh, out name)) return -1;
            foreach (BodySlot b in Slots) if (b.Name == name) return b.Id;
            return -1;
        }

        // Puts an item in its slot, over whatever was there, keeping the game's order.
        public static void Put(List<WornItem> outfit, int item, int variant, int mesh, int slot)
        {
            WornItem w = new WornItem(item, variant, slot, mesh);
            for (int i = 0; i < outfit.Count; i++) if (outfit[i].Slot == slot) { outfit[i] = w; return; }
            outfit.Add(w);
        }
        public static void TakeOff(List<WornItem> outfit, int slot) { outfit.RemoveAll(delegate(WornItem w) { return w.Slot == slot; }); }
        public static WornItem WornIn(List<WornItem> outfit, int slot)
        {
            foreach (WornItem w in outfit) if (w.Slot == slot) return w;
            return null;
        }
        public static string Describe(List<WornItem> outfit)
        {
            StringBuilder sb = new StringBuilder();
            foreach (WornItem w in outfit) sb.Append(w.Key).Append(';');
            return sb.ToString();
        }

        // The 16-byte records: count, then records with zeros up to max.
        public static List<WornItem> Read(byte[] d, int countAt, int at, int max)
        {
            List<WornItem> r = new List<WornItem>();
            int n = (int)Math.Min(Be.U32(d, countAt), (uint)max);
            for (int k = 0; k < n; k++)
            {
                int o = at + 16 * k;
                r.Add(new WornItem((int)Be.U32(d, o), (int)Be.U32(d, o + 4), (int)Be.U32(d, o + 8), (int)Be.U32(d, o + 12)));
            }
            return r;
        }
        public static void Write(byte[] d, int countAt, int at, int max, List<WornItem> outfit)
        {
            int n = Math.Min(outfit.Count, max);
            Be.Put(d, countAt, (uint)n);
            Array.Clear(d, at, max * 16);
            for (int k = 0; k < n; k++)
            {
                int o = at + 16 * k;
                Be.Put(d, o, (uint)outfit[k].Item); Be.Put(d, o + 4, (uint)outfit[k].Variant);
                Be.Put(d, o + 8, (uint)outfit[k].Slot); Be.Put(d, o + 12, (uint)outfit[k].Mesh);
            }
        }
    }

    class Neighbourhood { public string Name; public int OwnerAt; public uint Owner; }
    class Homie { public string Name; public int FlagAt; public bool Unlocked; }
    class StashWeapon { public uint Hash; public int FlagAt; public bool Unlocked; }
    class CarriedWeapon
    {
        public string Name; public uint Count = 1, Clip, Reserve;
        public CarriedWeapon(string name, uint clip, uint reserve) { Name = name; Clip = clip; Reserve = reserve; }
    }

    class StorySave
    {
        public static readonly string[] Gangs = { "3rd Street Saints", "Los Carnales", "Vice Kings", "Westside Rollerz" };
        const int RespectAt = 0x90, RespectTotalAt = 0x94, RespectNeededAt = 0x98, CashAt = 0xA0;
        // (count, pointer) pairs in the header.
        const int WeaponsAt = 0x144, FoodAt = 0x240, HomiesAt = 0x6E88, HoodsAt = 0x6E90, StashAt = 0x6F8C, EndPointerAt = 0x6FF8;
        // What the player wears: count, then 16-byte records in the header (room for 128).
        const int OutfitCountAt = 0x64C, OutfitAt = 0x650;
        // Clothing owned: count, then 12-byte records (item, variant, 0) in the header (room for 2048).
        const int OwnedCountAt = 0xE50, OwnedAt = 0xE54, OwnedMax = 2048;
        const uint UnlockedFlag = 0x80000000u;

        public readonly string Path;
        byte[] data;
        public readonly List<Neighbourhood> Hoods = new List<Neighbourhood>();
        public readonly List<Homie> Homies = new List<Homie>();
        public readonly List<StashWeapon> Stash = new List<StashWeapon>();
        // Weapons on the wheel (name, loaded, spare ammo) and the food carried, in file order.
        public List<CarriedWeapon> Weapons = new List<CarriedWeapon>();
        public List<string> Food = new List<string>();
        // What the player wears, one record per body slot.
        public List<WornItem> Outfit = new List<WornItem>();
        // Clothing owned (bought or worn), in the game's order.
        public List<Pair> Owned = new List<Pair>();
        string loaded, loadedOutfit, loadedOwned;

        public StorySave(string path)
        {
            Path = path;
            data = File.ReadAllBytes(path);
            if (data.Length < 0x7800 || U32(4) != 0x17)
                throw new InvalidDataException("This is not a Saints Row save this editor knows.");
            int count, at;
            if (List(WeaponsAt, 16, 64, out count, out at))
                for (int i = 0; i < count; i++)
                {
                    int o = at + i * 16;
                    string n = NameAt(U32(o));
                    if (n == null) continue;
                    CarriedWeapon w = new CarriedWeapon(n, U32(o + 8), U32(o + 12));
                    w.Count = U32(o + 4);
                    Weapons.Add(w);
                }
            if (List(FoodAt, 4, 64, out count, out at))
                for (int i = 0; i < count; i++)
                {
                    string n = NameAt(U32(at + i * 4));
                    if (n != null) Food.Add(n);
                }
            loaded = Inventory();
            Outfit = WardrobeTables.Read(data, OutfitCountAt, OutfitAt, WardrobeTables.MaxWorn);
            loadedOutfit = WardrobeTables.Describe(Outfit);
            int ownedCount = (int)Math.Min(U32(OwnedCountAt), (uint)OwnedMax);
            for (int k = 0; k < ownedCount; k++) Owned.Add(new Pair((int)U32(OwnedAt + 12 * k), (int)U32(OwnedAt + 12 * k + 4)));
            loadedOwned = OwnedText();
            if (List(HomiesAt, 12, 64, out count, out at))
                for (int i = 0; i < count; i++)
                {
                    int o = at + i * 12;
                    string n = NameAt(U32(o));
                    if (n == null) continue;
                    Homie h = new Homie(); h.Name = n; h.FlagAt = o + 8; h.Unlocked = U32(o + 8) == 1;
                    Homies.Add(h);
                }
            if (List(HoodsAt, 12, 128, out count, out at))
                for (int i = 0; i < count; i++)
                {
                    int o = at + i * 12;
                    string n = NameAt(U32(o));
                    if (n == null) continue;
                    Neighbourhood h = new Neighbourhood(); h.Name = n; h.OwnerAt = o + 4; h.Owner = U32(o + 4);
                    Hoods.Add(h);
                }
            if (List(StashAt, 8, 128, out count, out at))
                for (int i = 0; i < count; i++)
                {
                    int o = at + i * 8;
                    StashWeapon s = new StashWeapon(); s.Hash = U32(o); s.FlagAt = o + 4; s.Unlocked = (U32(o + 4) & UnlockedFlag) != 0;
                    Stash.Add(s);
                }
        }

        uint U32(int o) { return o >= 0 && o + 4 <= data.Length ? Be.U32(data, o) : 0; }
        string NameAt(uint pointer)
        {
            int o = (int)pointer;
            if (pointer >= 0x7FFFFFFF || o < 0x7000 || o >= data.Length) return null;
            int i = o;
            while (i < data.Length && data[i] != 0 && i - o < 64)
            {
                if (data[i] < 32 || data[i] >= 127) return null;
                i++;
            }
            if (i - o < 2 || i >= data.Length) return null;
            return Encoding.ASCII.GetString(data, o, i - o);
        }
        bool List(int at, int recordSize, int limit, out int count, out int pointer)
        {
            uint c = U32(at), p = U32(at + 4);
            count = (int)Math.Min(c, 0x7FFFFFFFu); pointer = (int)Math.Min(p, 0x7FFFFFFFu);
            return c <= (uint)limit && p >= 0x7000 && (long)p + (long)c * recordSize <= data.Length;
        }
        // Clothing that is worn is owned; hair and faces are not clothing.
        public void OwnWorn(WardrobeTables tables)
        {
            HashSet<long> have = new HashSet<long>();
            foreach (Pair p in Owned) have.Add(p.Key);
            foreach (WornItem w in Outfit)
            {
                Pair p = new Pair(w.Item, w.Variant);
                if (tables.IsClothing(w.Item) && have.Add(p.Key)) Owned.Add(p);
            }
        }
        string OwnedText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Pair p in Owned) sb.Append(p.Key).Append(';');
            return sb.ToString();
        }
        string Inventory()
        {
            StringBuilder sb = new StringBuilder();
            foreach (CarriedWeapon w in Weapons) sb.Append(w.Name).Append(':').Append(w.Count).Append(':').Append(w.Clip).Append(':').Append(w.Reserve).Append(';');
            sb.Append('|');
            foreach (string f in Food) sb.Append(f).Append(';');
            return sb.ToString();
        }

        public decimal Cash
        {
            get { return (int)U32(CashAt) / 100m; }
            set { Be.Put(data, CashAt, (uint)(int)(Math.Max(0m, Math.Min(value, 20000000m)) * 100m)); }
        }
        public uint Respect
        {
            get { return U32(RespectAt); }
            set { Be.Put(data, RespectAt, value); Be.Put(data, RespectTotalAt, value); }
        }
        public uint RespectNeeded { get { return U32(RespectNeededAt); } }

        public void SetOwner(int index, uint gang)
        {
            if (index < 0 || index >= Hoods.Count || gang >= Gangs.Length) return;
            Hoods[index].Owner = gang;
            Be.Put(data, Hoods[index].OwnerAt, gang);
        }
        public void SetHomie(int index, bool unlocked)
        {
            if (index < 0 || index >= Homies.Count) return;
            Homies[index].Unlocked = unlocked;
            Be.Put(data, Homies[index].FlagAt, unlocked ? 1u : 0u);
        }
        public void SetStash(int index, bool unlocked)
        {
            if (index < 0 || index >= Stash.Count) return;
            Stash[index].Unlocked = unlocked;
            uint old = U32(Stash[index].FlagAt);
            Be.Put(data, Stash[index].FlagAt, unlocked ? old | UnlockedFlag : old & ~UnlockedFlag);
        }

        // Puts the weapon and food lists after the game's own data. The game follows the
        // pointers, so the lists may sit anywhere; the file grows in steps of 2048 bytes.
        void WriteInventory()
        {
            if (Inventory() == loaded) return;
            // Lists this editor wrote earlier sit after everything else; write over them.
            int firstTable = (int)U32(HomiesAt + 4);
            int start = int.MaxValue;
            foreach (int p in new int[] { (int)U32(WeaponsAt + 4), (int)U32(FoodAt + 4) }) if (p > firstTable && p < start) start = p;
            if (start == int.MaxValue) start = (int)U32(EndPointerAt);
            if (start < 0x7000 || start > data.Length) return;

            List<byte> block = new List<byte>();
            List<uint> weaponNames = new List<uint>(), foodNames = new List<uint>();
            foreach (CarriedWeapon w in Weapons) weaponNames.Add(Text(block, start, w.Name));
            foreach (string f in Food) foodNames.Add(Text(block, start, f));
            int weaponsPointer = start + block.Count;
            for (int i = 0; i < Weapons.Count; i++)
            {
                Add(block, weaponNames[i]); Add(block, Weapons[i].Count); Add(block, Weapons[i].Clip); Add(block, Weapons[i].Reserve);
            }
            int foodPointer = start + block.Count;
            foreach (uint n in foodNames) Add(block, n);
            while (block.Count % 16 != 0) block.Add(0);

            int end = start + block.Count;
            byte[] result = new byte[Math.Max(0x8800, (end + 0x7FF) & ~0x7FF)];
            Buffer.BlockCopy(data, 0, result, 0, start);
            block.CopyTo(result, start);
            data = result;
            Be.Put(data, WeaponsAt, (uint)Weapons.Count); Be.Put(data, WeaponsAt + 4, (uint)weaponsPointer);
            Be.Put(data, FoodAt, (uint)Food.Count); Be.Put(data, FoodAt + 4, (uint)foodPointer);
            Be.Put(data, EndPointerAt, (uint)end);
            loaded = Inventory();
        }
        static void Add(List<byte> block, uint v)
        {
            block.Add((byte)(v >> 24)); block.Add((byte)(v >> 16)); block.Add((byte)(v >> 8)); block.Add((byte)v);
        }
        static uint Text(List<byte> block, int start, string s)
        {
            uint at = (uint)(start + block.Count);
            block.AddRange(Encoding.ASCII.GetBytes(s)); block.Add(0);
            while (block.Count % 16 != 0) block.Add(0);
            return at;
        }

        // Writes the save, keeping the previous file next to it as game.dat.<date>-<time>.bak.
        public string Save()
        {
            WriteInventory();
            if (WardrobeTables.Describe(Outfit) != loadedOutfit)
            {
                WardrobeTables.Write(data, OutfitCountAt, OutfitAt, WardrobeTables.MaxWorn, Outfit);
                loadedOutfit = WardrobeTables.Describe(Outfit);
            }
            if (OwnedText() != loadedOwned)
            {
                int n = Math.Min(Owned.Count, OwnedMax);
                Be.Put(data, OwnedCountAt, (uint)n);
                Array.Clear(data, OwnedAt, OwnedMax * 12);
                for (int k = 0; k < n; k++) { Be.Put(data, OwnedAt + 12 * k, (uint)Owned[k].Item); Be.Put(data, OwnedAt + 12 * k + 4, (uint)Owned[k].Variant); }
                loadedOwned = OwnedText();
            }
            string backup = Backup(Path);
            File.WriteAllBytes(Path, data);
            return backup;
        }

        // Keeps a dated copy of a file next to it and returns its name.
        public static string Backup(string path)
        {
            string stem = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss"), backup = stem + ".bak";
            for (int n = 2; File.Exists(backup); n++) backup = stem + "-" + n + ".bak";
            File.Copy(path, backup, false);
            return backup;
        }
    }
}

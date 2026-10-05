// Shared, window-free code: packfile reader, the game's own tables, and the
// story save (game.dat). The Mac version (mac/Sources/Core) does the same things.
//
// Story save: big-endian. A fixed header (0x7000 bytes) holds counts and
// pointers; the lists they point to follow it. Pointers are offsets from the
// start of the file.
//   0x0090  respect            0x00A0  cash in cents
//   0x0144  carried weapons: count, pointer to 16-byte records (name, count, loaded, spare)
//   0x0240  carried food: count, pointer to names
//   0x6E88  homies: count, pointer to 12-byte records (name, 0, unlocked)
//   0x6E90  neighbourhoods: count, pointer to 12-byte records (name, owner, flags)
//   0x6F8C  crib weapon cache: count, pointer to 8-byte records (name hash, flags)

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

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

        static int Number(string s)
        {
            int v;
            return s != null && int.TryParse(s, out v) ? v : 0;
        }

        // The top-level <element> blocks of one of the game's tables.
        List<string> Blocks(string file, string element)
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
        static string Tag(string block, string name)
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
        const uint UnlockedFlag = 0x80000000u;

        public readonly string Path;
        byte[] data;
        public readonly List<Neighbourhood> Hoods = new List<Neighbourhood>();
        public readonly List<Homie> Homies = new List<Homie>();
        public readonly List<StashWeapon> Stash = new List<StashWeapon>();
        // Weapons on the wheel (name, loaded, spare ammo) and the food carried, in file order.
        public List<CarriedWeapon> Weapons = new List<CarriedWeapon>();
        public List<string> Food = new List<string>();
        string loaded;

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
            string stem = Path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss"), backup = stem + ".bak";
            for (int n = 2; File.Exists(backup); n++) backup = stem + "-" + n + ".bak";
            File.Copy(Path, backup, false);
            File.WriteAllBytes(Path, data);
            return backup;
        }
    }
}

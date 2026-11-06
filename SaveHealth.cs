// Keeping saves safe: a backup of every save the editor opens, a check of a
// save against what the game itself checks, a repair, and outfit files.
//
// What the game checks (read in its code, see README "Damaged saves"):
//   - the save list (0x82208E40) splits the slot folder name at '@' into the
//     version, mission, number, date and time; a name that does not split
//     into five parts is listed as damaged, and a version other than 23 too.
//   - the loader (0x822036E0) reads game.dat, works out the CRC-32 of every
//     byte from 0x0004 to the end and compares it with the word at 0x0000.
//     When they differ it shows "save file corrupt" and offers to delete it.
// The online character (3_MPStorage) has no checksum: only its size and
// layout are checked.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MpCharacterEditor
{
    // Copies of saves in the editor's own folder, outside the game's folders, so they
    // survive the game deleting a save (its "corrupt" dialog offers that). One folder per
    // save; a copy is only made when the file differs from every copy already kept.
    static class SaveBackups
    {
        public static string Root
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("SR_EDITOR_BACKUPS");   // tests
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    @"SaintsRebornMpEditor\Backups");
            }
        }

        // ...\<profile>\545107D1\00000001\<slot>\game.dat  -> Backups\story\<profile>\<slot>
        // ...\profile\<player>\storage\3_MPStorage           -> Backups\online\<player>
        public static string FolderFor(string path)
        {
            string full = Path.GetFullPath(path), name = Path.GetFileName(full);
            string dir = Path.GetDirectoryName(full);
            string parent = Path.GetFileName(dir) ?? "save";
            if (name.StartsWith("game.dat", StringComparison.OrdinalIgnoreCase))
            {
                string up3 = Up(dir, 3), profile = up3 == null ? "other" : Path.GetFileName(up3);
                return Path.Combine(Root, "story", Clean(profile), Clean(parent));
            }
            if (name.IndexOf("MPStorage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string player = string.Equals(parent, "storage", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(Path.GetDirectoryName(dir)) : parent;
                return Path.Combine(Root, "online", Clean(player));
            }
            return Path.Combine(Root, "other", Clean(parent));
        }
        static string Up(string dir, int n) { for (int i = 0; i < n && dir != null; i++) dir = Path.GetDirectoryName(dir); return dir; }
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "save";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
        static string BaseName(string path)
        {
            string n = Path.GetFileName(path);
            if (n.StartsWith("game.dat", StringComparison.OrdinalIgnoreCase)) return "game.dat";
            if (n.IndexOf("MPStorage", StringComparison.OrdinalIgnoreCase) >= 0) return "3_MPStorage";
            return n;
        }

        // Copies the file into its backup folder unless an identical copy is there already.
        // Returns the copy, or null when none was needed or it could not be made.
        public static string Keep(string path, string reason)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] now = File.ReadAllBytes(path);
                string folder = FolderFor(path);
                Directory.CreateDirectory(folder);
                foreach (string old in List(path))
                    if (new FileInfo(old).Length == now.Length && Same(File.ReadAllBytes(old), now)) { CopyHeader(path, folder); return null; }
                string stem = Path.Combine(folder, BaseName(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + reason.Replace(' ', '-'));
                string copy = stem + ".bak";
                for (int n = 2; File.Exists(copy); n++) copy = stem + "-" + n + ".bak";
                File.WriteAllBytes(copy, now);
                File.SetLastWriteTime(copy, DateTime.Now);
                CopyHeader(path, folder);
                return copy;
            }
            catch (Exception) { return null; }
        }
        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        // The game's list entry for a story save (display name), so a deleted save can be put back whole.
        static void CopyHeader(string path, string folder)
        {
            string header = SaveCheck.HeaderFile(path);
            if (header != null && File.Exists(header)) File.Copy(header, Path.Combine(folder, Path.GetFileName(header)), true);
        }

        // The copies of a file, newest first: the editor's folder and the ones kept next to it.
        public static List<string> List(string path)
        {
            List<string> r = new List<string>();
            string folder = FolderFor(path), stem = BaseName(path) + ".";
            if (Directory.Exists(folder))
                foreach (string f in Directory.GetFiles(folder, stem + "*.bak")) r.Add(f);
            r.Sort(delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            return r;
        }
        public static List<string> NextTo(string path)
        {
            List<string> r = new List<string>();
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, Path.GetFileName(path) + ".*")) r.Add(f);
            r.Sort(delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            return r;
        }
    }

    enum Health { Good = 0, Note = 1, Warning = 2, Damaged = 3 }

    class Finding
    {
        public Health Level; public string Text;
        public Finding(Health level, string text) { Level = level; Text = text; }
        public override string ToString() { return (Level == Health.Damaged ? "DAMAGED: " : Level == Health.Warning ? "warning: " : Level == Health.Note ? "note: " : "") + Text; }
    }

    class SaveReport
    {
        public string Path, Kind = "unknown";
        public readonly List<Finding> Findings = new List<Finding>();
        // The file's contents are damaged (not only its folder name).
        public bool DataDamaged;
        // The only damage to the contents is a checksum that does not match: rewriting it makes the save load.
        public bool ChecksumOnly;
        public Health Worst
        {
            get { Health w = Health.Good; foreach (Finding f in Findings) if (f.Level > w) w = f.Level; return w; }
        }
        public void Add(Health level, string text) { Findings.Add(new Finding(level, text)); }
        public string Summary
        {
            get
            {
                switch (Worst)
                {
                    case Health.Damaged: return ChecksumOnly ? "damaged, can be repaired" : DataDamaged ? "damaged" : "damaged (folder name)";
                    case Health.Warning: return "loads, with warnings";
                    default: return "good";
                }
            }
        }
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Kind + " " + Path + ": " + Summary);
            foreach (Finding f in Findings) sb.AppendLine("  " + f);
            return sb.ToString();
        }
    }

    static class SaveCheck
    {
        public static bool IsStory(string path, byte[] d)
        {
            string n = System.IO.Path.GetFileName(path);
            if (n.StartsWith("game.dat", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.IndexOf("MPStorage", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return d.Length != Character.Size && d.Length >= 8 && Be.U32(d, 4) == StorySave.Version;
        }

        public static SaveReport Check(string path)
        {
            SaveReport r = new SaveReport(); r.Path = path;
            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch (Exception ex) { r.Add(Health.Damaged, "can't be read: " + ex.Message); return r; }
            if (IsStory(path, d)) r.Kind = "story"; else r.Kind = "online";
            if (r.Kind == "story") Story(r, d); else Online(r, d);
            r.DataDamaged = r.Worst == Health.Damaged;
            r.ChecksumOnly = r.ChecksumOnly && OnlyChecksum(r);
            if (r.Kind == "story" && System.IO.Path.GetFileName(path).Equals("game.dat", StringComparison.OrdinalIgnoreCase)) Folder(r, path);
            return r;
        }

        // ---- Story save ----

        static void Story(SaveReport r, byte[] d)
        {
            if (d.Length < StorySave.MinSize)
            {
                r.Add(Health.Damaged, "the file is cut short (" + d.Length + " bytes; a save is at least " + StorySave.MinSize + ")");
                return;
            }
            uint version = Be.U32(d, 4);
            if (version != StorySave.Version)
            {
                r.Add(Health.Damaged, "version " + version + " at 0x0004; the game only loads version " + StorySave.Version);
                return;
            }
            int before = r.Findings.Count;
            Lists(r, d);
            bool sound = true;
            for (int i = before; i < r.Findings.Count; i++) if (r.Findings[i].Level == Health.Damaged) sound = false;

            uint stored = StorySave.StoredChecksum(d), wanted = StorySave.Checksum(d);
            if (stored != wanted)
            {
                if (sound)
                {
                    r.Add(Health.Damaged, "the checksum is " + stored.ToString("x8") + " but the contents add up to " + wanted.ToString("x8") +
                        ": the game will say this save is corrupt. Repair writes the right checksum (the contents look whole).");
                    r.ChecksumOnly = true;
                }
                else r.Add(Health.Damaged, "the checksum does not match either, so the contents really are broken; restore a backup");
            }
        }
        static bool OnlyChecksum(SaveReport r)
        {
            int damaged = 0;
            foreach (Finding f in r.Findings) if (f.Level == Health.Damaged) damaged++;
            return damaged == 1;
        }

        static void Lists(SaveReport r, byte[] d)
        {
            int length = d.Length;
            List(r, d, 0x144, 16, 64, "the carried weapons", 0);
            List(r, d, 0x240, 4, 64, "the carried food", -1);
            List(r, d, 0x6E88, 12, 64, "the homies", 0);
            List(r, d, 0x6E90, 12, 128, "the neighbourhoods", 0);
            List(r, d, 0x6E98, GarageVehicle.Size, 128, "the garage", -2);
            List(r, d, 0x6F8C, 8, 128, "the crib weapon cache", -2);
            // The unlocked cheats: size in bytes at 0x6FF4, pointer at 0x6FF8, one name hash each.
            uint cheatsSize = Be.U32(d, StorySave.CheatsSizeAt), cheats = Be.U32(d, StorySave.CheatsAt);
            if (cheats < 0x7000 || (long)cheats + cheatsSize > length)
                r.Add(Health.Damaged, "the unlocked cheats (0x6FF8) point outside the file");
            else if (cheatsSize % 4 != 0 || cheatsSize > 512)
                r.Add(Health.Warning, "the unlocked cheats list is " + cheatsSize + " bytes, not a whole number of entries");
            else
            {
                int lost = 0;
                for (uint o = cheats; o < cheats + cheatsSize; o += 4) if (Be.U32(d, (int)o) == 0) lost++;
                if (lost > 0) r.Add(Health.Warning, lost + " unlocked cheat(s) were wiped by an older version of this editor; " +
                    "a backup from before that edit still has them");
            }
            uint outfit = Be.U32(d, 0x64C), owned = Be.U32(d, 0xE50);
            if (outfit > WardrobeTables.MaxWorn) r.Add(Health.Damaged, "the outfit says " + outfit + " items; there is room for " + WardrobeTables.MaxWorn);
            if (owned > 2048) r.Add(Health.Damaged, "the clothing owned says " + owned + " items; there is room for 2048");
            int cash = (int)Be.U32(d, 0xA0);
            if (cash < 0) r.Add(Health.Warning, "cash is negative ($" + (cash / 100m).ToString("0.00") + ")");
        }
        // A (count, pointer) pair: the records must sit inside the file. names: offset of a
        // name pointer in each record to check, -1 for a list of name pointers, -2 for none.
        static void List(SaveReport r, byte[] d, int at, int size, int limit, string what, int names)
        {
            uint count = Be.U32(d, at), pointer = Be.U32(d, at + 4);
            if (count == 0) return;
            if (count > (uint)limit) { r.Add(Health.Damaged, what + " (0x" + at.ToString("x") + ") says " + count + " entries; the game has no more than " + limit); return; }
            if (pointer < 0x7000 || (long)pointer + (long)count * size > d.Length)
            {
                r.Add(Health.Damaged, what + " (0x" + at.ToString("x") + ") point outside the file");
                return;
            }
            if (names == -2) return;
            int bad = 0;
            for (int i = 0; i < count; i++)
            {
                int o = (int)pointer + i * size + (names < 0 ? 0 : names);
                if (!Text(d, Be.U32(d, o))) bad++;
            }
            if (bad > 0) r.Add(Health.Warning, bad + " of " + count + " entries in " + what + " have no readable name; the editor leaves them out");
        }
        static bool Text(byte[] d, uint pointer)
        {
            if (pointer < 0x7000 || pointer >= d.Length) return false;
            int i = (int)pointer;
            while (i < d.Length && d[i] != 0) { if (d[i] < 32 || d[i] >= 127 || i - pointer > 64) return false; i++; }
            return i < d.Length && i - pointer >= 2;
        }

        // The slot folder and the game's list entry for it.
        static void Folder(SaveReport r, string path)
        {
            string slot = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            List<string> parts = new List<string>();
            foreach (string p in slot.Split('@')) if (p.Length > 0) parts.Add(p);   // the game splits with strtok
            int version;
            if (parts.Count < 5)
                r.Add(Health.Damaged, "the folder name \"" + slot + "\" is not one the game made (version@mission@number@date@time), " +
                    "so the game lists the save as damaged. Give the folder back its old name.");
            else if (!int.TryParse(parts[0], out version) || version != StorySave.Version)
                r.Add(Health.Damaged, "the folder name starts with \"" + parts[0] + "\"; the game only lists saves whose name starts with " + StorySave.Version);
            string header = HeaderFile(path);
            if (header != null && !File.Exists(header))
                r.Add(Health.Note, "there is no list entry for it in ..\\Headers\\00000001, so the game may not show its name");
        }
        // <profile>\545107D1\Headers\00000001\<slot>.header for <profile>\545107D1\00000001\<slot>\game.dat.
        public static string HeaderFile(string path)
        {
            string slotDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            string listDir = System.IO.Path.GetDirectoryName(slotDir);
            if (listDir == null || System.IO.Path.GetFileName(listDir) != "00000001") return null;
            string title = System.IO.Path.GetDirectoryName(listDir);
            return System.IO.Path.Combine(title, @"Headers\00000001\" + System.IO.Path.GetFileName(slotDir) + ".header");
        }

        // ---- Online character ----

        static void Online(SaveReport r, byte[] d)
        {
            if (d.Length != Character.Size)
            {
                r.Add(Health.Damaged, "the file is " + d.Length + " bytes; an online character is " + Character.Size);
                return;
            }
            if (Be.U32(d, 0) != 0x00040000u || Be.U32(d, 8) != 0x00034000u)
                r.Add(Health.Warning, "the first words are not the ones every character file has (00040000 at 0x0, 00034000 at 0x8)");
            int cash = (int)Be.U32(d, 4);
            if (cash < 0) r.Add(Health.Warning, "cash is negative");
            uint worn = Be.U32(d, 0x414), owned = Be.U32(d, 0xC18);
            if (worn > WardrobeTables.MaxWorn) r.Add(Health.Damaged, "it says " + worn + " worn items; there is room for " + WardrobeTables.MaxWorn);
            if (owned > Character.OwnedMax) r.Add(Health.Damaged, "it says " + owned + " owned items; there is room for " + Character.OwnedMax);
            else
            {
                HashSet<long> seen = new HashSet<long>(); int twice = 0;
                for (int k = 0; k < owned; k++) if (!seen.Add(new Pair((int)Be.U32(d, 0xC1C + 12 * k), (int)Be.U32(d, 0xC20 + 12 * k)).Key)) twice++;
                if (twice > 0) r.Add(Health.Note, twice + " owned items are listed twice");
            }
        }

        // ---- Repair ----

        // The newest backup (the editor's folder first, then the ones next to the file) that
        // the game would load. Null when there is none.
        public static string GoodBackup(string path)
        {
            List<string> all = SaveBackups.List(path);
            all.AddRange(SaveBackups.NextTo(path));
            all.Sort(delegate(string a, string b) { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); });
            bool story = IsStory(path, File.Exists(path) ? File.ReadAllBytes(path) : new byte[0]);
            foreach (string b in all)
            {
                SaveReport r = new SaveReport(); r.Path = b;
                byte[] d;
                try { d = File.ReadAllBytes(b); } catch (Exception) { continue; }
                if (story) Story(r, d); else Online(r, d);
                if (r.Worst < Health.Damaged) return b;
            }
            return null;
        }

        // Makes a damaged save load again: a wrong checksum is rewritten; anything worse is
        // replaced by the newest good backup. The file as it was is kept first. Returns what
        // was done, or null when nothing could be done.
        public static string Repair(string path)
        {
            SaveReport r = Check(path);
            if (!r.DataDamaged) return r.Worst < Health.Damaged ? "nothing to repair" : null;   // a folder name is not ours to change
            if (r.ChecksumOnly)
            {
                byte[] d = File.ReadAllBytes(path);
                SaveBackups.Keep(path, "before repair");
                StorySave.Backup(path);
                StorySave.SetChecksum(d);
                File.WriteAllBytes(path, d);
                return "checksum rewritten";
            }
            string good = GoodBackup(path);
            if (good == null) return null;
            SaveBackups.Keep(path, "before repair");
            if (File.Exists(path)) StorySave.Backup(path);
            byte[] copy = File.ReadAllBytes(good);
            if (r.Kind == "story" && StorySave.StoredChecksum(copy) != StorySave.Checksum(copy)) StorySave.SetChecksum(copy);
            File.WriteAllBytes(path, copy);
            return "restored " + System.IO.Path.GetFileName(good);
        }
    }

    // An outfit in a file of its own (Documents\Saints Reborn Outfits\*.sroutfit), to keep it
    // or put it on another save or the online character. One line per worn item:
    // item id, variant id, body slot, wear option (as the saves keep them).
    static class OutfitFile
    {
        public const string Extension = ".sroutfit";
        const string Magic = "Saints Reborn outfit 1";

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Saints Reborn Outfits"); }
        }

        public static void Write(string path, List<WornItem> outfit, string from)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Magic);
            sb.AppendLine("# from " + from + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("# item variant slot wear-option");
            foreach (WornItem w in outfit) sb.AppendLine(w.Item + " " + w.Variant + " " + w.Slot + " " + w.Mesh);
            File.WriteAllText(path, sb.ToString());
        }

        public static List<WornItem> Read(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != Magic) throw new InvalidDataException("This is not an outfit file.");
            List<WornItem> outfit = new List<WornItem>();
            HashSet<int> slots = new HashSet<int>();
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                string[] p = l.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int item, variant, slot, mesh;
                if (p.Length != 4 || !int.TryParse(p[0], out item) || !int.TryParse(p[1], out variant) || !int.TryParse(p[2], out slot) || !int.TryParse(p[3], out mesh))
                    throw new InvalidDataException("Line " + (i + 1) + " of the outfit file can't be read.");
                if (slots.Add(slot)) outfit.Add(new WornItem(item, variant, slot, mesh));
            }
            if (outfit.Count > WardrobeTables.MaxWorn) throw new InvalidDataException("The outfit has more items than a save has room for.");
            return outfit;
        }
    }
}

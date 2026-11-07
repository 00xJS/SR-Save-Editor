// Keeping saves safe: a copy of every save the editor opens, a check of a save against
// what the game itself checks, a repair, and outfit files. Same as SaveHealth.cs.
//
// What the game checks (read in its code, see README "Damaged saves"):
//   - the save list (0x82208E40) splits the slot folder name at '@' into the version,
//     mission, number, date and time; a name that does not split into five parts is
//     listed as damaged, and a version other than 23 too.
//   - the loader (0x822036E0) works out the CRC-32 of every byte of game.dat from 0x0004
//     to the end and compares it with the word at 0x0000; when they differ it says the
//     save is corrupt and offers to delete it.
// The online character (3_MPStorage) has no checksum: only its size and layout are checked.
import Foundation

private func be32(_ d: [UInt8], _ o: Int) -> UInt32 {
    guard o >= 0, o + 4 <= d.count else { return 0 }
    return UInt32(d[o]) << 24 | UInt32(d[o + 1]) << 16 | UInt32(d[o + 2]) << 8 | UInt32(d[o + 3])
}
private func put32(_ d: inout [UInt8], _ o: Int, _ v: UInt32) {
    d[o] = UInt8(v >> 24); d[o + 1] = UInt8((v >> 16) & 255); d[o + 2] = UInt8((v >> 8) & 255); d[o + 3] = UInt8(v & 255)
}
private func modified(_ u: URL) -> Date { (try? u.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast }

/// The story save's checksum, as the game's loader works it out.
enum StoryChecksum {
    static let version: UInt32 = 23, minSize = 0x7800
    static func wanted(_ d: [UInt8]) -> UInt32 { GameTables.crc32(d.dropFirst(4)) }
    static func stored(_ d: [UInt8]) -> UInt32 { be32(d, 0) }
    static func set(_ d: inout [UInt8]) { put32(&d, 0, wanted(d)) }
}

/// Copies of saves in the editor's own folder, outside the game's folders, so they survive the
/// game deleting a save (its "corrupt" dialog offers that). One folder per save; a copy is only
/// made when the file differs from every copy already kept.
enum EditorBackups {
    static var root: URL {
        if let over = ProcessInfo.processInfo.environment["SR_EDITOR_BACKUPS"], !over.isEmpty { return URL(fileURLWithPath: over) }   // tests
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return support.appendingPathComponent("Saints Reborn Save Editor/Backups")
    }

    /// .../<profile>/545107D1/00000001/<slot>/game.dat -> Backups/story/<profile>/<slot>
    /// .../profile/<player>/storage/3_MPStorage        -> Backups/online/<player>
    static func folder(for url: URL) -> URL {
        let name = url.lastPathComponent, dir = url.deletingLastPathComponent(), parent = dir.lastPathComponent
        if name.lowercased().hasPrefix("game.dat") {
            let comps = dir.pathComponents
            let profile = comps.count >= 4 ? comps[comps.count - 4] : "other"
            return root.appendingPathComponent("story").appendingPathComponent(profile).appendingPathComponent(parent)
        }
        if name.range(of: "MPStorage", options: .caseInsensitive) != nil {
            let player = parent.lowercased() == "storage" ? dir.deletingLastPathComponent().lastPathComponent : parent
            return root.appendingPathComponent("online").appendingPathComponent(player)
        }
        return root.appendingPathComponent("other").appendingPathComponent(parent)
    }
    private static func baseName(_ url: URL) -> String {
        let n = url.lastPathComponent
        if n.lowercased().hasPrefix("game.dat") { return "game.dat" }
        if n.range(of: "MPStorage", options: .caseInsensitive) != nil { return "3_MPStorage" }
        return n
    }

    /// Copies the file into its backup folder unless an identical copy is there already.
    /// Returns the copy, or nil when none was needed or it could not be made.
    @discardableResult
    static func keep(_ url: URL, reason: String) -> URL? {
        guard let now = try? Data(contentsOf: url) else { return nil }
        let folder = folder(for: url)
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            defer { copyHeader(url, to: folder) }
            for old in list(for: url) where (try? Data(contentsOf: old)) == now { return nil }
            let stamp = DateFormatter(); stamp.dateFormat = "yyyyMMdd-HHmmss"
            let stem = "\(baseName(url)).\(stamp.string(from: Date())).\(reason.replacingOccurrences(of: " ", with: "-"))"
            var copy = folder.appendingPathComponent(stem + ".bak"), n = 2
            while FileManager.default.fileExists(atPath: copy.path) { copy = folder.appendingPathComponent("\(stem)-\(n).bak"); n += 1 }
            try now.write(to: copy)
            return copy
        } catch { return nil }
    }
    /// The game's list entry for a story save (its display name), so a deleted save can be put back whole.
    private static func copyHeader(_ url: URL, to folder: URL) {
        guard let header = SaveCheck.headerFile(for: url), let data = try? Data(contentsOf: header) else { return }
        try? data.write(to: folder.appendingPathComponent(header.lastPathComponent))
    }
    /// The copies of a file in the editor's folder, newest first.
    static func list(for url: URL) -> [URL] {
        let folder = folder(for: url), stem = baseName(url) + "."
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        return files.filter { $0.lastPathComponent.hasPrefix(stem) && $0.pathExtension == "bak" }.sorted { modified($0) > modified($1) }
    }
}

enum Health: Int, Comparable {
    case good = 0, note, warning, damaged
    static func < (a: Health, b: Health) -> Bool { a.rawValue < b.rawValue }
}

struct Finding: CustomStringConvertible {
    let level: Health, text: String
    var description: String {
        (level == .damaged ? "DAMAGED: " : level == .warning ? "warning: " : level == .note ? "note: " : "") + text
    }
}

final class SaveReport: CustomStringConvertible, Identifiable {
    let url: URL
    var kind = "unknown"
    var findings: [Finding] = []
    /// The file's contents are damaged (not only its folder name).
    var dataDamaged = false
    /// The only damage to the contents is a checksum that does not match: rewriting it makes the save load.
    var checksumOnly = false
    init(url: URL) { self.url = url }
    var id: URL { url }
    var worst: Health { findings.map(\.level).max() ?? .good }
    func add(_ level: Health, _ text: String) { findings.append(Finding(level: level, text: text)) }
    var summary: String {
        switch worst {
        case .damaged: return checksumOnly ? "damaged, can be repaired" : dataDamaged ? "damaged" : "damaged (folder name)"
        case .warning: return "loads, with warnings"
        default: return "good"
        }
    }
    var description: String { "\(kind) \(url.path): \(summary)\n" + findings.map { "  \($0)\n" }.joined() }
}

enum SaveCheck {
    static func isStory(_ url: URL, _ d: [UInt8]) -> Bool {
        let n = url.lastPathComponent
        if n.lowercased().hasPrefix("game.dat") { return true }
        if n.range(of: "MPStorage", options: .caseInsensitive) != nil { return false }
        return d.count != MpCharacter.size && d.count >= 8 && be32(d, 4) == StoryChecksum.version
    }

    static func check(_ url: URL) -> SaveReport {
        let r = SaveReport(url: url)
        guard let data = try? Data(contentsOf: url) else { r.add(.damaged, "can't be read"); return r }
        let d = [UInt8](data)
        r.kind = isStory(url, d) ? "story" : "online"
        if r.kind == "story" { story(r, d) } else { online(r, d) }
        r.dataDamaged = r.worst == .damaged
        r.checksumOnly = r.checksumOnly && r.findings.filter { $0.level == .damaged }.count == 1
        if r.kind == "story", url.lastPathComponent.lowercased() == "game.dat" { folder(r, url) }
        return r
    }

    // MARK: Story save

    private static func story(_ r: SaveReport, _ d: [UInt8]) {
        if d.count < StoryChecksum.minSize {
            r.add(.damaged, "the file is cut short (\(d.count) bytes; a save is at least \(StoryChecksum.minSize))"); return
        }
        let version = be32(d, 4)
        if version != StoryChecksum.version {
            r.add(.damaged, "version \(version) at 0x0004; the game only loads version \(StoryChecksum.version)"); return
        }
        let before = r.findings.count
        lists(r, d)
        let sound = !r.findings[before...].contains { $0.level == .damaged }
        let stored = StoryChecksum.stored(d), wanted = StoryChecksum.wanted(d)
        if stored != wanted {
            if sound {
                r.add(.damaged, "the checksum is \(String(format: "%08x", stored)) but the contents add up to \(String(format: "%08x", wanted))" +
                      ": the game will say this save is corrupt. Repair writes the right checksum (the contents look whole).")
                r.checksumOnly = true
            } else {
                r.add(.damaged, "the checksum does not match either, so the contents really are broken; restore a backup")
            }
        }
    }

    private static func lists(_ r: SaveReport, _ d: [UInt8]) {
        list(r, d, 0x144, 16, 64, "the carried weapons", 0)
        list(r, d, 0x240, 4, 64, "the carried food", -1)
        list(r, d, 0x6E88, 12, 64, "the homies", 0)
        list(r, d, 0x6E90, 12, 128, "the neighbourhoods", 0)
        list(r, d, 0x6E98, GarageVehicle.size, 128, "the garage", -2)
        list(r, d, 0x6F8C, 8, 128, "the crib weapon cache", -2)
        // The unlocked cheats: size in bytes at 0x6FF4, pointer at 0x6FF8, one name hash each.
        let cheatsSize = Int(be32(d, 0x6FF4)), cheats = Int(be32(d, 0x6FF8))
        if cheats < 0x7000 || cheats + cheatsSize > d.count {
            r.add(.damaged, "the unlocked cheats (0x6FF8) point outside the file")
        } else if cheatsSize % 4 != 0 || cheatsSize > 512 {
            r.add(.warning, "the unlocked cheats list is \(cheatsSize) bytes, not a whole number of entries")
        } else {
            let lost = stride(from: cheats, to: cheats + cheatsSize, by: 4).filter { be32(d, $0) == 0 }.count
            if lost > 0 { r.add(.warning, "\(lost) unlocked cheat(s) were wiped by an older version of this editor; a backup from before that edit still has them") }
        }
        let outfit = be32(d, 0x64C), owned = be32(d, 0xE50)
        if outfit > UInt32(WardrobeTables.maxWorn) { r.add(.damaged, "the outfit says \(outfit) items; there is room for \(WardrobeTables.maxWorn)") }
        if owned > 2048 { r.add(.damaged, "the clothing owned says \(owned) items; there is room for 2048") }
        let cash = Int32(bitPattern: be32(d, 0xA0))
        if cash < 0 { r.add(.warning, "cash is negative ($\(String(format: "%.2f", Double(cash) / 100)))") }
    }
    /// A (count, pointer) pair: the records must sit inside the file. names: offset of a name
    /// pointer in each record to check, -1 for a list of name pointers, -2 for none.
    private static func list(_ r: SaveReport, _ d: [UInt8], _ at: Int, _ size: Int, _ limit: Int, _ what: String, _ names: Int) {
        let count = Int(be32(d, at)), pointer = Int(be32(d, at + 4))
        if count == 0 { return }
        if count > limit { r.add(.damaged, "\(what) (0x\(String(at, radix: 16))) says \(count) entries; the game has no more than \(limit)"); return }
        if pointer < 0x7000 || pointer + count * size > d.count { r.add(.damaged, "\(what) (0x\(String(at, radix: 16))) point outside the file"); return }
        if names == -2 { return }
        let bad = (0..<count).filter { !text(d, Int(be32(d, pointer + $0 * size + max(names, 0)))) }.count
        if bad > 0 { r.add(.warning, "\(bad) of \(count) entries in \(what) have no readable name; the editor leaves them out") }
    }
    private static func text(_ d: [UInt8], _ pointer: Int) -> Bool {
        guard pointer >= 0x7000, pointer < d.count else { return false }
        var i = pointer
        while i < d.count, d[i] != 0 { if d[i] < 32 || d[i] >= 127 || i - pointer > 64 { return false }; i += 1 }
        return i < d.count && i - pointer >= 2
    }

    /// The slot folder and the game's list entry for it.
    private static func folder(_ r: SaveReport, _ url: URL) {
        let slot = url.deletingLastPathComponent().lastPathComponent
        let parts = slot.split(separator: "@").map(String.init)   // the game splits with strtok
        if parts.count < 5 {
            r.add(.damaged, "the folder name \"\(slot)\" is not one the game made (version@mission@number@date@time), so the game lists the save as damaged. Give the folder back its old name.")
        } else if Int(parts[0]) != Int(StoryChecksum.version) {
            r.add(.damaged, "the folder name starts with \"\(parts[0])\"; the game only lists saves whose name starts with \(StoryChecksum.version)")
        }
        if let header = headerFile(for: url), !FileManager.default.fileExists(atPath: header.path) {
            r.add(.note, "there is no list entry for it in ../Headers/00000001, so the game may not show its name")
        }
    }
    /// <profile>/545107D1/Headers/00000001/<slot>.header for <profile>/545107D1/00000001/<slot>/game.dat.
    static func headerFile(for url: URL) -> URL? {
        let slotDir = url.deletingLastPathComponent(), listDir = slotDir.deletingLastPathComponent()
        guard listDir.lastPathComponent == "00000001" else { return nil }
        return listDir.deletingLastPathComponent().appendingPathComponent("Headers/00000001/\(slotDir.lastPathComponent).header")
    }

    // MARK: Online character

    private static func online(_ r: SaveReport, _ d: [UInt8]) {
        if d.count != MpCharacter.size { r.add(.damaged, "the file is \(d.count) bytes; an online character is \(MpCharacter.size)"); return }
        if be32(d, 0) != 0x0004_0000 || be32(d, 8) != 0x0003_4000 {
            r.add(.warning, "the first words are not the ones every character file has (00040000 at 0x0, 00034000 at 0x8)")
        }
        if Int32(bitPattern: be32(d, 4)) < 0 { r.add(.warning, "cash is negative") }
        let worn = Int(be32(d, 0x414)), owned = Int(be32(d, 0xC18))
        if worn > WardrobeTables.maxWorn { r.add(.damaged, "it says \(worn) worn items; there is room for \(WardrobeTables.maxWorn)") }
        if owned > MpCharacter.ownedMax { r.add(.damaged, "it says \(owned) owned items; there is room for \(MpCharacter.ownedMax)") }
        else {
            var seen = Set<ClothingPair>(), twice = 0
            for k in 0..<owned where !seen.insert(ClothingPair(item: Int(be32(d, 0xC1C + 12 * k)), variant: Int(be32(d, 0xC20 + 12 * k)))).inserted { twice += 1 }
            if twice > 0 { r.add(.note, "\(twice) owned items are listed twice") }
        }
    }

    // MARK: Repair

    /// The newest backup (the editor's folder and the ones next to the file) that the game would load.
    static func goodBackup(for url: URL) -> URL? {
        let all = (EditorBackups.list(for: url) + Backups.list(for: url)).sorted { modified($0) > modified($1) }
        let isStoryFile = isStory(url, (try? Data(contentsOf: url)).map { [UInt8]($0) } ?? [])
        for b in all {
            guard let data = try? Data(contentsOf: b) else { continue }
            let r = SaveReport(url: b)
            if isStoryFile { story(r, [UInt8](data)) } else { online(r, [UInt8](data)) }
            if r.worst < .damaged { return b }
        }
        return nil
    }

    /// Makes a damaged save load again: a wrong checksum is rewritten; anything worse is replaced
    /// by the newest good backup. The file as it was is kept first. Returns what was done, or nil
    /// when nothing could be done.
    static func repair(_ url: URL) throws -> String? {
        let r = check(url)
        if !r.dataDamaged { return r.worst < .damaged ? "nothing to repair" : nil }   // a folder name is not ours to change
        if r.checksumOnly {
            var d = [UInt8](try Data(contentsOf: url))
            EditorBackups.keep(url, reason: "before repair")
            _ = try Backups.keep(url)
            StoryChecksum.set(&d)
            try Data(d).write(to: url, options: .atomic)
            return "checksum rewritten"
        }
        guard let good = goodBackup(for: url) else { return nil }
        EditorBackups.keep(url, reason: "before repair")
        if FileManager.default.fileExists(atPath: url.path) { _ = try Backups.keep(url) }
        var copy = [UInt8](try Data(contentsOf: good))
        if r.kind == "story", StoryChecksum.stored(copy) != StoryChecksum.wanted(copy) { StoryChecksum.set(&copy) }
        try Data(copy).write(to: url, options: .atomic)
        return "restored \(good.lastPathComponent)"
    }
}

/// An outfit in a file of its own (~/Documents/Saints Reborn Outfits/*.sroutfit), to keep it or
/// put it on another save or the online character. One line per worn item: item id, variant id,
/// body slot, wear option (as the saves keep them). The Windows version reads the same files.
enum OutfitFile {
    static let fileExtension = "sroutfit"
    private static let magic = "Saints Reborn outfit 1"

    static var folder: URL {
        (FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Documents"))
            .appendingPathComponent("Saints Reborn Outfits")
    }

    static func write(_ outfit: [WornItem], to url: URL, from: String) throws {
        let stamp = DateFormatter(); stamp.dateFormat = "yyyy-MM-dd HH:mm"
        var s = "\(magic)\n# from \(from), \(stamp.string(from: Date()))\n# item variant slot wear-option\n"
        for w in outfit { s += "\(w.item) \(w.variant) \(w.slot) \(w.mesh)\n" }
        try s.write(to: url, atomically: true, encoding: .utf8)
    }

    static func read(_ url: URL) throws -> [WornItem] {
        let lines = try String(contentsOf: url, encoding: .utf8).components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }
        guard lines.first == magic else { throw SaveError(description: "This is not an outfit file.") }
        var outfit: [WornItem] = [], slots = Set<Int>()
        for (i, l) in lines.enumerated().dropFirst() where !l.isEmpty && !l.hasPrefix("#") {
            let p = l.split(whereSeparator: { $0 == " " || $0 == "\t" }).compactMap { Int($0) }
            guard p.count == 4 else { throw SaveError(description: "Line \(i + 1) of the outfit file can't be read.") }
            if slots.insert(p[2]).inserted { outfit.append(WornItem(item: p[0], variant: p[1], slot: p[2], mesh: p[3])) }
        }
        guard outfit.count <= WardrobeTables.maxWorn else { throw SaveError(description: "The outfit has more items than a save has room for.") }
        return outfit
    }

    /// The pieces the online character can keep: clothing multiplayer does not sell is left off.
    static func forOnline(_ outfit: [WornItem], wardrobe: WardrobeTables?, store: Set<ClothingPair>) -> (outfit: [WornItem], skipped: Int) {
        let use = outfit.filter { w in !(wardrobe?.isClothing(w.item) == true && !store.contains(ClothingPair(item: w.item, variant: w.variant))) }
        return (use, outfit.count - use.count)
    }

    /// Writes the outfit into a story save or an online character, keeping backups. Returns a note.
    static func wear(_ outfit: [WornItem], in url: URL, wardrobe: WardrobeTables?, store: Set<ClothingPair>) throws -> String {
        let story = SaveCheck.isStory(url, [UInt8](try Data(contentsOf: url)))
        EditorBackups.keep(url, reason: "opened")
        if story {
            let s = try SinglePlayerSave(url: url)
            s.outfit = outfit
            for w in outfit where wardrobe?.isClothing(w.item) == true && !s.owned.contains(ClothingPair(item: w.item, variant: w.variant)) {
                s.owned.append(ClothingPair(item: w.item, variant: w.variant))
            }
            _ = try s.write()
            return "done"
        }
        let (use, skipped) = forOnline(outfit, wardrobe: wardrobe, store: store)
        let c = try MpCharacter(url: url)
        c.outfit = use
        _ = try c.write(store: store)
        return "done" + (skipped > 0 ? " (\(skipped) piece(s) not sold in multiplayer left off)" : "")
    }
}

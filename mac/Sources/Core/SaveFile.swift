// Single-player save (game.dat) of Saints Row (Xbox 360), as written by Saints Reborn.
// Big-endian. A fixed header (0x7000 bytes) holds counts and pointers; the lists they
// point to follow it. Pointers are offsets from the start of the file.
import Foundation

struct SaveError: Error, CustomStringConvertible { let description: String }

struct Neighbourhood { let name: String; let ownerOffset: Int; var owner: UInt32 }
struct Contact { let name: String; let flagOffset: Int; var unlocked: Bool }
struct CarriedWeapon: Equatable { var name: String; var count: UInt32 = 1; var clip: UInt32; var reserve: UInt32 }
struct StashWeapon { let hash: UInt32; let flagOffset: Int; var unlocked: Bool }
/// A car in the garage: 1160 bytes the game wrote. The first words are 0, the car's number,
/// the name hash of the model, and a garage number; the rest is paint, parts and condition.
struct GarageVehicle: Equatable {
    static let size = 0x488
    var raw: [UInt8]
    private func word(_ o: Int) -> UInt32 { UInt32(raw[o]) << 24 | UInt32(raw[o + 1]) << 16 | UInt32(raw[o + 2]) << 8 | UInt32(raw[o + 3]) }
    private mutating func set(_ o: Int, _ v: UInt32) { raw[o] = UInt8(v >> 24); raw[o + 1] = UInt8((v >> 16) & 255); raw[o + 2] = UInt8((v >> 8) & 255); raw[o + 3] = UInt8(v & 255) }
    var id: UInt32 { get { word(4) } set { set(4, newValue) } }
    var hash: UInt32 { get { word(8) } set { set(8, newValue) } }
}

final class SinglePlayerSave {
    static let gangs: [UInt32: String] = [0: "3rd Street Saints", 1: "Los Carnales", 2: "Vice Kings", 3: "Westside Rollerz"]
    private static let respectOffset = 0x90, respectTotalOffset = 0x94, respectNeededOffset = 0x98, cashOffset = 0xA0
    // (count, pointer) pairs in the header.
    private static let weaponsAt = 0x144, foodAt = 0x240, contactsAt = 0x6E88, hoodsAt = 0x6E90, stashAt = 0x6F8C
    private static let endPointerAt = 0x6FF8
    // The garage: count and pointer to 1160-byte records; the next car number; and the list
    // that follows the garage in the game's own layout (so a garage pointer past it is one this editor placed).
    private static let garageAt = 0x6E98, nextVehicleAt = 0x6EA0, afterGarageAt = 0x6EA8
    // What the player wears: count, then 16-byte records in the header (room for 128).
    private static let outfitCountAt = 0x64C, outfitAt = 0x650
    // Clothing owned: count, then 12-byte records (item, variant, 0) in the header (room for 2048).
    private static let ownedCountAt = 0xE50, ownedAt = 0xE54, ownedMax = 2048
    private static let unlockedFlag: UInt32 = 0x8000_0000

    private(set) var data: Data
    let url: URL
    private(set) var neighbourhoods: [Neighbourhood] = []
    private(set) var contacts: [Contact] = []
    private(set) var stash: [StashWeapon] = []
    /// Weapons on the wheel (name, magazine, spare ammo) and the food carried, in file order.
    var weapons: [CarriedWeapon] = []
    var food: [String] = []
    /// The cars in the garage, in the game's order.
    var vehicles: [GarageVehicle] = []
    private var loadedVehicles: [GarageVehicle] = []
    /// What the player wears, one record per body slot.
    var outfit: [WornItem] = []
    /// Clothing owned (bought or worn), in the game's order.
    var owned: [ClothingPair] = []
    private var loadedOwned: [ClothingPair] = []
    private var loadedWeapons: [CarriedWeapon] = []
    private var loadedFood: [String] = []
    private var loadedOutfit: [WornItem] = []

    init(url: URL) throws {
        self.url = url
        data = try Data(contentsOf: url)
        guard data.count >= 0x7800, u32(4) == 0x17 else {
            throw SaveError(description: "This is not a Saints Row save this editor knows (version \(data.count >= 8 ? String(u32(4)) : "?")).")
        }
        readTables()
    }

    func u32(_ o: Int) -> UInt32 {
        guard o >= 0, o + 4 <= data.count else { return 0 }
        return UInt32(data[o]) << 24 | UInt32(data[o + 1]) << 16 | UInt32(data[o + 2]) << 8 | UInt32(data[o + 3])
    }
    private static func bytes(_ v: UInt32) -> [UInt8] { [UInt8(v >> 24), UInt8((v >> 16) & 255), UInt8((v >> 8) & 255), UInt8(v & 255)] }
    private func setU32(_ o: Int, _ v: UInt32) { data.replaceSubrange(o..<o + 4, with: Self.bytes(v)) }

    private func name(at pointer: UInt32) -> String? {
        let o = Int(pointer)
        guard o >= 0x7000, o < data.count else { return nil }
        var bytes: [UInt8] = []
        var i = o
        while i < data.count, data[i] != 0, bytes.count < 64 { bytes.append(data[i]); i += 1 }
        guard bytes.count >= 2, i < data.count, bytes.allSatisfy({ $0 >= 32 && $0 < 127 }) else { return nil }
        return String(decoding: bytes, as: UTF8.self)
    }
    private func list(_ at: Int, recordSize: Int, limit: Int) -> (count: Int, pointer: Int)? {
        let count = Int(u32(at)), pointer = Int(u32(at + 4))
        guard count <= limit, pointer >= 0x7000, pointer + count * recordSize <= data.count else { return nil }
        return (count, pointer)
    }

    private func readTables() {
        if let l = list(Self.weaponsAt, recordSize: 16, limit: 64) {
            for i in 0..<l.count {
                let o = l.pointer + i * 16
                if let n = name(at: u32(o)) { weapons.append(CarriedWeapon(name: n, count: u32(o + 4), clip: u32(o + 8), reserve: u32(o + 12))) }
            }
        }
        if let l = list(Self.foodAt, recordSize: 4, limit: 64) {
            for i in 0..<l.count { if let n = name(at: u32(l.pointer + i * 4)) { food.append(n) } }
        }
        if let l = list(Self.garageAt, recordSize: GarageVehicle.size, limit: 128) {
            for i in 0..<l.count { vehicles.append(GarageVehicle(raw: Array(data[l.pointer + i * GarageVehicle.size..<l.pointer + (i + 1) * GarageVehicle.size]))) }
        }
        loadedWeapons = weapons; loadedFood = food; loadedVehicles = vehicles
        outfit = [WornItem].read(u32, countAt: Self.outfitCountAt, at: Self.outfitAt, max: WardrobeTables.maxWorn)
        loadedOutfit = outfit
        for k in 0..<Int(min(u32(Self.ownedCountAt), UInt32(Self.ownedMax))) {
            owned.append(ClothingPair(item: Int(u32(Self.ownedAt + 12 * k)), variant: Int(u32(Self.ownedAt + 12 * k + 4))))
        }
        loadedOwned = owned
        if let l = list(Self.contactsAt, recordSize: 12, limit: 64) {
            for i in 0..<l.count {
                let o = l.pointer + i * 12
                if let n = name(at: u32(o)) { contacts.append(Contact(name: n, flagOffset: o + 8, unlocked: u32(o + 8) == 1)) }
            }
        }
        if let l = list(Self.hoodsAt, recordSize: 12, limit: 128) {
            for i in 0..<l.count {
                let o = l.pointer + i * 12
                if let n = name(at: u32(o)) { neighbourhoods.append(Neighbourhood(name: n, ownerOffset: o + 4, owner: u32(o + 4))) }
            }
        }
        if let l = list(Self.stashAt, recordSize: 8, limit: 128) {
            for i in 0..<l.count {
                let o = l.pointer + i * 8
                stash.append(StashWeapon(hash: u32(o), flagOffset: o + 4, unlocked: u32(o + 4) & Self.unlockedFlag != 0))
            }
        }
    }

    var cash: Double {
        get { Double(Int32(bitPattern: u32(Self.cashOffset))) / 100 }
        set { setU32(Self.cashOffset, UInt32(bitPattern: Int32(max(0, min(newValue, 20_000_000)) * 100))) }
    }
    var respect: UInt32 {
        get { u32(Self.respectOffset) }
        set { setU32(Self.respectOffset, newValue); setU32(Self.respectTotalOffset, newValue) }
    }
    var respectNeeded: UInt32 { u32(Self.respectNeededOffset) }

    func setOwner(of index: Int, to gang: UInt32) {
        guard neighbourhoods.indices.contains(index), Self.gangs[gang] != nil else { return }
        neighbourhoods[index].owner = gang
        setU32(neighbourhoods[index].ownerOffset, gang)
    }
    func setContact(_ index: Int, unlocked: Bool) {
        guard contacts.indices.contains(index) else { return }
        contacts[index].unlocked = unlocked
        setU32(contacts[index].flagOffset, unlocked ? 1 : 0)
    }
    func setStash(_ index: Int, unlocked: Bool) {
        guard stash.indices.contains(index) else { return }
        stash[index].unlocked = unlocked
        let old = u32(stash[index].flagOffset)
        setU32(stash[index].flagOffset, unlocked ? old | Self.unlockedFlag : old & ~Self.unlockedFlag)
    }

    /// Another car like this one, with the next free number.
    func duplicateVehicle(at index: Int) {
        guard vehicles.indices.contains(index) else { return }
        var v = vehicles[index]
        v.id = u32(Self.nextVehicleAt)
        setU32(Self.nextVehicleAt, v.id + 1)
        vehicles.append(v)
    }

    /// Puts the weapon and food lists, and the garage once it has been changed, after the
    /// game's own data. The game follows the pointers, so the lists may sit anywhere; the
    /// file grows in steps of 2048 bytes.
    private func writeInventory() {
        // A garage this editor placed earlier sits past the list that follows it in the game's layout.
        let garageOurs = u32(Self.garageAt + 4) > u32(Self.afterGarageAt)
        let garageChanged = vehicles != loadedVehicles
        guard weapons != loadedWeapons || food != loadedFood || garageChanged else { return }
        // Lists this editor wrote earlier sit after everything else; write over them.
        let firstTable = Int(u32(Self.contactsAt + 4))
        var ours = [Int(u32(Self.weaponsAt + 4)), Int(u32(Self.foodAt + 4))].filter { $0 > firstTable }
        if garageOurs { ours.append(Int(u32(Self.garageAt + 4))) }
        let start = ours.min() ?? Int(u32(Self.endPointerAt))
        guard start >= 0x7000, start <= data.count else { return }

        var block: [UInt8] = []
        func pad() { while block.count % 16 != 0 { block.append(0) } }
        func text(_ s: String) -> UInt32 {
            let at = UInt32(start + block.count)
            block += Array(s.utf8) + [0]; pad()
            return at
        }
        let weaponNames = weapons.map { text($0.name) }
        let foodNames = food.map { text($0) }
        let weaponsPointer = start + block.count
        for (w, n) in zip(weapons, weaponNames) { block += Self.bytes(n) + Self.bytes(w.count) + Self.bytes(w.clip) + Self.bytes(w.reserve) }
        let foodPointer = start + block.count
        for n in foodNames { block += Self.bytes(n) }
        pad()
        var garagePointer: Int? = nil
        if garageOurs || garageChanged {
            garagePointer = start + block.count
            for v in vehicles { block += v.raw }
            pad()
        }

        let end = start + block.count
        var out = Data(data.prefix(start))
        out.append(contentsOf: block)
        out.append(Data(count: max(0x8800, (end + 0x7FF) & ~0x7FF) - end))
        data = out
        setU32(Self.weaponsAt, UInt32(weapons.count)); setU32(Self.weaponsAt + 4, UInt32(weaponsPointer))
        setU32(Self.foodAt, UInt32(food.count)); setU32(Self.foodAt + 4, UInt32(foodPointer))
        if let g = garagePointer { setU32(Self.garageAt, UInt32(vehicles.count)); setU32(Self.garageAt + 4, UInt32(g)) }
        setU32(Self.endPointerAt, UInt32(end))
        loadedWeapons = weapons; loadedFood = food; loadedVehicles = vehicles
    }

    /// Writes the save, keeping the previous file next to it as game.dat.<date>-<time>.bak.
    private func writeOutfit() {
        guard outfit != loadedOutfit else { return }
        let worn = Array(outfit.prefix(WardrobeTables.maxWorn))
        setU32(Self.outfitCountAt, UInt32(worn.count))
        data.replaceSubrange(Self.outfitAt..<Self.outfitAt + WardrobeTables.maxWorn * 16, with: worn.records(max: WardrobeTables.maxWorn))
        loadedOutfit = outfit
    }
    private func writeOwned() {
        guard owned != loadedOwned else { return }
        let list = Array(owned.prefix(Self.ownedMax))
        setU32(Self.ownedCountAt, UInt32(list.count))
        var block = [UInt8](repeating: 0, count: Self.ownedMax * 12)
        for (k, p) in list.enumerated() {
            block.replaceSubrange(12 * k..<12 * k + 4, with: Self.bytes(UInt32(p.item)))
            block.replaceSubrange(12 * k + 4..<12 * k + 8, with: Self.bytes(UInt32(p.variant)))
        }
        data.replaceSubrange(Self.ownedAt..<Self.ownedAt + Self.ownedMax * 12, with: block)
        loadedOwned = owned
    }

    func write() throws -> URL {
        writeInventory()
        writeOutfit()
        writeOwned()
        let backup = try Backups.keep(url)
        try data.write(to: url, options: .atomic)
        return backup
    }
}

/// Dated copies of a file next to it: <name>.<date>-<time>.bak.
enum Backups {
    static func keep(_ url: URL) throws -> URL {
        let stamp = DateFormatter(); stamp.dateFormat = "yyyyMMdd-HHmmss"
        let folder = url.deletingLastPathComponent(), base = "\(url.lastPathComponent).\(stamp.string(from: Date()))"
        var backup = folder.appendingPathComponent(base + ".bak"), n = 2
        while FileManager.default.fileExists(atPath: backup.path) { backup = folder.appendingPathComponent("\(base)-\(n).bak"); n += 1 }
        try FileManager.default.copyItem(at: url, to: backup)
        return backup
    }
    /// The backups of a file, newest first.
    static func list(for url: URL) -> [URL] {
        let folder = url.deletingLastPathComponent(), prefix = url.lastPathComponent + "."
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        func date(_ u: URL) -> Date { (try? u.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast }
        return files.filter { $0.lastPathComponent.hasPrefix(prefix) && $0.pathExtension == "bak" }.sorted { date($0) > date($1) }
    }
    /// Puts a backup in place of the file; the file as it is now is kept as a backup first.
    static func restore(_ backup: URL, to url: URL) throws -> URL {
        let kept = try keep(url)
        let data = try Data(contentsOf: backup)
        try data.write(to: url, options: .atomic)
        return kept
    }
}

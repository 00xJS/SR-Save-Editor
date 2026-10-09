// Names and limits read from the player's own game files (misc.vpp_xbox2).
// The program itself contains no game data.
import Foundation

struct WeaponInfo { let name, title, weaponClass: String; let magazine, maxRounds: Int; let slot: Int }
struct CheatInfo: Identifiable { let id: Int; let name, number, category: String }
struct VehicleInfo: Identifiable {
    let name, title, category: String
    /// Ten times the table's Max_Hitpoints (5000 when it is not given): the saves count in tenths.
    let maxHitpoints: UInt32
    var id: String { name }; var hash: UInt32 { GameTables.hash(name) }
}

final class GameTables {
    /// Wheel slots, clockwise from the top.
    static let slotNames = ["Unarmed", "Melee", "Pistol", "SMG", "Shotgun", "Rifle", "Launcher", "Thrown"]
    private static let slotOfClass: [String: Int] = ["bat": 1, "knife": 1, "nightstick": 1, "pimp slap": 1, "pistol": 2,
                                                     "smg": 3, "shotgun": 4, "rifle": 5, "launcher": 6, "thrown": 7]

    private(set) var strings: [String: String] = [:]
    private(set) var weapons: [String: WeaponInfo] = [:]     // by lower-case name
    private(set) var offered: [String] = []                  // sold in stores or kept at the crib
    private(set) var food: [String] = []
    private(set) var cheats: [CheatInfo] = []
    private(set) var vehicles: [VehicleInfo] = []
    private var vehicleByHash: [UInt32: VehicleInfo] = [:]
    private(set) var homieTitles: [String: String] = [:]
    private var hashed: [UInt32: String] = [:]
    let pack: Packfile
    let packfilesFolder: URL
    private(set) lazy var icons: GameIcons? = GameIcons(tables: self, packfiles: packfilesFolder)

    static func find(near url: URL?) -> URL? {
        var tries: [URL] = []
        var dir = url
        for _ in 0..<8 { guard let d = dir else { break }; tries.append(d); dir = d.path == "/" ? nil : d.deletingLastPathComponent() }
        tries.append(FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("SaintsReborn/dist"))
        for t in tries {
            for sub in ["game/packfiles/misc.vpp_xbox2", "packfiles/misc.vpp_xbox2", "dist/game/packfiles/misc.vpp_xbox2"] {
                let f = t.appendingPathComponent(sub)
                if FileManager.default.fileExists(atPath: f.path) { return f }
            }
        }
        return nil
    }

    init(packfile: URL) throws {
        pack = try Packfile(url: packfile)
        packfilesFolder = packfile.deletingLastPathComponent()
        if let d = pack.read("US_Strings.txt"), let text = String(data: d, encoding: .utf16) {
            for raw in text.split(whereSeparator: { $0 == "\n" || $0 == "\r\n" }) {
                let line = raw.trimmingCharacters(in: CharacterSet(charactersIn: "\r\u{FEFF} "))
                guard let eq = line.firstIndex(of: "="), eq != line.startIndex else { continue }
                strings[line[..<eq].trimmingCharacters(in: .whitespaces)] = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            }
        }
        for block in blocks("weapons.xtbl", "Weapon") {
            guard let name = tag(block, "Name") else { continue }
            let cls = (tag(block, "Weapon_Class") ?? "").lowercased()
            weapons[name.lowercased()] = WeaponInfo(name: name, title: title(name), weaponClass: cls,
                                                    magazine: Int(tag(block, "Magazine_Size") ?? "") ?? 0,
                                                    maxRounds: Int(tag(block, "Max_Rounds") ?? "") ?? 0,
                                                    slot: Self.slotOfClass[cls] ?? -1)
        }
        var seen = Set<String>()
        for file in ["store_weapons.xtbl", "crib_weapons.xtbl"] {
            for block in blocks(file, "Entry") {
                guard let w = tag(block, "Weapon")?.lowercased(), weapons[w] != nil, seen.insert(w).inserted else { continue }
                offered.append(w)
                hashed[Self.hash(w)] = w
            }
        }
        for block in blocks("vehicles.xtbl", "Vehicle") {
            guard let n = tag(block, "Name") else { continue }
            let v = VehicleInfo(name: n, title: tag(block, "Display_Name") ?? n, category: tag(block, "Category") ?? "",
                                maxHitpoints: UInt32((Int(tag(block, "Max_Hitpoints") ?? "") ?? 5000) * 10))
            vehicles.append(v); vehicleByHash[v.hash] = v
        }
        vehicles.sort { $0.title < $1.title }
        for block in blocks("drugs.xtbl", "Drug") { if let n = tag(block, "Name") { food.append(n) } }
        for block in blocks("homies.xtbl", "Homie") {
            if let n = tag(block, "Name"), let t = tag(block, "Display_Name") { homieTitles[n] = strings[t] ?? t }
        }
        for (i, block) in blocks("cheats.xtbl", "Cheats").enumerated() {
            guard let n = tag(block, "Name"), let number = tag(block, "Number") else { continue }
            let category = (tag(block, "Category") ?? "").replacingOccurrences(of: "Entries:", with: "")
            cheats.append(CheatInfo(id: i, name: strings[tag(block, "DisplayName") ?? ""] ?? n, number: number, category: category))
        }
    }

    /// The top-level <element> blocks of one of the game's tables.
    private func blocks(_ file: String, _ element: String) -> [String] {
        guard let d = pack.read(file), let text = String(data: d, encoding: .isoLatin1) else { return [] }
        let parts = text.components(separatedBy: "<\(element)>").dropFirst()
        return parts.map { $0.components(separatedBy: "</\(element)>").first ?? $0 }
    }
    private func tag(_ block: String, _ name: String) -> String? {
        guard let a = block.range(of: "<\(name)>"), let b = block.range(of: "</\(name)>", range: a.upperBound..<block.endIndex) else { return nil }
        let v = block[a.upperBound..<b.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
        return v.isEmpty || v.contains("<") ? nil : v
    }

    func title(_ name: String) -> String {
        if let s = strings[name] ?? strings[name.lowercased()], !s.isEmpty { return s }
        return name.replacingOccurrences(of: "_", with: " ").capitalized
    }
    func weapon(_ name: String) -> WeaponInfo? { weapons[name.lowercased()] }
    func choices(slot: Int) -> [WeaponInfo] { offered.compactMap { weapons[$0] }.filter { $0.slot == slot } }
    func name(hash: UInt32) -> String? { hashed[hash] }
    func vehicle(hash: UInt32) -> VehicleInfo? { vehicleByHash[hash] }
    func vehicle(named name: String) -> VehicleInfo? { vehicles.first { $0.name.lowercased() == name.lowercased() } }

    /// The game's name hash: CRC-32 (reflected, start 0, no final inversion) of the lower-case name.
    static func hash(_ name: String) -> UInt32 {
        var c: UInt32 = 0
        for byte in name.lowercased().utf8 {
            c ^= UInt32(byte)
            for _ in 0..<8 { c = c & 1 != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1 }
        }
        return c
    }
}

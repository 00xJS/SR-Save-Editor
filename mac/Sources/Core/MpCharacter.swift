// Multiplayer character (3_MPStorage, 27,724 bytes, big-endian): cash and owned clothing.
import Foundation

struct ClothingPair: Hashable { let item: Int, variant: Int }
struct ClothingChoice: Identifiable { let pair: ClothingPair; let title, style, category: String; var id: ClothingPair { pair } }

/// The clothing sold in multiplayer, read from the player's own game files.
final class ClothingTables {
    private(set) var store: [ClothingChoice] = []
    private var titles: [ClothingPair: ClothingChoice] = [:]
    private static let mpStore = "Multiplayer Purchase Clothing"

    init(tables: GameTables) {
        guard let itemsData = tables.pack.read("customization_items.xtbl"), let storesData = tables.pack.read("customization_stores.xtbl"),
              let items = try? XMLDocument(data: itemsData), let stores = try? XMLDocument(data: storesData) else { return }
        func str(_ n: XMLNode, _ child: String) -> String {
            ((n as? XMLElement)?.elements(forName: child).first?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        func lookup(_ key: String, _ fallback: String) -> String {
            if let s = tables.strings[key], !s.isEmpty { return s }
            return fallback
        }
        var byName: [String: (id: Int, title: String, category: String, variants: [(id: Int, name: String, title: String)])] = [:]
        for n in (try? items.nodes(forXPath: "/root/Table/Customization_Item")) ?? [] {
            guard let id = Int(str(n, "ID")) else { continue }
            let name = str(n, "Name")
            var variants: [(id: Int, name: String, title: String)] = []
            for v in (try? n.nodes(forXPath: "Variants/Variant")) ?? [] {
                guard let vid = Int(str(v, "VariantID")) else { continue }
                let vname = str(v, "Variant_Name")
                variants.append((vid, vname, lookup(str(v, "Name"), vname)))
            }
            let entry = (id: id, title: lookup(str(n, "DisplayName"), name), category: str(n, "Category"), variants: variants)
            byName[name] = entry
            for v in variants {
                let pair = ClothingPair(item: id, variant: v.id)
                titles[pair] = ClothingChoice(pair: pair, title: entry.title, style: v.title, category: entry.category)
            }
        }
        var seen = Set<ClothingPair>()
        for s in (try? stores.nodes(forXPath: "/root/Table/Store")) ?? [] where str(s, "Name") == Self.mpStore {
            for si in (try? s.nodes(forXPath: "Items/Store_Item")) ?? [] {
                guard let item = byName[str(si, "Item")] else { continue }
                let wanted = Set(((try? si.nodes(forXPath: "Variants/Item_Variant/Variant")) ?? []).map { ($0.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines) })
                for v in item.variants where wanted.isEmpty || wanted.contains(v.name) {
                    let pair = ClothingPair(item: item.id, variant: v.id)
                    // Listed in the store table, but the game drops these three Football Jersey styles on load.
                    if pair.item == 235 && [0, 512, 555].contains(pair.variant) { continue }
                    if seen.insert(pair).inserted, let c = titles[pair] { store.append(c) }
                }
            }
        }
    }
    func choice(_ pair: ClothingPair) -> ClothingChoice {
        titles[pair] ?? ClothingChoice(pair: pair, title: "Item \(pair.item)", style: "Style \(pair.variant)", category: "")
    }
}

final class MpCharacter {
    static let size = 27724, ownedMax = 2048
    private static let cashAt = 0x4, wornCountAt = 0x414, wornAt = 0x418, wornMax = 128, ownedCountAt = 0xC18, ownedAt = 0xC1C
    let url: URL
    private var data: [UInt8]
    var cents: UInt32
    var owned: [ClothingPair] = []
    private(set) var wearing: Set<ClothingPair> = []

    static func players(game: URL) -> [(name: String, file: URL)] {
        let root = game.appendingPathComponent("545107D1/profile")
        let dirs = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return dirs.map { ($0.lastPathComponent, $0.appendingPathComponent("storage/3_MPStorage")) }
            .filter { FileManager.default.fileExists(atPath: $0.1.path) }.sorted { $0.0 < $1.0 }
    }

    private func u32(_ o: Int) -> UInt32 { UInt32(data[o]) << 24 | UInt32(data[o + 1]) << 16 | UInt32(data[o + 2]) << 8 | UInt32(data[o + 3]) }
    private func put(_ o: Int, _ v: UInt32) { data[o] = UInt8(v >> 24); data[o + 1] = UInt8((v >> 16) & 255); data[o + 2] = UInt8((v >> 8) & 255); data[o + 3] = UInt8(v & 255) }

    init(url: URL) throws {
        self.url = url
        data = [UInt8](try Data(contentsOf: url))
        guard data.count == Self.size else {
            throw SaveError(description: "This file is \(data.count) bytes; a multiplayer character file is \(Self.size).")
        }
        cents = 0
        cents = u32(Self.cashAt)
        for k in 0..<Int(min(u32(Self.wornCountAt), UInt32(Self.wornMax))) {
            wearing.insert(ClothingPair(item: Int(u32(Self.wornAt + 16 * k)), variant: Int(u32(Self.wornAt + 16 * k + 4))))
        }
        for k in 0..<Int(min(u32(Self.ownedCountAt), UInt32(Self.ownedMax))) {
            owned.append(ClothingPair(item: Int(u32(Self.ownedAt + 12 * k)), variant: Int(u32(Self.ownedAt + 12 * k + 4))))
        }
    }

    /// Writes cash and the owned list, keeping the previous file as 3_MPStorage.<date>-<time>.bak.
    func write() throws -> URL {
        guard owned.count <= Self.ownedMax else { throw SaveError(description: "The file has room for \(Self.ownedMax) owned items.") }
        put(Self.cashAt, cents)
        put(Self.ownedCountAt, UInt32(owned.count))
        for i in Self.ownedAt..<Self.ownedAt + Self.ownedMax * 12 { data[i] = 0 }
        for (k, p) in owned.enumerated() {
            put(Self.ownedAt + 12 * k, UInt32(p.item)); put(Self.ownedAt + 12 * k + 4, UInt32(p.variant))
        }
        let backup = try Backups.keep(url)
        try Data(data).write(to: url, options: .atomic)
        return backup
    }
}

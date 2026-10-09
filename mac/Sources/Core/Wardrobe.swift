// What the player wears. The story save and the online character both keep an
// outfit as 16-byte records: item id, variant id, slot number, wear option
// (MeshID). The wear option decides the slot: the game's own slot table
// (player_cust.xtbl) lists every mesh under its slot.
import Foundation

struct WornItem: Equatable, Hashable {
    var item: Int, variant: Int, slot: Int, mesh: Int
}

struct WardrobeVariant: Identifiable, Hashable { let id: Int; let title: String }
struct WearOption: Identifiable, Hashable { let id: Int; let title: String; let slot: Int }   // id is the MeshID; slot -1 = unknown
struct WardrobeItem: Identifiable {
    let id: Int
    let name, title, category: String
    let variants: [WardrobeVariant]
    let options: [WearOption]
    func options(in slot: Int) -> [WearOption] { options.filter { $0.slot == slot } }
}

/// A slot on the body, with the number the save files use for it.
struct BodySlot: Identifiable, Hashable {
    let id: Int          // the number in the save
    let name: String     // the game's slot name
    let title: String
}

/// Clothing, hair and jewellery from the player's own game files, by body slot.
final class WardrobeTables {
    /// The slots this editor can dress, in the order they are shown. The numbers were read
    /// from saves; the coat's (7) is the one gap between the shirts and the underwear.
    static let slots: [BodySlot] = [
        BodySlot(id: 0, name: "hair head", title: "Hair"), BodySlot(id: 4, name: "hair eyebrows", title: "Eyebrows"),
        BodySlot(id: 53, name: "eyes", title: "Eyes"), BodySlot(id: 3, name: "hair sideburns", title: "Sideburns"),
        BodySlot(id: 1, name: "hair mustache", title: "Mustache"), BodySlot(id: 2, name: "hair beard", title: "Beard"),
        BodySlot(id: 12, name: "headwear", title: "Hat"), BodySlot(id: 24, name: "eyewear", title: "Eyewear"),
        BodySlot(id: 5, name: "undershirt", title: "Undershirt"), BodySlot(id: 6, name: "overshirt", title: "Overshirt"),
        BodySlot(id: 7, name: "coat", title: "Coat"),
        BodySlot(id: 19, name: "necklace high", title: "Necklace, high"), BodySlot(id: 18, name: "necklace medium", title: "Necklace, medium"),
        BodySlot(id: 17, name: "necklace low", title: "Necklace, low"), BodySlot(id: 21, name: "medallion medium", title: "Medallion, medium"),
        BodySlot(id: 20, name: "medallion low", title: "Medallion, low"), BodySlot(id: 22, name: "pennant high", title: "Pendant"),
        BodySlot(id: 15, name: "left wrist", title: "Left wrist"), BodySlot(id: 16, name: "right wrist", title: "Right wrist"),
        BodySlot(id: 25, name: "finger left index", title: "Left index finger"), BodySlot(id: 27, name: "finger left ring", title: "Left ring finger"),
        BodySlot(id: 26, name: "finger right index", title: "Right index finger"), BodySlot(id: 28, name: "finger right ring", title: "Right ring finger"),
        BodySlot(id: 8, name: "underwear", title: "Underwear"), BodySlot(id: 9, name: "pants", title: "Pants"),
        BodySlot(id: 10, name: "socks", title: "Socks"), BodySlot(id: 11, name: "shoes", title: "Shoes"),
    ]
    static func slot(_ id: Int) -> BodySlot? { slots.first { $0.id == id } }
    /// Slots the editor does not know the number of (belts, piercings, tattoos, skin) stay as they are.
    static let maxWorn = 128

    private(set) var items: [Int: WardrobeItem] = [:]
    private(set) var byName: [String: Int] = [:]
    private(set) var meshSlot: [Int: String] = [:]   // MeshID -> the game's slot name

    init(tables: GameTables) {
        func str(_ n: XMLNode, _ child: String) -> String {
            ((n as? XMLElement)?.elements(forName: child).first?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        func lookup(_ key: String, _ fallback: String) -> String {
            if let s = tables.strings[key], !s.isEmpty { return s }
            return fallback.replacingOccurrences(of: "_", with: " ").capitalized
        }
        if let data = tables.pack.read("player_cust.xtbl"), let doc = try? XMLDocument(data: data) {
            for s in (try? doc.nodes(forXPath: "/root/Table/Custom_player_slot")) ?? [] {
                let slot = str(s, "Slot")
                for m in (try? s.nodes(forXPath: "Components/Component/MeshID")) ?? [] {
                    if let id = Int((m.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)) { meshSlot[id] = slot }
                }
            }
        }
        let numbers = Dictionary(uniqueKeysWithValues: Self.slots.map { ($0.name, $0.id) })
        guard let data = tables.pack.read("customization_items.xtbl"), let doc = try? XMLDocument(data: data) else { return }
        for n in (try? doc.nodes(forXPath: "/root/Table/Customization_Item")) ?? [] {
            guard let id = Int(str(n, "ID")) else { continue }
            let name = str(n, "Name")
            var variants: [WardrobeVariant] = []
            for v in (try? n.nodes(forXPath: "Variants/Variant")) ?? [] {
                guard let vid = Int(str(v, "VariantID")) else { continue }
                variants.append(WardrobeVariant(id: vid, title: lookup(str(v, "Name"), str(v, "Variant_Name"))))
            }
            var options: [WearOption] = []
            for w in (try? n.nodes(forXPath: "Wear_Options/Wear_Option")) ?? [] {
                guard let mesh = Int(str(w, "MeshID")), str(w, "Disabled").lowercased() != "yes" else { continue }
                let slot = meshSlot[mesh].flatMap { numbers[$0] } ?? -1
                options.append(WearOption(id: mesh, title: lookup(str(w, "Name"), "Normal"), slot: slot))
            }
            items[id] = WardrobeItem(id: id, name: name, title: lookup(str(n, "DisplayName"), name), category: str(n, "Category"), variants: variants, options: options)
            byName[name.lowercased()] = id
        }
    }

    /// Items that can go in a slot, in the game's order.
    func choices(slot: Int) -> [WardrobeItem] {
        items.values.filter { !$0.options(in: slot).isEmpty }.sorted { $0.id < $1.id }
    }
    func item(_ id: Int) -> WardrobeItem? { items[id] }
    func title(_ w: WornItem) -> String { items[w.item]?.title ?? "Item \(w.item)" }
    func style(_ w: WornItem) -> String { items[w.item]?.variants.first { $0.id == w.variant }?.title ?? "Style \(w.variant)" }
    func wear(_ w: WornItem) -> String { items[w.item]?.options.first { $0.id == w.mesh }?.title ?? "" }
    /// The slot the save will put a wear option in, or nil when the editor does not know it.
    func slot(ofMesh mesh: Int) -> Int? { meshSlot[mesh].flatMap { n in Self.slots.first { $0.name == n }?.id } }
}

extension Array where Element == WornItem {
    /// Puts an item in its slot, taking off whatever was there. The slot comes from the wear option.
    mutating func put(item: Int, variant: Int, mesh: Int, slot: Int) {
        let w = WornItem(item: item, variant: variant, slot: slot, mesh: mesh)
        if let i = firstIndex(where: { $0.slot == slot }) { self[i] = w } else { append(w) }   // keep the game's order
    }
    mutating func takeOff(slot: Int) { removeAll { $0.slot == slot } }
    func worn(in slot: Int) -> WornItem? { first { $0.slot == slot } }

    /// Reads `count` 16-byte records.
    static func read(_ u32: (Int) -> UInt32, countAt: Int, at: Int, max: Int) -> [WornItem] {
        (0..<Int(Swift.min(u32(countAt), UInt32(max)))).map { k in
            WornItem(item: Int(u32(at + 16 * k)), variant: Int(u32(at + 16 * k + 4)), slot: Int(u32(at + 16 * k + 8)), mesh: Int(u32(at + 16 * k + 12)))
        }
    }
    /// The 16-byte records, with zeros up to `max` records.
    func records(max: Int) -> [UInt8] {
        var out: [UInt8] = []
        func add(_ v: Int) { let u = UInt32(truncatingIfNeeded: v); out += [UInt8(u >> 24), UInt8((u >> 16) & 255), UInt8((u >> 8) & 255), UInt8(u & 255)] }
        for w in prefix(max) { add(w.item); add(w.variant); add(w.slot); add(w.mesh) }
        out += [UInt8](repeating: 0, count: max * 16 - out.count)
        return out
    }
}

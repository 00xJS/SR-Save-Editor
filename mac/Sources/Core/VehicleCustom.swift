// The paint and parts of a car in the garage, read from the player's own game files
// (vehicle_cust.xtbl and vehicle_cust_colors.xtbl in misc2.vpp_xbox2).
//
// In a car record, from 0x10: a flags byte, three constant bytes, 0x0B, the length of the
// variant section, the variant section (left as it is), then the number of entries and the
// entries. An entry is slot, component, number of colours, then (palette, colour) pairs,
// one per colour sub-slot of the component. Slots and components are indexes into the
// car's own table; palettes are indexes into the colour table, colours into the palette.
// The word at 0x154 is one less than the number of bytes from 0x10.
import Foundation

struct CustomColor: Hashable { var palette: Int; var color: Int }
struct CustomEntry: Equatable { var slot: Int; var component: Int; var colors: [CustomColor] }
struct CustomComponent { let name: String; let colorSlots: [String] }
struct CustomSlot: Identifiable {
    let id: Int; let name: String; let components: [CustomComponent]
    /// A colour slot has one component (Paint) with colour sub-slots; a part slot has parts to pick.
    var isColor: Bool { components.count == 1 && !(components[0].colorSlots.isEmpty) }
}
struct CustomPalette: Identifiable { let id: Int; let name: String; let colors: [String] }

final class VehicleCustomTables {
    private(set) var palettes: [CustomPalette] = []
    private var slotsByVehicle: [String: [CustomSlot]] = [:]

    init?(tables: GameTables) {
        let file = tables.packfilesFolder.appendingPathComponent("misc2.vpp_xbox2")
        guard let pack = try? Packfile(url: file) else { return nil }
        func str(_ n: XMLNode, _ child: String) -> String {
            ((n as? XMLElement)?.elements(forName: child).first?.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if let data = pack.read("vehicle_cust_colors.xtbl"), let doc = try? XMLDocument(data: data) {
            for (i, p) in ((try? doc.nodes(forXPath: "/root/Table/Custom_vehicle_palettes")) ?? []).enumerated() {
                let colors = ((try? p.nodes(forXPath: "Colors/Color")) ?? []).map { str($0, "Name") }
                palettes.append(CustomPalette(id: i, name: str(p, "Name"), colors: colors))
            }
        }
        if let data = pack.read("vehicle_cust.xtbl"), let doc = try? XMLDocument(data: data) {
            for v in (try? doc.nodes(forXPath: "/root/Table/Custom_vehicle_properties")) ?? [] {
                var slots: [CustomSlot] = []
                for (i, s) in ((try? v.nodes(forXPath: "Slots/Slot")) ?? []).enumerated() {
                    let comps = ((try? s.nodes(forXPath: "Components/Component")) ?? []).map { c in
                        CustomComponent(name: str(c, "Name"), colorSlots: ((try? c.nodes(forXPath: "Color_slots/Color_slot")) ?? []).map { str($0, "Name") })
                    }
                    slots.append(CustomSlot(id: i, name: str(s, "Name"), components: comps))
                }
                slotsByVehicle[str(v, "Name").lowercased()] = slots
            }
        }
        if palettes.isEmpty || slotsByVehicle.isEmpty { return nil }
    }
    /// The slots a model can be customised in; empty for one the shops don't take.
    func slots(for vehicleName: String) -> [CustomSlot] { slotsByVehicle[vehicleName.lowercased()] ?? [] }
    func palette(_ i: Int) -> CustomPalette? { palettes.indices.contains(i) ? palettes[i] : nil }
    func colorName(_ c: CustomColor) -> String {
        guard let p = palette(c.palette) else { return "palette \(c.palette) colour \(c.color)" }
        return "\(p.colors.indices.contains(c.color) ? p.colors[c.color] : "colour \(c.color)") (\(p.name))"
    }
}

extension GarageVehicle {
    private static let customAt = 0x10, customLengthAt = 0x154, customEnd = 0x150
    private var customBytes: [UInt8] {
        let n = Int(UInt32(raw[Self.customLengthAt]) << 24 | UInt32(raw[Self.customLengthAt + 1]) << 16 | UInt32(raw[Self.customLengthAt + 2]) << 8 | UInt32(raw[Self.customLengthAt + 3])) + 1
        return Array(raw[Self.customAt..<min(Self.customAt + n, Self.customEnd)])
    }
    /// The variant section and everything before the entries.
    private var customHead: [UInt8] {
        let b = customBytes
        guard b.count >= 6 else { return [0, 0x0A, 0x55, 0xB0, 0x0B, 0] }
        return Array(b.prefix(6 + Int(b[5])))
    }
    /// The paint and parts chosen, as the game wrote them.
    var customization: [CustomEntry] {
        get {
            let b = customBytes
            guard b.count >= 7, b.count >= 7 + Int(b[5]) else { return [] }
            var i = 6 + Int(b[5]), out: [CustomEntry] = []
            let count = Int(b[i]); i += 1
            for _ in 0..<count {
                guard i + 3 <= b.count else { break }
                var e = CustomEntry(slot: Int(b[i]), component: Int(b[i + 1]), colors: [])
                let n = Int(b[i + 2]); i += 3
                for _ in 0..<n { guard i + 2 <= b.count else { break }; e.colors.append(CustomColor(palette: Int(b[i]), color: Int(b[i + 1]))); i += 2 }
                out.append(e)
            }
            return out
        }
        set {
            var b = customHead + [UInt8(min(newValue.count, 255))]
            for e in newValue.prefix(255) {
                b += [UInt8(e.slot), UInt8(e.component), UInt8(e.colors.count)]
                for c in e.colors { b += [UInt8(c.palette), UInt8(c.color)] }
            }
            guard Self.customAt + b.count <= Self.customEnd else { return }
            raw.replaceSubrange(Self.customAt..<Self.customEnd, with: b + [UInt8](repeating: 0, count: Self.customEnd - Self.customAt - b.count))
            let len = UInt32(b.count - 1)
            raw[Self.customLengthAt] = UInt8(len >> 24); raw[Self.customLengthAt + 1] = UInt8((len >> 16) & 255)
            raw[Self.customLengthAt + 2] = UInt8((len >> 8) & 255); raw[Self.customLengthAt + 3] = UInt8(len & 255)
        }
    }
    func entry(slot: Int) -> CustomEntry? { customization.first { $0.slot == slot } }
    /// Puts a part in a slot (colours empty), or a colour in a colour slot (one pair per sub-slot).
    mutating func setCustom(slot: Int, component: Int, colors: [CustomColor]) {
        var list = customization
        let e = CustomEntry(slot: slot, component: component, colors: colors)
        if let i = list.firstIndex(where: { $0.slot == slot }) { list[i] = e } else { list.append(e) }   // keep the game's order
        customization = list
    }
    /// Back to the car's stock choice for that slot.
    mutating func clearCustom(slot: Int) { customization = customization.filter { $0.slot != slot } }
}

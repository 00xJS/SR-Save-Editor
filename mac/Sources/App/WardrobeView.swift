// The wardrobe: body slots on the left, what can go in the chosen slot on the right.
// Used for the story save and for the online character.
import SwiftUI

struct WardrobeView: View {
    @Binding var outfit: [WornItem]
    let tables: WardrobeTables
    /// Online: only clothing sold in multiplayer can be put on. nil = everything in the game files.
    var allowed: Set<ClothingPair>? = nil
    let changed: () -> Void
    @State private var slot: Int = 12
    @State private var search = ""

    private struct Row: Identifiable, Hashable {
        let item: Int, variant: Int, title: String, style: String, category: String
        var id: String { "\(item)/\(variant)" }
        var pair: ClothingPair { ClothingPair(item: item, variant: variant) }
    }

    private var rows: [Row] {
        var out: [Row] = []
        for item in tables.choices(slot: slot) {
            let variants = item.variants.isEmpty ? [WardrobeVariant(id: 0, title: "")] : item.variants
            for v in variants {
                if let allowed, !allowed.contains(ClothingPair(item: item.id, variant: v.id)) { continue }
                out.append(Row(item: item.id, variant: v.id, title: item.title, style: v.title, category: item.category))
            }
        }
        guard !search.isEmpty else { return out }
        return out.filter { ($0.title + " " + $0.style + " " + $0.category).localizedCaseInsensitiveContains(search) }
    }

    private var others: Int { outfit.filter { WardrobeTables.slot($0.slot) == nil }.count }

    var body: some View {
        HStack(spacing: 0) {
            List(WardrobeTables.slots, selection: Binding(get: { Optional(slot) }, set: { if let s = $0 { slot = s } })) { s in
                let w = outfit.worn(in: s.id)
                VStack(alignment: .leading, spacing: 1) {
                    Text(s.title).font(.callout)
                    Text(w.map { "\(tables.title($0))\(tables.style($0).isEmpty ? "" : " · \(tables.style($0))")" } ?? "—")
                        .font(.caption).foregroundColor(.secondary).lineLimit(1)
                }.tag(s.id)
            }.frame(width: 250)
            Divider()
            VStack(alignment: .leading, spacing: 0) {
                let current = outfit.worn(in: slot)
                HStack {
                    Text(WardrobeTables.slot(slot)?.title ?? "Slot \(slot)").font(.headline)
                    if let w = current { Text("\(tables.title(w)) \(tables.style(w))").foregroundColor(.secondary) } else { Text("nothing").foregroundColor(.secondary) }
                    Spacer()
                    if let w = current, let item = tables.item(w.item), item.options(in: slot).count > 1 {
                        Picker("Worn", selection: Binding(get: { w.mesh }, set: { m in outfit.put(item: w.item, variant: w.variant, mesh: m, slot: slot); changed() })) {
                            ForEach(item.options(in: slot)) { Text($0.title).tag($0.id) }
                        }.frame(maxWidth: 260)
                    }
                    Button("Take off") { outfit.takeOff(slot: slot); changed() }.disabled(current == nil)
                }.padding(10)
                HStack {
                    TextField("Search", text: $search).textFieldStyle(.roundedBorder).frame(width: 220)
                    Spacer()
                    if others > 0 { Text("\(others) worn in slots this editor can't change yet").font(.caption).foregroundColor(.secondary) }
                }.padding(.horizontal, 10).padding(.bottom, 6)
                List(rows) { r in
                    let on = current?.item == r.item && current?.variant == r.variant
                    Button {
                        guard let item = tables.item(r.item) else { return }
                        // Keep the way it is worn when only the colour changes.
                        let mesh = (current?.item == r.item ? current?.mesh : nil) ?? item.options(in: slot).first?.id
                        guard let mesh else { return }
                        outfit.put(item: r.item, variant: r.variant, mesh: mesh, slot: slot); changed()
                    } label: {
                        HStack {
                            Image(systemName: on ? "checkmark" : "").frame(width: 14)
                            Text(r.title); Text(r.style).foregroundColor(.secondary); Spacer(); Text(r.category).foregroundColor(.secondary)
                        }.contentShape(Rectangle())
                    }.buttonStyle(.plain)
                }
            }
        }
    }
}

/// A tickable list of clothing: the owned list of the story save or of the online character.
struct OwnedListView: View {
    let choices: [ClothingChoice]
    @Binding var owned: Set<ClothingPair>
    /// Worn right now: owned whatever the tick says.
    let locked: Set<ClothingPair>
    let note: String
    let changed: () -> Void
    @State private var search = ""
    @State private var category = "All"
    @State private var ownedOnly = false

    var body: some View {
        let categories = ["All"] + Array(Set(choices.map(\.category))).sorted()
        let shown = choices.filter { c in
            (category == "All" || c.category == category) && (!ownedOnly || owned.contains(c.pair) || locked.contains(c.pair))
                && (search.isEmpty || (c.title + " " + c.style + " " + c.category).localizedCaseInsensitiveContains(search))
        }
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Owns \(choices.filter { owned.contains($0.pair) || locked.contains($0.pair) }.count) of \(choices.count). \(note)").foregroundColor(.secondary)
                Spacer()
                Toggle("Owned only", isOn: $ownedOnly).toggleStyle(.checkbox)
                Picker("", selection: $category) { ForEach(categories, id: \.self) { Text($0).tag($0) } }.labelsHidden().frame(width: 150)
                TextField("Search", text: $search).textFieldStyle(.roundedBorder).frame(width: 160)
                Button("Own all shown") { owned.formUnion(shown.map(\.pair)); changed() }
                Button("Own none shown") { owned.subtract(shown.map(\.pair)); changed() }
            }.padding(.horizontal, 10).padding(.bottom, 8)
            List(shown) { item in
                Toggle(isOn: Binding(get: { owned.contains(item.pair) || locked.contains(item.pair) }, set: { on in
                    if on { owned.insert(item.pair) } else { owned.remove(item.pair) }
                    changed()
                })) {
                    HStack { Text(item.title); Text(item.style).foregroundColor(.secondary); Spacer(); Text(locked.contains(item.pair) ? "wearing" : item.category).foregroundColor(.secondary) }
                }.disabled(locked.contains(item.pair))
            }
        }
    }
}

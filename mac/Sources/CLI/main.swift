// Command-line front end, used for testing the save code.
import Foundation

let args = CommandLine.arguments
guard args.count >= 3 else {
    print("""
    usage: sredit info <game.dat> | mp <3_MPStorage> [cash=N wear:...] | backups <file> | restore <file> <backup>
           sredit set <game.dat> cash=N respect=N hood:<name|all>=<0-3> contact:<name|all>=<0|1>
                  weapon:<name>=<magazine>/<spare>|none food=<name,name,...> stash:<name|all>=<0|1>
                  wear:<item id>=<variant id>/<mesh id>   wear:<slot number>=none
    """)
    exit(2)
}
do {
    let file = URL(fileURLWithPath: args[2])
    if args[1] == "icon", args.count >= 5 {
        // sredit icon <any file in dist> <item> <out.rgba>: writes the raw RGBA of an icon, for testing.
        guard let t = GameTables.find(near: file).flatMap({ try? GameTables(packfile: $0) }), let img = t.icons?.icon(for: args[3]) else { print("no icon"); exit(1) }
        try Data(img.pixels).write(to: URL(fileURLWithPath: args[4]))
        print("\(img.width)x\(img.height)")
        exit(0)
    }
    if args[1] == "restore", args.count >= 4 {
        // sredit restore <file> <backup>: puts a backup back, keeping the current file as a backup.
        print("kept", try Backups.restore(URL(fileURLWithPath: args[3]), to: file).lastPathComponent)
        exit(0)
    }
    if args[1] == "backups" {
        for b in Backups.list(for: file) { print(b.lastPathComponent) }
        exit(0)
    }
    // wear:<item>=<variant>/<mesh> puts an item on (the slot follows from the mesh); wear:<slot>=none takes one off.
    func dress(_ outfit: inout [WornItem], _ key: String, _ value: String, _ wardrobe: WardrobeTables?) {
        guard key.hasPrefix("wear:"), let n = Int(key.dropFirst(5)) else { return }
        if value == "none" { outfit.takeOff(slot: n); return }
        let ids = value.split(separator: "/").compactMap { Int($0) }
        guard ids.count == 2, let slot = wardrobe?.slot(ofMesh: ids[1]) else { print("wear: unknown mesh or no game files"); return }
        outfit.put(item: n, variant: ids[0], mesh: ids[1], slot: slot)
    }
    func describe(_ outfit: [WornItem], _ wardrobe: WardrobeTables?) -> String {
        outfit.map { w in "\(WardrobeTables.slot(w.slot)?.title ?? "slot \(w.slot)"): \(wardrobe?.title(w) ?? "item \(w.item)") \(wardrobe?.style(w) ?? "\(w.variant)") [\(w.mesh)]" }.joined(separator: "; ")
    }
    if args[1] == "mp" {
        let c = try MpCharacter(url: file)
        let t = GameTables.find(near: file).flatMap({ try? GameTables(packfile: $0) })
        let wardrobe = t.map { WardrobeTables(tables: $0) }
        if args.count > 3 {
            for a in args.dropFirst(3) {
                let kv = a.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
                guard kv.count == 2 else { continue }
                if kv[0] == "cash", let v = Double(kv[1]) { c.cents = UInt32(v * 100) } else { dress(&c.outfit, kv[0], kv[1], wardrobe) }
            }
            let store = Set(t.map { ClothingTables(tables: $0).store.map(\.pair) } ?? [])
            print("saved; previous file kept as", try c.write(store: store).lastPathComponent)
        }
        print(String(format: "cash $%.2f | owned %d | wearing %d", Double(c.cents) / 100, c.owned.count, c.outfit.count))
        if let t {
            let clothing = ClothingTables(tables: t)
            print("store: \(clothing.store.count) styles; first owned:", c.owned.prefix(3).map { let x = clothing.choice($0); return "\(x.title) (\(x.style))" }.joined(separator: ", "))
        }
        print("outfit:", describe(c.outfit, wardrobe))
        exit(0)
    }
    let save = try SinglePlayerSave(url: file)
    let tables = GameTables.find(near: file).flatMap { try? GameTables(packfile: $0) }
    let wardrobe = tables.map { WardrobeTables(tables: $0) }
    if args[1] == "set" {
        for a in args.dropFirst(3) {
            let kv = a.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
            guard kv.count == 2 else { continue }
            if kv[0] == "cash", let v = Double(kv[1]) { save.cash = v }
            else if kv[0] == "respect", let v = UInt32(kv[1]) { save.respect = v }
            else if kv[0] == "food" { save.food = kv[1].split(separator: ",").map(String.init) }
            else if kv[0].hasPrefix("hood:"), let v = UInt32(kv[1]) {
                let n = String(kv[0].dropFirst(5))
                for (i, h) in save.neighbourhoods.enumerated() where n == "all" || h.name == n { save.setOwner(of: i, to: v) }
            } else if kv[0].hasPrefix("contact:") {
                let n = String(kv[0].dropFirst(8))
                for (i, c) in save.contacts.enumerated() where n == "all" || c.name == n { save.setContact(i, unlocked: kv[1] == "1") }
            } else if kv[0].hasPrefix("stash:") {
                let n = String(kv[0].dropFirst(6))
                for (i, s) in save.stash.enumerated() where n == "all" || s.hash == GameTables.hash(n) { save.setStash(i, unlocked: kv[1] == "1") }
            } else if kv[0].hasPrefix("wear:") { dress(&save.outfit, kv[0], kv[1], wardrobe)
            } else if kv[0].hasPrefix("weapon:") {
                let n = String(kv[0].dropFirst(7))
                save.weapons.removeAll { $0.name == n }
                let ammo = kv[1].split(separator: "/").compactMap { UInt32($0) }
                if ammo.count == 2 { save.weapons.append(CarriedWeapon(name: n, clip: ammo[0], reserve: ammo[1])) }
            }
        }
        print("saved; previous file kept as", try save.write().lastPathComponent)
    }
    print(String(format: "cash $%.2f | respect %d (next bar at %d)", save.cash, save.respect, save.respectNeeded))
    print("weapons:", save.weapons.map { "\($0.name) \($0.clip)/\($0.reserve)" }.joined(separator: ", "))
    print("food:", save.food.joined(separator: ", "))
    print("outfit:", describe(save.outfit, wardrobe))
    print("stash:", save.stash.map { "\(tables?.name(hash: $0.hash) ?? String($0.hash, radix: 16))\($0.unlocked ? "*" : "")" }.joined(separator: ", "))
    print("contacts:", save.contacts.map { "\($0.name)\($0.unlocked ? "*" : "")" }.joined(separator: ", "))
    var byGang: [UInt32: Int] = [:]
    for h in save.neighbourhoods { byGang[h.owner, default: 0] += 1 }
    print("neighbourhoods: \(save.neighbourhoods.count) |", byGang.sorted { $0.key < $1.key }.map { "\(SinglePlayerSave.gangs[$0.key] ?? "?\($0.key)"): \($0.value)" }.joined(separator: ", "))
    if let t = tables { print("game files: \(t.weapons.count) weapons, \(t.offered.count) offered, \(t.food.count) food, \(t.cheats.count) cheats") }
} catch { print("error:", error); exit(1) }

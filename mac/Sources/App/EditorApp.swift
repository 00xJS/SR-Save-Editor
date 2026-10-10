// Saints Reborn Save Editor for macOS (unofficial). Contains no game data or artwork.
import SwiftUI
import AppKit

final class Editor: ObservableObject {
    @Published var saves: [URL] = []
    @Published var selected: URL?
    @Published var save: SinglePlayerSave?
    @Published var tables: GameTables?
    @Published var status = "Choose a save."
    @Published var cashText = ""
    @Published var respectText = ""
    @Published var revision = 0
    // Weapon wheel: one weapon per slot (0 is the fists), then the four quick items.
    @Published var slots: [CarriedWeapon?] = Array(repeating: nil, count: 8)
    @Published var quick: [String?] = Array(repeating: nil, count: 4)
    @Published var selectedSlot = 2
    @Published var outfit: [WornItem] = []
    @Published var storyOwned: Set<ClothingPair> = []
    private var extras: [CarriedWeapon] = []
    private var extraFood: [String] = []
    // Online character.
    @Published var players: [(name: String, file: URL)] = []
    @Published var playerName = ""
    @Published var character: MpCharacter?
    @Published var mpCashText = ""
    @Published var ownedClothing: Set<ClothingPair> = []
    @Published var mpOutfit: [WornItem] = []
    private(set) var clothing: ClothingTables?
    private var wardrobeTables: WardrobeTables?
    /// The wardrobe tables are big; they are read the first time a wardrobe screen opens.
    var wardrobe: WardrobeTables? {
        if wardrobeTables == nil, let t = tables { wardrobeTables = WardrobeTables(tables: t) }
        return wardrobeTables
    }
    private var spDirty = false, mpDirty = false

    static var defaultGameFolder: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("SaintsReborn/dist/game") }

    // For testing: -game <folder> opens another game folder at launch.
    init() { openGameFolder(UserDefaults.standard.string(forKey: "game").map { URL(fileURLWithPath: $0) } ?? Self.defaultGameFolder) }

    func openGameFolder(_ folder: URL) {
        var game = folder
        for sub in ["game", "dist/game"] where FileManager.default.fileExists(atPath: folder.appendingPathComponent(sub + "/packfiles").path) {
            game = folder.appendingPathComponent(sub)
        }
        loadTables(near: game)
        var found: [URL] = []
        for user in (try? FileManager.default.contentsOfDirectory(at: game, includingPropertiesForKeys: nil)) ?? [] where user.lastPathComponent.count == 16 {
            let slots = user.appendingPathComponent("545107D1/00000001")
            for slot in (try? FileManager.default.contentsOfDirectory(at: slots, includingPropertiesForKeys: nil)) ?? [] {
                let file = slot.appendingPathComponent("game.dat")
                if FileManager.default.fileExists(atPath: file.path) { found.append(file) }
            }
        }
        saves = found.sorted { $0.path > $1.path }
        if let first = saves.first { open(first) } else { save = nil; status = "No single-player saves found in \(game.path)." }
        players = MpCharacter.players(game: game)
        if let first = players.first { openPlayer(first.name) } else { character = nil }
    }

    private func loadTables(near url: URL) {
        guard let pack = GameTables.find(near: url), tables == nil || true else { return }
        tables = try? GameTables(packfile: pack)
        clothing = tables.map { ClothingTables(tables: $0) }
        wardrobeTables = nil
    }

    func title(of url: URL) -> String {
        // Folder names look like 23@2425788128@2@10_05_26@03_47_57 (date and time at the end).
        let parts = url.deletingLastPathComponent().lastPathComponent.split(separator: "@")
        guard parts.count >= 5 else { return url.deletingLastPathComponent().lastPathComponent }
        return "\(parts[3].replacingOccurrences(of: "_", with: "/")) \(parts[4].replacingOccurrences(of: "_", with: ":"))"
    }

    func open(_ url: URL) {
        do {
            let s = try SinglePlayerSave(url: url)
            if tables == nil { loadTables(near: url) }
            save = s; selected = url; spDirty = false
            cashText = String(format: "%.0f", s.cash); respectText = String(s.respect)
            slots = Array(repeating: nil, count: 8); extras = []
            for w in s.weapons {
                if let slot = tables?.weapon(w.name)?.slot, slot >= 1, slot <= 7, slots[slot] == nil { slots[slot] = w } else { extras.append(w) }
            }
            quick = Array(repeating: nil, count: 4)
            for (i, f) in s.food.prefix(4).enumerated() { quick[i] = f }
            extraFood = Array(s.food.dropFirst(4))
            outfit = s.outfit
            storyOwned = Set(s.owned)
            status = "Loaded the save from \(title(of: url))."
        } catch { save = nil; status = "\(error)" }
        revision += 1
    }

    func openPlayer(_ name: String) {
        guard let p = players.first(where: { $0.name == name }) else { return }
        do {
            let c = try MpCharacter(url: p.file)
            character = c; playerName = name; mpDirty = false
            mpCashText = String(format: "%.0f", Double(c.cents) / 100)
            ownedClothing = Set(c.owned)
            mpOutfit = c.outfit
        } catch { character = nil; status = "\(error)" }
    }

    func touch() { spDirty = true; revision += 1 }
    func touchOnline() { mpDirty = true }
    var canSave: Bool { (save != nil && spDirty) || (character != nil && mpDirty) }

    func setWeapon(slot: Int, to info: WeaponInfo?) {
        slots[slot] = info.map { CarriedWeapon(name: $0.name, clip: UInt32($0.magazine), reserve: UInt32($0.maxRounds)) }
        selectedSlot = slot
        touch()
    }

    static var gameRunning: Bool {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep"); p.arguments = ["-f", "saintsrow"]
        p.standardOutput = Pipe()
        guard (try? p.run()) != nil else { return false }
        p.waitUntilExit()
        return p.terminationStatus == 0
    }

    /// Puts an earlier copy of the story save or the online character back.
    func restore(_ backup: URL, online: Bool) {
        if Self.gameRunning { status = "Saints Reborn is running. Close the game first."; return }
        do {
            if online, let c = character {
                let kept = try Backups.restore(backup, to: c.url); openPlayer(playerName)
                status = "Restored \(backup.lastPathComponent). The character you had is kept as \(kept.lastPathComponent)."
            } else if let s = save {
                let kept = try Backups.restore(backup, to: s.url); open(s.url)
                status = "Restored \(backup.lastPathComponent). The save you had is kept as \(kept.lastPathComponent)."
            }
        } catch { status = "Could not restore: \(error.localizedDescription)" }
    }

    func write() {
        if Self.gameRunning { status = "Saints Reborn is running. Close the game first, then save."; return }
        var notes: [String] = []
        do {
            if let s = save, spDirty {
                if let v = Double(cashText) { s.cash = v }
                if let v = UInt32(respectText) { s.respect = v }
                if tables != nil {
                    s.weapons = slots.compactMap { $0 } + extras
                    s.food = quick.compactMap { $0 } + extraFood
                }
                s.outfit = outfit
                // Clothing that is worn is owned; hair and faces are not clothing.
                var want = storyOwned
                if let w = wardrobe { for x in outfit where w.isClothing(x.item) { want.insert(ClothingPair(item: x.item, variant: x.variant)) } }
                s.owned = s.owned.filter { want.contains($0) } + want.subtracting(s.owned).sorted { ($0.item, $0.variant) < ($1.item, $1.variant) }
                storyOwned = Set(s.owned)
                notes.append("Story save written (previous kept as \(try s.write().lastPathComponent)).")
                spDirty = false
            }
            if let c = character, mpDirty {
                if let v = Double(mpCashText) { c.cents = UInt32(max(0, min(v, 20_000_000)) * 100) }
                c.outfit = mpOutfit
                let store = clothing?.store.map(\.pair) ?? []
                let inStore = Set(store)
                c.owned = c.owned.filter { !inStore.contains($0) } + store.filter { ownedClothing.contains($0) || c.wearing.contains($0) }
                notes.append("Online character written (previous kept as \(try c.write().lastPathComponent)).")
                mpDirty = false
            }
            status = notes.isEmpty ? "Nothing to save." : notes.joined(separator: " ")
        } catch { status = "Could not save: \(error.localizedDescription)" }
        revision += 1
    }
}

// MARK: - Weapon wheel (drawn here; icons come from the player's own game files)

final class IconCache {
    static let shared = IconCache()
    private var images: [String: NSImage?] = [:]
    func image(_ item: String, _ tables: GameTables?) -> NSImage? {
        if let cached = images[item] { return cached }
        var made: NSImage? = nil
        if let img = tables?.icons?.icon(for: item) {
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: img.width, pixelsHigh: img.height, bitsPerSample: 8, samplesPerPixel: 4,
                                       hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: img.width * 4, bitsPerPixel: 32)
            if let rep, let data = rep.bitmapData {
                img.pixels.withUnsafeBufferPointer { data.update(from: $0.baseAddress!, count: img.pixels.count) }
                let image = NSImage(size: NSSize(width: img.width, height: img.height)); image.addRepresentation(rep); made = image
            }
        }
        images[item] = made
        return made
    }
}

struct WheelSlot: View {
    let label: String
    var icon: NSImage? = nil
    let ammo: Double?      // 0...1, nil = no ammo arc
    let selected: Bool
    let filled: Bool
    var size: CGFloat = 86
    var body: some View {
        ZStack {
            if selected { Circle().stroke(Color(red: 0.45, green: 0.72, blue: 1.0), lineWidth: 5).blur(radius: 3).frame(width: size + 12, height: size + 12) }
            Circle().fill(RadialGradient(colors: [Color(red: 0.42, green: 0.47, blue: 0.80), Color(red: 0.30, green: 0.34, blue: 0.66)], center: .center, startRadius: 2, endRadius: size / 2))
            Circle().stroke(Color.black.opacity(0.92), lineWidth: size * 0.08)
            if let ammo {
                Circle().trim(from: 0, to: 0.32 * max(0.04, min(1, ammo)))
                    .stroke(LinearGradient(colors: [Color(red: 1.0, green: 0.42, blue: 0.0), Color(red: 1.0, green: 0.68, blue: 0.1)], startPoint: .bottom, endPoint: .top),
                            style: StrokeStyle(lineWidth: size * 0.07, lineCap: .butt))
                    .rotationEffect(.degrees(100))
            }
            if let icon {
                Image(nsImage: icon).resizable().interpolation(.high).aspectRatio(contentMode: .fit)
                    .frame(width: size * 0.72, height: size * 0.55).shadow(color: .black.opacity(0.6), radius: 2, x: 0, y: 1)
            } else {
                Text(label).font(.system(size: max(9, size * 0.125), weight: .semibold, design: .rounded)).foregroundColor(.white.opacity(filled ? 1 : 0.55))
                    .multilineTextAlignment(.center).lineLimit(3).minimumScaleFactor(0.6).padding(size * 0.14)
                    .shadow(color: .black.opacity(0.7), radius: 1, x: 0, y: 1)
            }
        }
        .frame(width: size, height: size)
        .contentShape(Circle())
    }
}

struct ChoiceList: View {
    let title: String
    let choices: [(id: String, title: String)]
    var tables: GameTables? = nil
    let current: String?
    let pick: (String?) -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.headline).padding(.bottom, 4)
            ForEach(choices, id: \.id) { c in
                Button { pick(c.id) } label: {
                    HStack {
                        Image(systemName: c.id == current ? "checkmark" : "").frame(width: 14)
                        if let img = IconCache.shared.image(c.id, tables) { Image(nsImage: img).resizable().interpolation(.high).aspectRatio(contentMode: .fit).frame(width: 42, height: 32) }
                        Text(c.title); Spacer()
                    }.contentShape(Rectangle())
                }.buttonStyle(.plain).padding(.vertical, 3)
            }
            Divider().padding(.vertical, 3)
            Button { pick(nil) } label: {
                HStack { Image(systemName: current == nil ? "checkmark" : "").frame(width: 14); Text("Empty"); Spacer() }.contentShape(Rectangle())
            }.buttonStyle(.plain).padding(.vertical, 3)
        }.padding(12).frame(minWidth: 210)
    }
}

struct WeaponWheel: View {
    @ObservedObject var editor: Editor
    let tables: GameTables
    @State private var openSlot: Int?
    @State private var openQuick: Int?

    var body: some View {
        GeometryReader { geo in
            let side = min(geo.size.width, geo.size.height)
            let centre = CGPoint(x: side * 0.42, y: side * 0.44)
            let radius = side * 0.30
            ZStack {
                LinearGradient(colors: [Color(red: 0.45, green: 0.50, blue: 0.86), Color(red: 0.58, green: 0.62, blue: 0.90)], startPoint: .top, endPoint: .bottom)
                ForEach(0..<8, id: \.self) { i in
                    let angle = Double(i) * .pi / 4 - .pi / 2
                    slot(i, size: side * 0.165).position(x: centre.x + radius * cos(angle), y: centre.y + radius * sin(angle))
                }
                ZStack {
                    Circle().fill(LinearGradient(colors: [Color(white: 0.55), Color(white: 0.22)], startPoint: .top, endPoint: .bottom))
                    Circle().stroke(Color.black.opacity(0.9), lineWidth: 5)
                    Text("LS").font(.system(size: side * 0.036, weight: .bold, design: .rounded)).foregroundColor(Color(white: 0.9))
                }.frame(width: side * 0.13, height: side * 0.13).position(centre)
                let pad = CGPoint(x: side * 0.81, y: side * 0.81), step = side * 0.115
                ForEach(0..<4, id: \.self) { i in
                    let dx: [CGFloat] = [0, 1, 0, -1], dy: [CGFloat] = [-1, 0, 1, 0]
                    quickSlot(i, size: side * 0.11).position(x: pad.x + dx[i] * step, y: pad.y + dy[i] * step)
                }
                Image(systemName: "dpad.fill").font(.system(size: side * 0.07)).foregroundColor(Color(white: 0.35)).position(pad)
            }
            .frame(width: side, height: side)
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    @ViewBuilder private func slot(_ i: Int, size: CGFloat) -> some View {
        if i == 0 {
            WheelSlot(label: "Fists", icon: IconCache.shared.image("fists", tables), ammo: nil, selected: false, filled: true, size: size)
        } else {
            let w = editor.slots[i]
            let info = w.flatMap { tables.weapon($0.name) }
            let ammo: Double? = w.flatMap { w in (info?.maxRounds ?? 0) > 0 ? Double(w.reserve) / Double(info!.maxRounds) : nil }
            Button { editor.selectedSlot = i; openSlot = i } label: {
                WheelSlot(label: w.map { tables.title($0.name) } ?? GameTables.slotNames[i], icon: w.flatMap { IconCache.shared.image($0.name, tables) },
                          ammo: ammo, selected: editor.selectedSlot == i, filled: w != nil, size: size)
            }
            .buttonStyle(.plain)
            .popover(isPresented: Binding(get: { openSlot == i }, set: { if !$0 { openSlot = nil } })) {
                ChoiceList(title: GameTables.slotNames[i], choices: tables.choices(slot: i).map { ($0.name, $0.title) }, tables: tables, current: w?.name) { name in
                    editor.setWeapon(slot: i, to: name.flatMap { tables.weapon($0) })
                    openSlot = nil
                }
            }
        }
    }

    private func quickSlot(_ i: Int, size: CGFloat) -> some View {
        let item = editor.quick[i]
        return Button { openQuick = i } label: {
            WheelSlot(label: item.map { tables.title($0) } ?? "", icon: item.flatMap { IconCache.shared.image($0, tables) }, ammo: nil, selected: false, filled: item != nil, size: size)
        }
        .buttonStyle(.plain)
        .popover(isPresented: Binding(get: { openQuick == i }, set: { if !$0 { openQuick = nil } })) {
            ChoiceList(title: "Food and drink", choices: tables.food.map { ($0, tables.title($0)) }, tables: tables, current: item) { name in
                editor.quick[i] = name; editor.touch(); openQuick = nil
            }
        }
    }
}

// MARK: - Screens

enum Screen: String, CaseIterable, Identifiable {
    case player = "Player", weapons = "Weapons", wardrobe = "Wardrobe", garage = "Garage", stash = "Crib weapons", hoods = "Neighbourhoods", homies = "Homies", cheats = "Cheat codes", online = "Online character"
    var id: String { rawValue }
    var icon: String {
        switch self {
        case .player: return "person.fill"
        case .weapons: return "scope"
        case .wardrobe: return "tshirt.fill"
        case .garage: return "car.fill"
        case .stash: return "shippingbox.fill"
        case .hoods: return "map.fill"
        case .homies: return "person.2.fill"
        case .cheats: return "phone.fill"
        case .online: return "globe"
        }
    }
    static let story: [Screen] = [.player, .weapons, .wardrobe, .garage, .stash, .hoods, .homies, .cheats]
}

struct ContentView: View {
    @StateObject var editor = Editor()
    // For testing: -screen <name> opens that screen at launch.
    @State var screen: Screen = Screen(rawValue: UserDefaults.standard.string(forKey: "screen") ?? "") ?? .player
    @State var cheatSearch = ""
    @State var clothingSearch = ""
    @State var onlineTab = 0
    @State var wardrobeTab = 0
    @State var selectedCar: Int?

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Picker("Save", selection: Binding(get: { editor.selected }, set: { if let u = $0 { editor.open(u) } })) {
                    ForEach(editor.saves, id: \.self) { Text(editor.title(of: $0)).tag(Optional($0)) }
                }.frame(maxWidth: 320)
                Button("Open…") {
                    let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = true
                    panel.message = "Choose the Saints Reborn dist folder, or a single game.dat"
                    if panel.runModal() == .OK, let u = panel.url {
                        if u.lastPathComponent == "game.dat" { editor.open(u) } else { editor.openGameFolder(u) }
                    }
                }
                Spacer()
                restoreMenu
                Button("Save") { editor.write() }.keyboardShortcut("s").disabled(!editor.canSave)
            }.padding(10)
            Divider()
            HStack(spacing: 0) {
                List(selection: Binding(get: { screen }, set: { screen = $0 ?? .player })) {
                    Section("Story") { ForEach(Screen.story) { s in Label(s.rawValue, systemImage: s.icon).tag(s) } }
                    Section("Online") { Label(Screen.online.rawValue, systemImage: Screen.online.icon).tag(Screen.online) }
                }.listStyle(.sidebar).frame(width: 200)
                Divider()
                Group {
                    switch screen {
                    case .online: onlineScreen
                    case .cheats: cheatsScreen
                    default:
                        if let save = editor.save {
                            switch screen {
                            case .weapons: weaponsScreen
                            case .wardrobe: wardrobeScreen
                            case .garage: garageScreen(save)
                            case .stash: stashScreen(save)
                            case .hoods: hoodsScreen(save)
                            case .homies: homiesScreen(save)
                            default: playerScreen
                            }
                        } else { Text(editor.status).foregroundColor(.secondary).padding() }
                    }
                }.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            Divider()
            HStack { Text(editor.status).font(.callout).foregroundColor(.secondary).lineLimit(2); Spacer() }.padding(8)
        }.frame(minWidth: 900, minHeight: 680)
    }

    /// Restore backup…: the dated copies of whichever file the current screen edits.
    @ViewBuilder var restoreMenu: some View {
        let online = screen == .online
        let file: URL? = online ? editor.character?.url : editor.save?.url
        let backups = file.map { Backups.list(for: $0) } ?? []
        Menu("Restore backup…") {
            if backups.isEmpty { Text("No backups yet") }
            ForEach(backups, id: \.self) { b in
                Button(b.lastPathComponent) { editor.restore(b, online: online) }
            }
        }.frame(maxWidth: 170).disabled(file == nil).id(editor.revision)
    }

    var needsGameFiles: some View {
        Text("The game's files were not found. Use Open… and choose the Saints Reborn dist folder.").foregroundColor(.secondary).padding()
    }

    var playerScreen: some View {
        Form {
            Section("Money") {
                TextField("Cash ($)", text: $editor.cashText).onChange(of: editor.cashText) { _ in editor.touch() }
            }
            Section("Respect") {
                TextField("Respect", text: $editor.respectText).onChange(of: editor.respectText) { _ in editor.touch() }
                if let s = editor.save { Text("Next bar at \(s.respectNeeded).").foregroundColor(.secondary) }
            }
        }.formStyle(.grouped).padding()
    }

    @ViewBuilder var weaponsScreen: some View {
        if let tables = editor.tables {
            VStack(spacing: 8) {
                WeaponWheel(editor: editor, tables: tables).padding(.top, 12).padding(.horizontal, 12)
                ammoRow(tables).padding(.horizontal, 16).padding(.bottom, 10)
            }
        } else { needsGameFiles }
    }

    @ViewBuilder func ammoRow(_ tables: GameTables) -> some View {
        let i = editor.selectedSlot
        if let w = editor.slots[i], let info = tables.weapon(w.name) {
            HStack(spacing: 14) {
                Text("\(GameTables.slotNames[i]): \(info.title)").font(.headline)
                Spacer()
                if info.maxRounds > 0 {
                    Stepper("Loaded \(w.clip) of \(info.magazine)", value: number(i, \.clip), in: 0...UInt32(max(info.magazine, 1)))
                    Stepper("Spare \(w.reserve) of \(info.maxRounds)", value: number(i, \.reserve), in: 0...UInt32(info.maxRounds), step: max(1, info.magazine))
                    Button("Fill") {
                        editor.slots[i]?.clip = UInt32(info.magazine); editor.slots[i]?.reserve = UInt32(info.maxRounds); editor.touch()
                    }
                } else { Text("No ammo").foregroundColor(.secondary) }
            }
        } else {
            Text("Click a slot to choose a weapon. Click a quick slot to choose food.").foregroundColor(.secondary)
        }
    }
    func number(_ slot: Int, _ key: WritableKeyPath<CarriedWeapon, UInt32>) -> Binding<UInt32> {
        Binding(get: { editor.slots[slot]?[keyPath: key] ?? 0 }, set: { editor.slots[slot]?[keyPath: key] = $0; editor.touch() })
    }

    @ViewBuilder var wardrobeScreen: some View {
        if let w = editor.wardrobe {
            VStack(spacing: 0) {
                Picker("", selection: $wardrobeTab) { Text("Wearing").tag(0); Text("Clothing owned").tag(1) }
                    .pickerStyle(.segmented).labelsHidden().frame(width: 260).padding(10)
                if wardrobeTab == 0 {
                    WardrobeView(outfit: $editor.outfit, tables: w) { editor.touch() }
                } else {
                    OwnedListView(choices: w.clothing, owned: $editor.storyOwned, locked: Set(editor.outfit.filter { w.isClothing($0.item) }.map { ClothingPair(item: $0.item, variant: $0.variant) }),
                                  note: "Bought in the story. What is worn stays owned.") { editor.touch() }
                }
            }
        } else { needsGameFiles }
    }

    @ViewBuilder func garageScreen(_ save: SinglePlayerSave) -> some View {
        if let tables = editor.tables {
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text("\(save.vehicles.count) in the garage. Click a car for its paint and parts.").foregroundColor(.secondary)
                    Spacer()
                }.padding(10)
                List(Array(save.vehicles.enumerated()), id: \.offset, selection: $selectedCar) { i, v in
                    HStack {
                        Text("#\(v.id)").foregroundColor(.secondary).frame(width: 40, alignment: .leading)
                        Picker("", selection: Binding(get: { v.hash }, set: { save.vehicles[i].hash = $0; editor.touch() })) {
                            if tables.vehicle(hash: v.hash) == nil { Text(String(format: "Unknown %08X", v.hash)).tag(v.hash) }
                            ForEach(tables.vehicles) { Text("\($0.title)  (\($0.category))").tag($0.hash) }
                        }.labelsHidden().frame(width: 320)
                        Text(tables.vehicle(hash: v.hash)?.name ?? "").foregroundColor(.secondary)
                        Spacer()
                        let max = tables.vehicle(hash: v.hash)?.maxHitpoints ?? 50000
                        if v.damaged(maxHitpoints: max) {
                            Text("\(Int(100 * Double(v.hitpoints) / Double(max)))%" + (v.repairBill > 0 ? String(format: ", $%.0f to fix", v.repairBill) : "")).foregroundColor(.orange)
                            Button("Repair") { save.vehicles[i].repair(maxHitpoints: max); editor.touch() }
                        } else { Text("Mint").foregroundColor(.secondary) }
                        Button("Another") { save.duplicateVehicle(at: i); editor.touch() }
                        Button("Remove") { save.vehicles.remove(at: i); editor.touch() }
                    }.tag(i)
                }.id(editor.revision).frame(maxHeight: 260)
                Divider()
                if let i = selectedCar, save.vehicles.indices.contains(i) { carDetail(save, i, tables) }
                else { Text("Click a car above to change its paint and parts.").foregroundColor(.secondary).padding().frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading) }
            }
        } else { needsGameFiles }
    }

    /// Paint and parts of one car: a row per slot of the model's own table.
    @ViewBuilder func carDetail(_ save: SinglePlayerSave, _ i: Int, _ tables: GameTables) -> some View {
        let v = save.vehicles[i]
        if let custom = tables.vehicleCustom, let model = tables.vehicle(hash: v.hash) {
            let slots = custom.slots(for: model.name)
            if slots.isEmpty {
                Text("The shops don't take a \(model.title).").foregroundColor(.secondary).padding()
            } else {
                Form {
                    ForEach(slots) { slot in
                        let entry = v.entry(slot: slot.id)
                        if slot.isColor {
                            let palettes = custom.palettes
                            let current = entry?.colors.first
                            HStack {
                                Picker(slot.name, selection: Binding(get: { current?.palette ?? -1 }, set: { p in
                                    if p < 0 { save.vehicles[i].clearCustom(slot: slot.id) }
                                    else { save.vehicles[i].setCustom(slot: slot.id, component: 0, colors: Array(repeating: CustomColor(palette: p, color: 0), count: max(1, slot.components[0].colorSlots.count))) }
                                    editor.touch()
                                })) {
                                    Text("Stock").tag(-1)
                                    ForEach(palettes) { Text($0.name).tag($0.id) }
                                }.frame(maxWidth: 360)
                                if let c = current, let p = custom.palette(c.palette) {
                                    Picker("", selection: Binding(get: { c.color }, set: { col in
                                        save.vehicles[i].setCustom(slot: slot.id, component: 0, colors: Array(repeating: CustomColor(palette: c.palette, color: col), count: max(1, slot.components[0].colorSlots.count)))
                                        editor.touch()
                                    })) {
                                        ForEach(Array(p.colors.enumerated()), id: \.offset) { k, name in Text(name).tag(k) }
                                    }.labelsHidden().frame(maxWidth: 260)
                                }
                            }
                        } else {
                            Picker(slot.name, selection: Binding(get: { entry?.component ?? -1 }, set: { c in
                                if c < 0 { save.vehicles[i].clearCustom(slot: slot.id) } else { save.vehicles[i].setCustom(slot: slot.id, component: c, colors: []) }
                                editor.touch()
                            })) {
                                Text("Stock").tag(-1)
                                ForEach(Array(slot.components.enumerated()), id: \.offset) { k, c in Text(c.name).tag(k) }
                            }.frame(maxWidth: 360)
                        }
                    }
                }.formStyle(.grouped)
            }
        } else { Text("The paint and parts tables were not found (misc2.vpp_xbox2).").foregroundColor(.secondary).padding() }
    }

    func stashScreen(_ save: SinglePlayerSave) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("\(save.stash.filter(\.unlocked).count) of \(save.stash.count) weapons in the crib's weapon cache").foregroundColor(.secondary)
                Spacer()
                Button("Unlock all") { for i in save.stash.indices { save.setStash(i, unlocked: true) }; editor.touch() }
            }.padding(10)
            List(Array(save.stash.enumerated()), id: \.offset) { i, w in
                let name = editor.tables?.name(hash: w.hash)
                Toggle(name.map { editor.tables!.title($0) } ?? String(format: "Weapon %08X", w.hash),
                       isOn: Binding(get: { save.stash[i].unlocked }, set: { save.setStash(i, unlocked: $0); editor.touch() }))
            }.id(editor.revision)
        }
    }

    func hoodsScreen(_ save: SinglePlayerSave) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("\(save.neighbourhoods.filter { $0.owner == 0 }.count) of \(save.neighbourhoods.count) held by the Saints").foregroundColor(.secondary)
                Spacer()
                Button("Give all to the Saints") { for i in save.neighbourhoods.indices { save.setOwner(of: i, to: 0) }; editor.touch() }
            }.padding(10)
            List(Array(save.neighbourhoods.enumerated()), id: \.offset) { i, hood in
                HStack {
                    Text(hood.name)
                    Spacer()
                    Picker("", selection: Binding(get: { save.neighbourhoods[i].owner }, set: { save.setOwner(of: i, to: $0); editor.touch() })) {
                        ForEach(SinglePlayerSave.gangs.keys.sorted(), id: \.self) { Text(SinglePlayerSave.gangs[$0]!).tag($0) }
                    }.labelsHidden().frame(width: 200)
                }
            }.id(editor.revision)
        }
    }

    func homiesScreen(_ save: SinglePlayerSave) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Homies you can call on the phone").foregroundColor(.secondary)
                Spacer()
                Button("Unlock all") { for i in save.contacts.indices { save.setContact(i, unlocked: true) }; editor.touch() }
            }.padding(10)
            List(Array(save.contacts.enumerated()), id: \.offset) { i, contact in
                Toggle(editor.tables?.homieTitles[contact.name] ?? contact.name.replacingOccurrences(of: "_", with: " ").capitalized,
                       isOn: Binding(get: { save.contacts[i].unlocked }, set: { save.setContact(i, unlocked: $0); editor.touch() }))
            }.id(editor.revision)
        }
    }

    @ViewBuilder var cheatsScreen: some View {
        if let tables = editor.tables {
            let shown = tables.cheats.filter { cheatSearch.isEmpty || $0.name.localizedCaseInsensitiveContains(cheatSearch) || $0.category.localizedCaseInsensitiveContains(cheatSearch) }
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text("Dial these on the phone in the game. Nothing here changes your save.").foregroundColor(.secondary)
                    Spacer()
                    TextField("Search", text: $cheatSearch).textFieldStyle(.roundedBorder).frame(width: 180)
                }.padding(10)
                Table(shown) {
                    TableColumn("Cheat", value: \.name)
                    TableColumn("Number", value: \.number).width(110)
                    TableColumn("Kind", value: \.category).width(160)
                }
            }
        } else { needsGameFiles }
    }

    @ViewBuilder var onlineScreen: some View {
        if let c = editor.character {
            let store = editor.clothing?.store ?? []
            let wearingNow = Set(editor.mpOutfit.map { ClothingPair(item: $0.item, variant: $0.variant) })
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Picker("Player", selection: Binding(get: { editor.playerName }, set: { editor.openPlayer($0) })) {
                        ForEach(editor.players, id: \.name) { Text($0.name).tag($0.name) }
                    }.frame(maxWidth: 240)
                    TextField("Cash ($)", text: $editor.mpCashText).textFieldStyle(.roundedBorder).frame(width: 150)
                        .onChange(of: editor.mpCashText) { _ in editor.touchOnline() }
                    Spacer()
                }.padding(10)
                Picker("", selection: $onlineTab) { Text("Clothing owned").tag(0); Text("Wearing").tag(1) }.pickerStyle(.segmented).labelsHidden().frame(width: 260).padding(.horizontal, 10).padding(.bottom, 8)
                if onlineTab == 1 {
                    if let w = editor.wardrobe {
                        WardrobeView(outfit: $editor.mpOutfit, tables: w, allowed: Set(store.map(\.pair))) { editor.touchOnline() }
                    } else { needsGameFiles }
                } else {
                    OwnedListView(choices: store, owned: $editor.ownedClothing, locked: wearingNow, note: "Sold in multiplayer. What is worn stays owned.") { editor.touchOnline() }
                }
            }
        } else {
            Text("No online character found. In the game, open MULTIPLAYER, change something in the wardrobe and save, then use Open… here.").foregroundColor(.secondary).padding()
        }
    }
}

@main
struct EditorApp: App {
    var body: some Scene {
        WindowGroup("Saints Reborn Save Editor") { ContentView() }.windowResizability(.contentMinSize)
    }
}

// The game's own HUD icons, read from the player's files: bitmap_sheets.xtbl
// (misc.vpp_xbox2) says where each icon sits on a sheet, and the sheets are
// textures in interface-backend.peg_xbox2 (pegfiles.vpp_xbox2).
//
// The texture decoding (Xenos tiling, DXT3/DXT5) is adapted from
// tools/glyphgen in Saints Reborn by Whompay (Apache License 2.0; see
// THIRD_PARTY_NOTICES.md).
import Foundation

struct RgbaImage { let width, height: Int; let pixels: [UInt8] }

final class GameIcons {
    private struct Sprite { let sheet: String; let x, y, width, height: Int }
    private struct Texture { let offset, width, height, format: Int }
    private var sprites: [String: Sprite] = [:]
    private var textures: [String: Texture] = [:]
    private var peg: [UInt8] = []
    private var decoded: [String: RgbaImage] = [:]
    private var bitmapOf: [String: String] = [:]   // item name (lower case) -> icon name

    init?(tables: GameTables, packfiles: URL) {
        guard let sheetsData = tables.pack.read("bitmap_sheets.xtbl"), let text = String(data: sheetsData, encoding: .isoLatin1),
              let pegPack = try? Packfile(url: packfiles.appendingPathComponent("pegfiles.vpp_xbox2")),
              let pegData = pegPack.read("interface-backend.peg_xbox2") else { return nil }
        for sheet in text.components(separatedBy: "<BitmapSheets>").dropFirst() {
            guard let name = tag(sheet, "Name") else { continue }
            for image in sheet.components(separatedBy: "<Properties>").dropFirst() {
                guard let n = tag(image, "Name"), let x = Int(tag(image, "StartX") ?? ""), let y = Int(tag(image, "StartY") ?? ""),
                      let w = Int(tag(image, "ImageWidth") ?? ""), let h = Int(tag(image, "ImageHeight") ?? "") else { continue }
                sprites[n] = Sprite(sheet: name, x: x, y: y, width: w, height: h)
            }
        }
        peg = [UInt8](pegData)
        // A peg file: u16 texture count at 0x10, then 0x48-byte entries from 0x18:
        // u32 data offset, u16 width, u16 height, u16 format, ..., name at +0x16.
        guard peg.count >= 0x18 else { return nil }
        let count = Int(peg[0x10]) << 8 | Int(peg[0x11])
        for j in 0..<count {
            let e = 0x18 + j * 0x48
            guard e + 0x48 <= peg.count else { break }
            let offset = Int(peg[e]) << 24 | Int(peg[e + 1]) << 16 | Int(peg[e + 2]) << 8 | Int(peg[e + 3])
            let width = Int(peg[e + 4]) << 8 | Int(peg[e + 5]), height = Int(peg[e + 6]) << 8 | Int(peg[e + 7])
            let format = Int(peg[e + 8]) << 8 | Int(peg[e + 9])
            var nameBytes = Array(peg[e + 0x16..<e + 0x46])
            if let z = nameBytes.firstIndex(of: 0) { nameBytes.removeSubrange(z...) }
            nameBytes.removeAll { $0 < 32 }
            let name = String(decoding: nameBytes, as: UTF8.self).replacingOccurrences(of: ".tga", with: "")
            textures[name] = Texture(offset: offset, width: width, height: height, format: format)
        }
        for (file, element) in [("items_inventory.xtbl", "Inventory_Item"), ("drugs.xtbl", "Drug")] {
            guard let d = tables.pack.read(file), let t = String(data: d, encoding: .isoLatin1) else { continue }
            for block in t.components(separatedBy: "<\(element)>").dropFirst() {
                if let n = tag(block, "Name"), let b = tag(block, "Bitmap") { bitmapOf[n.lowercased()] = b }
            }
        }
        bitmapOf["the_fist"] = bitmapOf["the_fist"] ?? "ui_hud_inv_burger"
    }

    private func tag(_ block: String, _ name: String) -> String? {
        guard let a = block.range(of: "<\(name)>"), let b = block.range(of: "</\(name)>", range: a.upperBound..<block.endIndex) else { return nil }
        let v = block[a.upperBound..<b.lowerBound].trimmingCharacters(in: .whitespacesAndNewlines)
        return v.isEmpty || v.contains("<") ? nil : v
    }

    /// The icon for a weapon or food item, by the game's item name; "fists" for the hands.
    func icon(for item: String) -> RgbaImage? {
        let key = item.lowercased() == "fists" ? "fist" : (bitmapOf[item.lowercased()] ?? item.lowercased())
        guard let s = sprites[key], let sheet = decodeSheet(s.sheet) else { return nil }
        var out = [UInt8](repeating: 0, count: s.width * s.height * 4)
        for y in 0..<s.height where s.y + y < sheet.height {
            let src = ((s.y + y) * sheet.width + s.x) * 4
            let n = min(s.width, sheet.width - s.x) * 4
            guard n > 0 else { break }
            out.replaceSubrange(y * s.width * 4..<y * s.width * 4 + n, with: sheet.pixels[src..<src + n])
        }
        return RgbaImage(width: s.width, height: s.height, pixels: out)
    }

    private func decodeSheet(_ name: String) -> RgbaImage? {
        if let d = decoded[name] { return d }
        guard let t = textures[name], t.format == 0x191 || t.format == 0x192, t.offset < peg.count else { return nil }
        let image = Self.decode(Array(peg[t.offset...]), width: t.width, height: t.height, format: t.format)
        decoded[name] = image
        return image
    }

    // ---- Xenos tiling and DXT (from glyphgen) ----
    private static func tiled(_ x: Int, _ y: Int, _ w: Int, _ lb: Int) -> Int {
        let aw = (w + 31) & ~31
        let macro = ((x >> 5) + (y >> 5) * (aw >> 5)) << (lb + 7)
        let micro = ((x & 7) + ((y & 6) << 2)) << lb
        let off = macro + ((micro & ~15) << 1) + (micro & 15) + ((y & 8) << (3 + lb)) + ((y & 1) << 4)
        return (((off & ~511) << 3) + ((off & 448) << 2) + (off & 63) + ((y & 16) << 7) + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6)) >> lb
    }
    private static func c565(_ c: Int) -> [Int] { [((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31] }

    static func decode(_ data: [UInt8], width w: Int, height h: Int, format: Int) -> RgbaImage? {
        var px = [UInt8](repeating: 0, count: w * h * 4)
        let bw = max(1, w / 4), bh = max(1, h / 4)
        for by in 0..<bh {
            for bx in 0..<bw {
                let o = tiled(bx, by, bw, 4) * 16
                guard o + 16 <= data.count else { return nil }
                var b = [UInt8](repeating: 0, count: 16)
                for i in stride(from: 0, to: 16, by: 2) { b[i] = data[o + i + 1]; b[i + 1] = data[o + i] }   // byte-swapped u16s
                let c0 = Int(b[8]) | Int(b[9]) << 8, c1 = Int(b[10]) | Int(b[11]) << 8
                let idx = UInt32(b[12]) | UInt32(b[13]) << 8 | UInt32(b[14]) << 16 | UInt32(b[15]) << 24
                var p = [c565(c0), c565(c1), [0, 0, 0], [0, 0, 0]]
                for k in 0..<3 { p[2][k] = (2 * p[0][k] + p[1][k]) / 3; p[3][k] = (p[0][k] + 2 * p[1][k]) / 3 }
                var al = [Int](repeating: 255, count: 16)
                if format == 0x191 {
                    var a: UInt64 = 0
                    for i in 0..<8 { a |= UInt64(b[i]) << (8 * UInt64(i)) }
                    for i in 0..<16 { al[i] = Int((a >> (4 * UInt64(i))) & 15) * 17 }
                } else {
                    let a0 = Int(b[0]), a1 = Int(b[1])
                    var bits: UInt64 = 0
                    for i in 0..<6 { bits |= UInt64(b[2 + i]) << (8 * UInt64(i)) }
                    var pal = [a0, a1, 0, 0, 0, 0, 0, 0]
                    if a0 > a1 { for i in 0..<6 { pal[2 + i] = ((6 - i) * a0 + (i + 1) * a1) / 7 } }
                    else { for i in 0..<4 { pal[2 + i] = ((4 - i) * a0 + (i + 1) * a1) / 5 }; pal[6] = 0; pal[7] = 255 }
                    for i in 0..<16 { al[i] = pal[Int((bits >> (3 * UInt64(i))) & 7)] }
                }
                for i in 0..<16 {
                    let x = bx * 4 + i % 4, y = by * 4 + i / 4
                    guard x < w, y < h else { continue }
                    let c = p[Int((idx >> (2 * UInt32(i))) & 3)], q = (y * w + x) * 4
                    px[q] = UInt8(c[0]); px[q + 1] = UInt8(c[1]); px[q + 2] = UInt8(c[2]); px[q + 3] = UInt8(al[i])
                }
            }
        }
        return RgbaImage(width: w, height: h, pixels: px)
    }
}

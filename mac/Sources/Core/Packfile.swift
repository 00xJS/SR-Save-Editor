// Saints Row (Xbox 360) packfile, version 3. Sections are aligned to 2048 bytes;
// flags at 0x14C: bit 0 compressed, bit 1 condensed (one zlib stream for all files).
import Foundation
import Compression

final class Packfile {
    private struct Entry { let name: String; let at, size, storedSize: Int }
    private let data: [UInt8]
    private let compressed: Bool, condensed: Bool
    private var dataAt = 0
    private var blob: [UInt8] = []
    private var entries: [Entry] = []

    private static func up(_ x: Int) -> Int { (x + 2047) & ~2047 }
    private func u32(_ o: Int) -> Int { Int(data[o]) << 24 | Int(data[o + 1]) << 16 | Int(data[o + 2]) << 8 | Int(data[o + 3]) }

    init(url: URL) throws {
        data = [UInt8](try Data(contentsOf: url))
        compressed = data.count > 0x150 && data[0x14F] & 1 != 0
        condensed = data.count > 0x150 && data[0x14F] & 2 != 0
        guard data.count >= 0x800, u32(0) == 0x51890ACE, u32(4) == 3 else {
            throw SaveError(description: "\(url.lastPathComponent) is not a Saints Row packfile.")
        }
        let count = u32(0x154)
        let namesAt = Self.up(0x800 + u32(0x15C))
        dataAt = Self.up(namesAt + u32(0x160))
        var stored = dataAt
        for i in 0..<count {
            let e = 0x800 + i * 28
            guard e + 28 <= data.count else { break }
            let nameAt = namesAt + u32(e)
            var end = nameAt
            while end < data.count, data[end] != 0 { end += 1 }
            let name = String(decoding: data[min(nameAt, data.count)..<end], as: UTF8.self)
            let size = u32(e + 16), storedSize = u32(e + 20)
            var at = u32(e + 8)
            if compressed && !condensed { at = stored; stored = Self.up(stored + storedSize) }
            entries.append(Entry(name: name, at: at, size: size, storedSize: storedSize))
        }
        if condensed {
            let blobSize = u32(0x164)
            blob = compressed ? inflate(at: dataAt, storedSize: u32(0x168), size: blobSize) : slice(data, dataAt, blobSize)
        }
    }

    private func slice(_ src: [UInt8], _ at: Int, _ size: Int) -> [UInt8] {
        guard at >= 0, size >= 0, at + size <= src.count else { return [] }
        return Array(src[at..<at + size])
    }

    // zlib stream: 2-byte header, then deflate data. The streams in these packfiles
    // are not always closed, so decode as a stream and keep what comes out.
    private func inflate(at: Int, storedSize: Int, size: Int) -> [UInt8] {
        guard size > 0, storedSize > 2, at + storedSize <= data.count else { return [] }
        var out = [UInt8](repeating: 0, count: size)
        var written = 0
        let stream = UnsafeMutablePointer<compression_stream>.allocate(capacity: 1)
        defer { stream.deallocate() }
        guard compression_stream_init(stream, COMPRESSION_STREAM_DECODE, COMPRESSION_ZLIB) == COMPRESSION_STATUS_OK else { return [] }
        defer { compression_stream_destroy(stream) }
        data.withUnsafeBufferPointer { src in
            out.withUnsafeMutableBufferPointer { dst in
                stream.pointee.src_ptr = src.baseAddress! + at + 2
                stream.pointee.src_size = storedSize - 2
                stream.pointee.dst_ptr = dst.baseAddress!
                stream.pointee.dst_size = size
                while stream.pointee.dst_size > 0 {
                    let before = stream.pointee.dst_size
                    let status = compression_stream_process(stream, 0)
                    if status != COMPRESSION_STATUS_OK || stream.pointee.dst_size == before { break }
                }
                written = size - stream.pointee.dst_size
            }
        }
        if written < size { out.removeSubrange(written...) }
        return out
    }

    func read(_ name: String) -> Data? {
        guard let e = entries.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) else { return nil }
        if condensed { return Data(slice(blob, e.at, e.size)) }
        if compressed { return Data(inflate(at: e.at, storedSize: e.storedSize, size: e.size)) }
        return Data(slice(data, dataAt + e.at, e.size))
    }
}

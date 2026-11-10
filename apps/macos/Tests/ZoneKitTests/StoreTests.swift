import Foundation
import Testing

@testable import ZoneKit

struct StoreTests {
  @Test func testPersistenceOwnershipAndTombstones() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let a = try Store(path: root.appendingPathComponent("a"), name: "Mac")
    let b = try Store(path: root.appendingPathComponent("b"), name: "PC")
    let device = await a.identity()
    try await b.pair(device: device, key: Codec.randomKey(), hosts: ["127.0.0.1"], port: 1234)
    let id = try await a.add(text: "one\n世界")
    let firstValue = try await a.nextChange(after: 0)
    let first = try #require(firstValue)
    try await b.apply(owner: device.id, identity: device, item: first)
    do {
      try await b.delete(id)
      Issue.record("Cannot delete a remote note")
    } catch {}
    do {
      try await b.apply(owner: device.id, identity: device, item: first)
      Issue.record("Cannot replay old revisions")
    } catch {}
    try await a.delete(id)
    let deletedValue = try await a.nextChange(after: first.revision)
    let deletion = try #require(deletedValue)
    #expect(deletion.deleted)
    try await b.apply(owner: device.id, identity: device, item: deletion)
    let state = try await b.snapshot()
    #expect(state.items.isEmpty)
    let local = try await a.add(text: "saved locally")
    try await a.rename("Work Mac")
    await a.close()
    let restored = try Store(path: root.appendingPathComponent("a"), name: "Ignored")
    let saved = try await restored.snapshot()
    #expect(saved.device.id == device.id)
    #expect(saved.device.name == "Work Mac")
    #expect(saved.items.first?.id == local)
    await restored.close()
    await b.close()
  }

  @Test func testExclusiveOpenBoundsAndAttachmentBytes() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let store = try Store(path: root, name: "Mac")
    do {
      _ = try Store(path: root, name: "Duplicate")
      Issue.record("Duplicate open must fail")
    } catch { #expect(error.localizedDescription.contains("another instance")) }
    let data = Data([0, 1, 254, 255])
    let id = try await store.add(
      text: "", name: "test.bin", mime: "application/octet-stream", data: data)
    let device = await store.identity()
    let file = try await store.item(owner: device.id, id: id)
    #expect(Data(base64Encoded: file.data) == data)
    do {
      _ = try await store.add(text: "", data: Data(count: 8 * 1024 * 1024 + 1))
      Issue.record("Oversized file must fail")
    } catch {}
    do {
      _ = try await store.add(text: "")
      Issue.record("Empty note must fail")
    } catch {}
    await store.close()
  }
}

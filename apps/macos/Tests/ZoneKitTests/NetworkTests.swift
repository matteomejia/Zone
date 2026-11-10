import CryptoKit
import Foundation
import Testing

@testable import ZoneKit

struct NetworkTests {
  @Test func testWindowsCodecFixture() throws {
    struct Fixture: Decodable {
      let key: String
      let plaintext: String
      let envelope: Envelope
    }
    let url = try #require(Bundle.module.url(forResource: "codec", withExtension: "json"))
    let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url))
    let expected = try JSONDecoder().decode(Request.self, from: Data(fixture.plaintext.utf8))
    let actual = try Codec.open(fixture.envelope, key: fixture.key, as: Request.self)
    #expect(actual.id == expected.id)
    #expect(actual.action == expected.action)
    #expect(actual.since == expected.since)
  }

  @Test func testWindowsPairingFixture() throws {
    let url = try #require(Bundle.module.url(forResource: "pairing", withExtension: "json"))
    let fixture = try JSONDecoder().decode([String: String].self, from: Data(contentsOf: url))
    func bytes(_ key: String) throws -> Data {
      try #require(Data(base64Encoded: try #require(fixture[key])))
    }
    let joiner = try P256.KeyAgreement.PrivateKey(rawRepresentation: bytes("joinerPrivate"))
    let host = try P256.KeyAgreement.PrivateKey(rawRepresentation: bytes("hostPrivate"))
    let joinerPublic = try bytes("joinerPublic")
    let hostPublic = try bytes("hostPublic")
    #expect(joiner.publicKey.x963Representation == joinerPublic)
    #expect(host.publicKey.x963Representation == hostPublic)
    #expect(Codec.commitment(joinerPublic) == fixture["commit"])
    // Each side derives the same key and code from its own private key.
    for (secret, remote) in [(joiner, hostPublic), (host, joinerPublic)] {
      let secrets = try Codec.joinSecrets(
        secret, remote: remote, joiner: joinerPublic, host: hostPublic)
      #expect(secrets.key == fixture["key"])
      #expect(secrets.code == fixture["code"])
    }
    let group = try #require(fixture["group"])
    let a = try #require(fixture["deviceA"])
    let b = try #require(fixture["deviceB"])
    #expect(try Codec.pairKey(group: group, a, b) == fixture["pairKey"])
    #expect(try Codec.pairKey(group: group, b, a) == fixture["pairKey"])
  }

  private func localInvitation(_ engine: ZoneEngine) async throws -> String {
    var value = try Invitation.parse(await engine.createInvitation())
    value.hosts = ["127.0.0.1"]
    return try value.code()
  }

  private func waitFor(_ engine: ZoneEngine, _ predicate: @Sendable (Snapshot) -> Bool) async throws
  {
    let end = Date().addingTimeInterval(20)
    while Date() < end {
      if try await predicate(engine.snapshot()) { return }
      try await Task.sleep(for: .milliseconds(50))
    }
    throw CoreError(message: "Peers did not converge before the deadline.")
  }

  @Test func testEncryptedTCPPairingFilesAndOfflineCatchup() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let a = ZoneEngine()
    let b = ZoneEngine()
    try await a.open(path: root.appendingPathComponent("a"), name: "MacBook", discover: false)
    try await b.open(path: root.appendingPathComponent("b"), name: "Desktop", discover: false)
    let code = try await localInvitation(a)
    try await b.pair(code)
    do {
      try await b.pair(code)
      Issue.record("Invitation must be single-use")
    } catch {}
    let note = try await a.add(text: "from the Mac")
    // Maximum-size, slash-heavy base64 also checks JSON framing overhead.
    let fileData = Data(repeating: 255, count: 8 * 1024 * 1024)
    let file = try await b.add(
      text: "", name: "test.bin", mime: "application/octet-stream", data: fileData)
    try await waitFor(b) { $0.items.contains { $0.id == note } }
    try await waitFor(a) { $0.items.contains { $0.id == file } }
    let bID = try await b.snapshot().device.id
    let received = try await a.attachment(owner: bID, id: file)
    #expect(received == fileData)
    await b.close()
    try await a.delete(note)
    let whileAway = try await a.add(text: "while you were away")
    let resumed = ZoneEngine()
    try await resumed.open(path: root.appendingPathComponent("b"), name: "Ignored", discover: false)
    let cache = try await resumed.snapshot()
    #expect(cache.device.id == bID)
    #expect(cache.items.contains { $0.id == note })
    try await waitFor(resumed) { state in
      !state.items.contains { $0.id == note } && state.items.contains { $0.id == whileAway }
    }
    await a.close()
    await resumed.close()
  }

  @Test func testInvalidAndCancelledInvitationsCannotPair() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let a = ZoneEngine()
    let b = ZoneEngine()
    try await a.open(path: root.appendingPathComponent("a"), name: "A", discover: false)
    try await b.open(path: root.appendingPathComponent("b"), name: "B", discover: false)
    let code = try await localInvitation(a)
    var wrong = try Invitation.parse(code)
    wrong.key = Codec.randomKey()
    do {
      try await b.pair(wrong.code())
      Issue.record("Wrong keys must fail")
    } catch {}
    await a.cancelInvitation()
    do {
      try await b.pair(code)
      Issue.record("Cancelled invitations must fail")
    } catch {}
    let snapshot = try await a.snapshot()
    #expect(snapshot.peers.isEmpty)
    await a.close()
    await b.close()
  }

  @Test func testApprovedJoinGroupIntroductionAndForget() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    defer { try? FileManager.default.removeItem(at: root) }
    let a = ZoneEngine()
    let b = ZoneEngine()
    let c = ZoneEngine()
    try await a.open(path: root.appendingPathComponent("a"), name: "A", discover: false)
    try await b.open(path: root.appendingPathComponent("b"), name: "B", discover: false)
    try await c.open(path: root.appendingPathComponent("c"), name: "C", discover: false)
    let aID = try await a.snapshot().device.id
    let cID = try await c.snapshot().device.id
    let aAddress = try #require(await a.endpoint())

    // Requests are ignored unless the connect panel is open.
    do {
      _ = try await b.join(aID, endpoint: aAddress)
      Issue.record("A closed panel must not answer")
    } catch {}

    // B joins A after A approves the matching code.
    await a.startPairing()
    let code = try await b.join(aID, endpoint: aAddress)
    #expect(try await a.snapshot().request == JoinRequest(name: "B", code: code))
    let waiting = Task { try await b.awaitApproval() }
    try await Task.sleep(for: .milliseconds(300))
    #expect(try await b.snapshot().peers.isEmpty)
    try await a.approve()
    try await waiting.value
    #expect(try await a.snapshot().peers.map(\.name) == ["B"])
    #expect(try await b.snapshot().peers.map(\.name) == ["A"])
    #expect(try await a.groupKey() == b.groupKey())

    // A declined request never pairs.
    _ = try await c.join(aID, endpoint: aAddress)
    await a.decline()
    do {
      try await c.awaitApproval()
      Issue.record("Declined joins must fail")
    } catch {}
    #expect(try await c.snapshot().peers.isEmpty)

    // Before C holds the group key, meeting A changes nothing.
    await c.discover(aID, name: "A", at: aAddress)
    await c.sync()
    #expect(try await a.snapshot().peers.count == 1)

    // C joins through B alone, then pairs with A without another approval.
    await a.stopPairing()
    await b.startPairing()
    let bID = try await b.snapshot().device.id
    _ = try await c.join(bID, endpoint: try #require(await b.endpoint()))
    let second = Task { try await c.awaitApproval() }
    try await b.approve()
    try await second.value
    await c.discover(aID, name: "A", at: aAddress)
    await c.sync()
    #expect(try await a.snapshot().peers.count == 2)
    #expect(try await c.snapshot().peers.count == 2)
    let note = try await c.add(text: "from C")
    try await waitFor(a) { $0.items.contains { $0.id == note } }

    // A forgotten device is not reconnected automatically from either side.
    try await a.forget(cID)
    try await c.forget(aID)
    await a.discover(cID, name: "C", at: try #require(await c.endpoint()))
    await a.sync()
    #expect(try await a.snapshot().peers.count == 1)
    #expect(try await c.snapshot().peers.count == 1)
    await a.close()
    await b.close()
    await c.close()
  }

  @Test func testEncryptionRejectsTamperingWrongKeysAndIdentityChanges() throws {
    let key = Codec.randomKey()
    let request = Request(action: "sync", since: 0)
    let data = try Codec.seal(request, key: key, sender: "a", recipient: "b")
    let envelope = try JSONDecoder().decode(Envelope.self, from: data)
    let opened = try Codec.open(envelope, key: key, as: Request.self)
    #expect(opened.id == request.id)
    #expect(throws: (any Error).self) {
      try Codec.open(envelope, key: Codec.randomKey(), as: Request.self)
    }
    var changed = envelope
    changed.sender = "c"
    #expect(throws: (any Error).self) { try Codec.open(changed, key: key, as: Request.self) }
    changed = envelope
    var bytes = try #require(Data(base64Encoded: envelope.sealed))
    bytes[bytes.count - 1] ^= 1
    changed.sealed = bytes.base64EncodedString()
    #expect(throws: (any Error).self) { try Codec.open(changed, key: key, as: Request.self) }
  }
}

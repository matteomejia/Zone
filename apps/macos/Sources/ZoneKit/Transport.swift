import CryptoKit
import Darwin
import Foundation
@preconcurrency import Network

let maximumFrame = 18 * 1024 * 1024
let serviceType = "_zone-pad._tcp"

struct Invitation: Codable, Sendable {
  var version: Int = 1
  var device: Device
  var hosts: [String]
  var port: UInt16
  var key: String
  var expires: Int64

  func code() throws -> String {
    let data = try JSONEncoder().encode(self)
    return "zone:swift1:"
      + data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(
        of: "/", with: "_"
      ).replacingOccurrences(of: "=", with: "")
  }

  static func parse(_ code: String) throws -> Invitation {
    let code = code.trimmingCharacters(in: .whitespacesAndNewlines)
    try require(
      code.utf8.count <= 16 * 1024 && code.hasPrefix("zone:swift1:"),
      "Use a current Zone invitation beginning with zone:swift1:.")
    var raw = String(code.dropFirst("zone:swift1:".count)).replacingOccurrences(of: "-", with: "+")
      .replacingOccurrences(of: "_", with: "/")
    raw += String(repeating: "=", count: (4 - raw.count % 4) % 4)
    guard let data = Data(base64Encoded: raw) else {
      throw CoreError(message: "Invalid invitation.")
    }
    let invitation = try JSONDecoder().decode(Invitation.self, from: data)
    try invitation.device.validate()
    try require(
      invitation.version == 1 && invitation.expires > milliseconds(),
      "This invitation is expired or unsupported. Create a new one.")
    try require(
      Data(base64Encoded: invitation.key)?.count == 32 && invitation.port > 0
        && !invitation.hosts.isEmpty && invitation.hosts.count <= 16
        && invitation.hosts.allSatisfy(isLocalHost), "Invalid local address or pairing key.")
    return invitation
  }
}

struct Envelope: Codable, Sendable {
  var version = 1
  var sender: String
  var recipient: String
  var sealed: String
}

enum Codec {
  static func aad(sender: String, recipient: String) -> Data {
    Data("zone/1\n\(sender)\n\(recipient)".utf8)
  }

  static func seal<T: Encodable>(_ value: T, key: String, sender: String, recipient: String) throws
    -> Data
  {
    guard let keyData = Data(base64Encoded: key), keyData.count == 32 else {
      throw CoreError(message: "Invalid pairing key.")
    }
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.withoutEscapingSlashes]
    let body = try encoder.encode(value)
    try require(body.count <= 12 * 1024 * 1024, "Message is too large.")
    let box = try AES.GCM.seal(
      body, using: SymmetricKey(data: keyData),
      authenticating: aad(sender: sender, recipient: recipient))
    guard let sealed = box.combined else {
      throw CoreError(message: "Could not encrypt the message.")
    }
    return try encoder.encode(
      Envelope(sender: sender, recipient: recipient, sealed: sealed.base64EncodedString()))
  }

  static func open<T: Decodable>(_ envelope: Envelope, key: String, as: T.Type) throws -> T {
    try require(
      envelope.version == 1 && envelope.sender.utf8.count <= 128
        && envelope.recipient.utf8.count <= 128, "Unsupported envelope.")
    guard let key = Data(base64Encoded: key), key.count == 32,
      let sealed = Data(base64Encoded: envelope.sealed), sealed.count >= 28,
      sealed.count <= 12 * 1024 * 1024 + 28
    else { throw CoreError(message: "Invalid encrypted message.") }
    let box = try AES.GCM.SealedBox(combined: sealed)
    let data = try AES.GCM.open(
      box, using: SymmetricKey(data: key),
      authenticating: aad(sender: envelope.sender, recipient: envelope.recipient))
    return try JSONDecoder().decode(T.self, from: data)
  }

  static func randomKey() -> String {
    SymmetricKey(size: .bits256).withUnsafeBytes { Data($0).base64EncodedString() }
  }

  /// The key two members of one group share. IDs are ordered by their UTF-8 bytes.
  static func pairKey(group: String, _ a: String, _ b: String) throws -> String {
    guard let group = Data(base64Encoded: group), group.count == 32 else {
      throw CoreError(message: "Invalid group key.")
    }
    let ids = [a, b].sorted { $0.utf8.lexicographicallyPrecedes($1.utf8) }
    return HKDF<SHA256>.deriveKey(
      inputKeyMaterial: SymmetricKey(data: group), salt: Data(),
      info: Data("zone/1 pair\n\(ids[0])\n\(ids[1])".utf8), outputByteCount: 32
    ).withUnsafeBytes { Data($0).base64EncodedString() }
  }

  /// The one-time pairing key and the six-digit code both screens show for a join.
  static func joinSecrets(
    _ secret: P256.KeyAgreement.PrivateKey, remote: Data, joiner: Data, host: Data
  ) throws -> (key: String, code: String) {
    let shared = try secret.sharedSecretFromKeyAgreement(
      with: P256.KeyAgreement.PublicKey(x963Representation: remote))
    func derive(_ label: String, _ count: Int) -> Data {
      shared.hkdfDerivedSymmetricKey(
        using: SHA256.self, salt: Data(), sharedInfo: Data(label.utf8) + joiner + host,
        outputByteCount: count
      ).withUnsafeBytes { Data($0) }
    }
    let digits = derive("zone/1 join code", 4).reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
    return (
      derive("zone/1 join key", 32).base64EncodedString(),
      String(format: "%06d", digits % 1_000_000)
    )
  }

  static func commitment(_ publicKey: Data) -> String {
    Data(SHA256.hash(data: publicKey)).base64EncodedString()
  }
}

/// Unencrypted join message: the two devices share no key yet.
struct Hello: Codable, Sendable {
  var version = 1
  var id: String
  var action: String
  var device: Device?
  var commit: String?
  var `public`: String?
}

struct HelloReply: Codable, Sendable {
  var id: String
  var device: Device
  var `public`: String?
}

struct Request: Codable, Sendable {
  var id = UUID().uuidString.lowercased()
  var action: String
  var device: Device?
  var hosts: [String]?
  var port: UInt16?
  var since: Int64?
}

struct Response: Codable, Sendable {
  var id: String
  var kind: String
  var device: Device
  var item: StoredItem?
  var hosts: [String]?
  var port: UInt16?
  var group: String?
}

/// Exactly one exchange uses a connection. Network.framework owns its I/O queue.
final class WireConnection: @unchecked Sendable {
  let connection: NWConnection
  static let queue = DispatchQueue(
    label: "app.zone.connections", qos: .utility, attributes: .concurrent)
  init(_ connection: NWConnection) { self.connection = connection }
  func start() { connection.start(queue: Self.queue) }
  func cancel() { connection.cancel() }

  func send(_ data: Data) async throws {
    try require(!data.isEmpty && data.count <= maximumFrame, "Message exceeds the transfer limit.")
    var length = UInt32(data.count).bigEndian
    var frame = withUnsafeBytes(of: &length) { Data($0) }
    frame.append(data)
    try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
      connection.send(
        content: frame,
        completion: .contentProcessed { error in
          if let error { continuation.resume(throwing: error) } else { continuation.resume() }
        })
    }
  }

  func receive(limit: Int = maximumFrame) async throws -> Data {
    let header = try await read(4)
    let count = header.reduce(0) { ($0 << 8) | Int($1) }
    try require(count > 0 && count <= min(limit, maximumFrame), "Invalid frame length.")
    return try await read(count)
  }

  private func read(_ count: Int) async throws -> Data {
    try await withCheckedThrowingContinuation { continuation in
      connection.receive(minimumIncompleteLength: count, maximumLength: count) {
        data, _, _, error in
        if let error {
          continuation.resume(throwing: error)
        } else if let data, data.count == count {
          continuation.resume(returning: data)
        } else {
          continuation.resume(
            throwing: CoreError(message: "The connection ended before the transfer finished."))
        }
      }
    }
  }
}

func deadline<T: Sendable>(
  _ seconds: Double, connection: WireConnection, operation: @escaping @Sendable () async throws -> T
) async throws -> T {
  try await withTaskCancellationHandler {
    try await withThrowingTaskGroup(of: T.self) { tasks in
      tasks.addTask { try await operation() }
      tasks.addTask {
        try await Task.sleep(for: .seconds(seconds))
        connection.cancel()
        throw CoreError(
          message: "The device did not respond. Keep both apps open on the same local network.")
      }
      defer { tasks.cancelAll() }
      guard let result = try await tasks.next() else { throw CancellationError() }
      return result
    }
  } onCancel: {
    connection.cancel()
  }
}

private final class ReadyOnce: @unchecked Sendable {
  private let lock = NSLock()
  private var continuation: CheckedContinuation<UInt16, Error>?
  init(_ continuation: CheckedContinuation<UInt16, Error>) { self.continuation = continuation }
  @discardableResult func finish(_ result: Result<UInt16, Error>) -> Bool {
    lock.lock()
    let current = continuation
    continuation = nil
    lock.unlock()
    current?.resume(with: result)
    return current != nil
  }
}

func startListener(_ listener: NWListener, queue: DispatchQueue) async throws -> UInt16 {
  try await withCheckedThrowingContinuation { continuation in
    let once = ReadyOnce(continuation)
    listener.stateUpdateHandler = { state in
      switch state {
      case .ready:
        if let port = listener.port?.rawValue { once.finish(.success(port)) }
      case .failed(let error): once.finish(.failure(error))
      case .cancelled: once.finish(.failure(CancellationError()))
      default: break
      }
    }
    listener.start(queue: queue)
    queue.asyncAfter(deadline: .now() + 8) {
      if once.finish(
        .failure(
          CoreError(
            message: "Local networking could not start. Check Zone’s local-network permission.")))
      {
        listener.cancel()
      }
    }
  }
}

func serviceName(_ id: String) -> String {
  "zone-" + SHA256.hash(data: Data(id.utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
}

func localAddresses() -> [String] {
  var head: UnsafeMutablePointer<ifaddrs>?
  guard getifaddrs(&head) == 0 else { return ["127.0.0.1"] }
  defer { freeifaddrs(head) }
  var addresses = Set<String>()
  var current = head
  while let entry = current {
    defer { current = entry.pointee.ifa_next }
    guard let addr = entry.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET),
      entry.pointee.ifa_flags & UInt32(IFF_UP) != 0
    else { continue }
    var buffer = [CChar](repeating: 0, count: Int(NI_MAXHOST))
    if getnameinfo(
      addr, socklen_t(addr.pointee.sa_len), &buffer, socklen_t(buffer.count), nil, 0, NI_NUMERICHOST
    ) == 0 {
      addresses.insert(
        String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self))
    }
  }
  return Array(addresses.filter { isLocalHost($0) && !$0.hasPrefix("127.") }.sorted().prefix(15))
    + ["127.0.0.1"]
}

func isLocalHost(_ host: String) -> Bool {
  let parts = host.split(separator: ".", omittingEmptySubsequences: false).compactMap { UInt8($0) }
  guard parts.count == 4, parts.map({ String($0) }).joined(separator: ".") == host else {
    return false
  }
  return parts[0] == 10 || parts[0] == 127 || (parts[0] == 172 && (16...31).contains(parts[1]))
    || (parts[0] == 192 && parts[1] == 168) || (parts[0] == 169 && parts[1] == 254)
}

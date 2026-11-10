import Foundation

public struct CoreError: LocalizedError, Sendable {
  public let message: String
  public init(message: String) { self.message = message }
  public var errorDescription: String? { message }
}

func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
  guard condition() else { throw CoreError(message: message) }
}

func milliseconds() -> Int64 { Int64(Date().timeIntervalSince1970 * 1000) }

public struct Device: Codable, Identifiable, Sendable, Equatable {
  public let id: String
  public var name: String
  public let platform: String

  func validate() throws {
    try require(
      !id.isEmpty && id.utf8.count <= 128 && !id.contains("\n"), "Invalid device identity.")
    try require(
      !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && name.utf8.count <= 100,
      "Device names must be between 1 and 100 bytes.")
    try require(platform.utf8.count <= 32, "Invalid platform.")
  }
}

public struct Peer: Identifiable, Sendable, Equatable {
  public let id: String
  public let name: String
  public let platform: String
  public let lastSync: Int64?
  public let error: String?
  public let online: Bool
}

public struct Note: Identifiable, Sendable, Equatable {
  public let id: String
  public let ownerId: String
  public let revision: Int64
  public let createdAt: Int64
  public let kind: String
  public let text: String
  public let name: String
  public let mime: String
  public let size: Int
  public var date: Date { Date(timeIntervalSince1970: Double(createdAt) / 1000) }
}

/// A Zone device seen on the local network that is not connected yet.
public struct Nearby: Identifiable, Sendable, Equatable {
  public let id: String
  public let name: String
}

/// Another device asking to connect. Both screens show the same code.
public struct JoinRequest: Sendable, Equatable {
  public let name: String
  public let code: String
}

public struct Snapshot: Sendable, Equatable {
  public let device: Device
  public let peers: [Peer]
  public let items: [Note]
  public let discoveryError: String?
  public let nearby: [Nearby]
  public let request: JoinRequest?
}

struct StoredItem: Codable, Sendable, Equatable {
  var id: String
  var revision: Int64
  var createdAt: Int64
  var kind: String
  var text: String
  var name: String
  var mime: String
  var data: String
  var deleted: Bool

  func validate() throws {
    try require(
      UUID(uuidString: id) != nil && revision > 0 && createdAt >= 0,
      "Invalid item identity or revision.")
    try require(["text", "image", "file"].contains(kind), "Unsupported item type.")
    try require(
      text.utf8.count <= 1024 * 1024 && name.utf8.count <= 255 && mime.utf8.count <= 128,
      "This item is too large.")
    try require(
      data.utf8.count <= ((8 * 1024 * 1024 + 2) / 3) * 4, "Files must be 8 MB or smaller.")
    guard let decoded = Data(base64Encoded: data) else {
      throw CoreError(message: "Invalid attachment encoding.")
    }
    try require(decoded.count <= 8 * 1024 * 1024, "Files must be 8 MB or smaller.")
  }

  func summary(owner: String) -> Note {
    Note(
      id: id, ownerId: owner, revision: revision, createdAt: createdAt, kind: kind, text: text,
      name: name, mime: mime, size: Data(base64Encoded: data)?.count ?? 0)
  }
}

struct StoredPeer: Codable, Sendable {
  var device: Device
  var key: String
  var hosts: [String]
  var port: UInt16
  var cursor: Int64 = 0
  var lastSync: Int64?
  var error: String?
  var summary: Peer {
    Peer(
      id: device.id, name: device.name, platform: device.platform, lastSync: lastSync, error: error,
      online: error == nil && lastSync.map { milliseconds() - $0 < 10_000 } == true)
  }
}

import CSQLite
import Darwin
import Foundation

private enum SQLValue {
  case text(String)
  case integer(Int64)
}

/// Owned exclusively by one Store actor. No pointer crosses the actor boundary.
private final class Database: @unchecked Sendable {
  private var db: OpaquePointer?
  private var lockFD: Int32 = -1

  init(path: URL) throws {
    try FileManager.default.createDirectory(
      at: path, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path.path)
    lockFD = Darwin.open(path.appendingPathComponent("zone.lock").path, O_CREAT | O_RDWR, 0o600)
    guard lockFD >= 0, flock(lockFD, LOCK_EX | LOCK_NB) == 0 else {
      if lockFD >= 0 {
        Darwin.close(lockFD)
        lockFD = -1
      }
      throw CoreError(message: "Zone is already open in another instance.")
    }
    if sqlite3_open(path.appendingPathComponent("zone.sqlite3").path, &db) != SQLITE_OK {
      close()
      throw CoreError(message: "Could not open the scratchpad database.")
    }
    do {
      try execute("PRAGMA journal_mode=WAL")
      try execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
      try execute(
        "CREATE TABLE IF NOT EXISTS items (owner TEXT NOT NULL, id TEXT NOT NULL, revision INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY(owner,id))"
      )
      try execute("CREATE INDEX IF NOT EXISTS item_revision ON items(owner,revision)")
      // A new trust table intentionally excludes old iroh credentials.
      try execute(
        "CREATE TABLE IF NOT EXISTS native_peers (id TEXT PRIMARY KEY, body TEXT NOT NULL)")
    } catch {
      close()
      throw error
    }
  }

  deinit { close() }

  func close() {
    if let db {
      sqlite3_close(db)
      self.db = nil
    }
    if lockFD >= 0 {
      flock(lockFD, LOCK_UN)
      Darwin.close(lockFD)
      lockFD = -1
    }
  }

  private func statement(_ sql: String, _ values: [SQLValue]) throws -> OpaquePointer {
    guard let db else { throw CoreError(message: "Zone is closed.") }
    var pointer: OpaquePointer?
    guard sqlite3_prepare_v2(db, sql, -1, &pointer, nil) == SQLITE_OK, let pointer else {
      throw failure()
    }
    let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
    for (offset, value) in values.enumerated() {
      let status: Int32
      switch value {
      case .text(let string):
        status = string.withCString {
          sqlite3_bind_text(pointer, Int32(offset + 1), $0, Int32(string.utf8.count), transient)
        }
      case .integer(let number): status = sqlite3_bind_int64(pointer, Int32(offset + 1), number)
      }
      guard status == SQLITE_OK else {
        sqlite3_finalize(pointer)
        throw failure()
      }
    }
    return pointer
  }

  func execute(_ sql: String, _ values: [SQLValue] = []) throws {
    let stmt = try statement(sql, values)
    defer { sqlite3_finalize(stmt) }
    let status = sqlite3_step(stmt)
    guard status == SQLITE_DONE || status == SQLITE_ROW else { throw failure() }
  }

  func rows(_ sql: String, _ values: [SQLValue] = []) throws -> [[String]] {
    let stmt = try statement(sql, values)
    defer { sqlite3_finalize(stmt) }
    var rows: [[String]] = []
    while true {
      let status = sqlite3_step(stmt)
      if status == SQLITE_DONE { return rows }
      guard status == SQLITE_ROW else { throw failure() }
      rows.append(
        (0..<sqlite3_column_count(stmt)).map { column in
          guard let bytes = sqlite3_column_text(stmt, column) else { return "" }
          let size = Int(sqlite3_column_bytes(stmt, column))
          return String(decoding: UnsafeBufferPointer(start: bytes, count: size), as: UTF8.self)
        })
    }
  }

  func transaction(_ action: () throws -> Void) throws {
    try execute("BEGIN IMMEDIATE")
    do {
      try action()
      try execute("COMMIT")
    } catch {
      try? execute("ROLLBACK")
      throw error
    }
  }

  private func failure() -> CoreError {
    CoreError(message: db.map { String(cString: sqlite3_errmsg($0)) } ?? "Zone is closed.")
  }
}

actor Store {
  private let db: Database
  private var device: Device

  init(path: URL, name: String) throws {
    let database = try Database(path: path)
    db = database
    if let saved = try database.rows("SELECT value FROM settings WHERE key='device'").first?.first {
      device = try JSONDecoder().decode(Device.self, from: Data(saved.utf8))
    } else {
      device = Device(id: UUID().uuidString.lowercased(), name: name, platform: "macos")
      try device.validate()
      try database.execute(
        "INSERT INTO settings(key,value) VALUES ('device',?)", [.text(try json(device))])
    }
    try device.validate()
    try database.execute(
      "INSERT OR IGNORE INTO settings(key,value) VALUES ('group',?)", [.text(Codec.randomKey())])
  }

  private func setting(_ key: String) throws -> String? {
    try db.rows("SELECT value FROM settings WHERE key=?", [.text(key)]).first?.first
  }

  private func set(_ key: String, _ value: String) throws {
    try db.execute(
      "INSERT INTO settings(key,value) VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
      [.text(key), .text(value)])
  }

  /// Devices holding this key connect to each other without asking.
  func group() throws -> String {
    guard let group = try setting("group") else { throw CoreError(message: "Missing group key.") }
    return group
  }

  func setGroup(_ key: String) throws {
    try require(Data(base64Encoded: key)?.count == 32, "Invalid group key.")
    try set("group", key)
  }

  /// Forgotten devices. They reconnect only when the user connects them again.
  func blocked() throws -> Set<String> {
    try setting("blocked").map { try decode($0, as: Set<String>.self) } ?? []
  }

  func identity() -> Device { device }
  func close() { db.close() }

  func rename(_ name: String) throws {
    var next = device
    next.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
    try next.validate()
    try db.execute("UPDATE settings SET value=? WHERE key='device'", [.text(try json(next))])
    device = next
  }

  func peers() throws -> [StoredPeer] {
    try db.rows("SELECT body FROM native_peers ORDER BY id").map {
      try decode($0[0], as: StoredPeer.self)
    }
  }

  func peer(_ id: String) throws -> StoredPeer? {
    try db.rows("SELECT body FROM native_peers WHERE id=?", [.text(id)]).first.map {
      try decode($0[0], as: StoredPeer.self)
    }
  }

  func pair(
    device peer: Device, key: String, hosts: [String], port: UInt16, explicit: Bool = true
  ) throws {
    try peer.validate()
    var blocked = try blocked()
    if blocked.contains(peer.id) {
      try require(explicit, "This device was forgotten.")
      blocked.remove(peer.id)
      try set("blocked", try json(blocked))
    }
    try require(peer.id != device.id, "Use the invitation on your other device.")
    try require(
      Data(base64Encoded: key)?.count == 32 && !hosts.isEmpty && hosts.count <= 16
        && hosts.allSatisfy(isLocalHost) && port > 0, "Invalid pairing details.")
    let existing = try self.peer(peer.id)
    let count = try peers().count
    try require(existing != nil || count < 16, "You can pair up to 16 devices.")
    var record = existing ?? StoredPeer(device: peer, key: key, hosts: hosts, port: port)
    record.device = peer
    record.key = key
    record.hosts = hosts
    record.port = port
    try savePeer(record)
  }

  private func savePeer(_ peer: StoredPeer) throws {
    try db.execute(
      "INSERT INTO native_peers(id,body) VALUES (?,?) ON CONFLICT(id) DO UPDATE SET body=excluded.body",
      [.text(peer.device.id), .text(try json(peer))])
  }

  func forget(_ id: String) throws {
    try require(id != device.id, "Cannot forget this device.")
    let blocked = try json(try self.blocked().union([id]))
    try db.transaction {
      try set("blocked", blocked)
      try db.execute("DELETE FROM native_peers WHERE id=?", [.text(id)])
      try db.execute("DELETE FROM items WHERE owner=?", [.text(id)])
    }
  }

  @discardableResult
  func add(text: String, name: String = "", mime: String = "", data: Data = Data()) throws -> String
  {
    try require(
      !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !data.isEmpty,
      "Paste some text or choose a nonempty file.")
    try require(data.count <= 8 * 1024 * 1024, "Files must be 8 MB or smaller.")
    let kind =
      data.isEmpty
      ? "text"
      : ["image/png", "image/jpeg", "image/webp", "image/gif"].contains(mime) ? "image" : "file"
    let item = StoredItem(
      id: UUID().uuidString.lowercased(), revision: try nextRevision(), createdAt: milliseconds(),
      kind: kind, text: text, name: name, mime: mime, data: data.base64EncodedString(),
      deleted: false)
    try item.validate()
    try write(item, owner: device.id)
    return item.id
  }

  private func nextRevision() throws -> Int64 {
    guard
      let raw = try db.rows(
        "SELECT COALESCE(MAX(revision),0) FROM items WHERE owner=?", [.text(device.id)]
      ).first?.first,
      let current = Int64(raw), current < Int64.max
    else { throw CoreError(message: "Invalid local revision.") }
    return current + 1
  }

  private func write(_ item: StoredItem, owner: String) throws {
    try db.execute(
      "INSERT INTO items(owner,id,revision,body) VALUES (?,?,?,?) ON CONFLICT(owner,id) DO UPDATE SET revision=excluded.revision,body=excluded.body",
      [.text(owner), .text(item.id), .integer(item.revision), .text(try json(item))])
  }

  func item(owner: String, id: String) throws -> StoredItem {
    guard
      let body = try db.rows(
        "SELECT body FROM items WHERE owner=? AND id=?", [.text(owner), .text(id)]
      ).first?.first
    else { throw CoreError(message: "This note could not be found.") }
    return try decode(body, as: StoredItem.self)
  }

  func delete(_ id: String) throws {
    var item = try item(owner: device.id, id: id)
    item.revision = try nextRevision()
    item.deleted = true
    item.text = ""
    item.data = ""
    try write(item, owner: device.id)
  }

  func nextChange(after cursor: Int64) throws -> StoredItem? {
    try require(cursor >= 0, "Invalid synchronization cursor.")
    return try db.rows(
      "SELECT body FROM items WHERE owner=? AND revision>? ORDER BY revision LIMIT 1",
      [.text(device.id), .integer(cursor)]
    ).first.map { try decode($0[0], as: StoredItem.self) }
  }

  func apply(owner: String, identity: Device, item: StoredItem?) throws {
    guard var peer = try peer(owner) else { throw CoreError(message: "This device is not paired.") }
    try identity.validate()
    try require(identity.id == owner && owner != device.id, "Device identity mismatch.")
    if let item {
      try item.validate()
      try require(item.revision > peer.cursor, "Outdated change.")
    }
    peer.device = identity
    peer.lastSync = milliseconds()
    peer.error = nil
    if let item { peer.cursor = item.revision }
    try db.transaction {
      if let item { try write(item, owner: owner) }
      try savePeer(peer)
    }
  }

  func markOffline(_ id: String) throws {
    guard var peer = try peer(id) else { return }
    peer.error = "Waiting for this device on your local network."
    try savePeer(peer)
  }

  func snapshot(
    discoveryError: String? = nil, nearby: [String: String] = [:], request: JoinRequest? = nil
  ) throws -> Snapshot {
    let peers = try peers()
    let known = Set(peers.map(\.device.id))
    let nearby = nearby.filter { !known.contains($0.key) }.map {
      Nearby(id: $0.key, name: $0.value)
    }
    .sorted { ($0.name, $0.id) < ($1.name, $1.id) }
    let owners = Set(peers.map(\.device.id) + [device.id])
    let items = try db.rows("SELECT owner,body FROM items ORDER BY revision DESC").compactMap {
      row -> Note? in
      guard owners.contains(row[0]) else { return nil }
      let item = try decode(row[1], as: StoredItem.self)
      return item.deleted ? nil : item.summary(owner: row[0])
    }
    return Snapshot(
      device: device, peers: peers.map(\.summary), items: items, discoveryError: discoveryError,
      nearby: nearby, request: request)
  }
}

func json<T: Encodable>(_ value: T) throws -> String {
  let encoder = JSONEncoder()
  encoder.outputFormatting = [.withoutEscapingSlashes]
  return String(decoding: try encoder.encode(value), as: UTF8.self)
}
func decode<T: Decodable>(_ string: String, as: T.Type) throws -> T {
  try JSONDecoder().decode(T.self, from: Data(string.utf8))
}

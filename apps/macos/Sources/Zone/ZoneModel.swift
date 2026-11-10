import AppKit
import Combine
import UniformTypeIdentifiers
import ZoneKit

/// Where a card sits on this Mac's board. Arrangement is local and never synced.
struct Spot: Codable, Equatable {
  var x: Double
  var y: Double
  var z: Int
}

@MainActor
final class ZoneModel: ObservableObject {
  let core = ZoneEngine()
  @Published var snapshot: Snapshot? { didSet { arrange() } }
  @Published var draft = ""
  @Published var notebookDeviceID: String?
  @Published var error: String?
  @Published private(set) var spots: [String: Spot] =
    UserDefaults.standard.data(forKey: "board.spots")
    .flatMap { try? JSONDecoder().decode([String: Spot].self, from: $0) } ?? [:]
  /// The newest local item, so the board can scroll to it.
  @Published private(set) var fresh: String?
  var boardWidth: CGFloat = 370
  private var drops: [CGPoint] = []
  private var primed = false
  private var started = false
  private var poller: Task<Void, Never>?
  private var refreshing = false

  func start() async {
    guard !started else { return }
    started = true
    do {
      let support = try FileManager.default.url(
        for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
      // Same location as the original shell: preserves identity, peers, and notes.
      // ZONE_DATA_DIR points a development run at a throwaway store.
      let path =
        ProcessInfo.processInfo.environment["ZONE_DATA_DIR"].map {
          URL(fileURLWithPath: $0, isDirectory: true)
        } ?? support.appendingPathComponent("app.zone.scratchpad", isDirectory: true)
      let name = String((Host.current().localizedName ?? "My Mac").prefix(30))
      try await core.open(path: path, name: name)
      await refresh()
      poller = Task { [weak self] in
        while !Task.isCancelled {
          do { try await Task.sleep(for: .seconds(1)) } catch { break }
          await self?.refresh()
        }
      }
    } catch { self.error = error.localizedDescription }
  }

  func shutdown() async {
    poller?.cancel()
    await core.close()
  }

  func refresh() async {
    guard !refreshing else { return }
    refreshing = true
    defer { refreshing = false }
    do {
      let next = try await core.snapshot()
      if next != snapshot { snapshot = next }
    } catch { self.error = error.localizedDescription }
  }

  /// Gives every unplaced item a spot and forgets items that no longer exist.
  func arrange() {
    guard let snapshot else { return }
    let live = Set(snapshot.items.map(\.id))
    var next = spots.filter { live.contains($0.key) }
    var top = next.values.map(\.z).max() ?? 0
    var newest: String?
    for (owner, notes) in Dictionary(grouping: snapshot.items, by: \.ownerId) {
      let missing = notes.filter { next[$0.id] == nil }.sorted { $0.createdAt < $1.createdAt }
      guard !missing.isEmpty else { continue }
      var taken = notes.compactMap { note in
        next[note.id].map { CGRect(origin: CGPoint(x: $0.x, y: $0.y), size: Board.size(note)) }
      }
      for note in missing {
        let size = Board.size(note)
        var origin = Board.freeSpot(size, among: taken, width: boardWidth)
        if owner == snapshot.device.id, primed {
          newest = note.id
          if !drops.isEmpty {
            let drop = drops.removeFirst()
            origin = CGPoint(
              x: max(4, drop.x - size.width / 2), y: max(4, drop.y - size.height / 2))
          }
        }
        top += 1
        next[note.id] = Spot(x: origin.x, y: origin.y, z: top)
        taken.append(CGRect(origin: origin, size: size))
      }
    }
    primed = true
    if next != spots {
      spots = next
      UserDefaults.standard.set(try? JSONEncoder().encode(next), forKey: "board.spots")
    }
    if let newest { fresh = newest }
  }

  func move(_ id: String, by offset: CGSize) {
    guard var spot = spots[id] else { return }
    spot.x = max(4, spot.x + offset.width)
    spot.y = max(4, spot.y + offset.height)
    spots[id] = spot
    raise(id)
  }

  func raise(_ id: String) {
    guard var spot = spots[id] else { return }
    let top = spots.values.map(\.z).max() ?? 0
    if spot.z < top { spot.z = top + 1 }
    spots[id] = spot
    UserDefaults.standard.set(try? JSONEncoder().encode(spots), forKey: "board.spots")
  }

  /// Runs one addition on this device's board, optionally at a point the user dropped onto.
  private func adding(at spots: [CGPoint], _ work: @escaping () async throws -> Void) {
    notebookDeviceID = snapshot?.device.id
    drops += spots
    Task {
      do { try await work() } catch {
        drops.removeAll()
        self.error = error.localizedDescription
      }
      await refresh()
    }
  }

  func addText() {
    guard !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
    add(text: draft)
    draft = ""
  }

  func add(text: String, at spot: CGPoint? = nil) {
    guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
    adding(at: spot.map { [$0] } ?? []) { [core] in try await core.add(text: text) }
  }

  /// Adds whatever the pasteboard holds: files first, then an image, then (if allowed) text.
  @discardableResult
  func ingest(_ board: NSPasteboard, text: Bool) -> Bool {
    let urls =
      board.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true])
      as? [URL] ?? []
    if !urls.isEmpty {
      addFiles(urls)
    } else if board.availableType(from: [.png, .tiff]) != nil,
      let image = NSImage(pasteboard: board)
    {
      addImage(image)
    } else if text, let string = board.string(forType: .string),
      !string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    {
      add(text: string)
    } else {
      return false
    }
    return true
  }

  func attach() {
    let panel = NSOpenPanel()
    panel.canChooseDirectories = false
    panel.allowsMultipleSelection = true
    panel.prompt = "Add"
    panel.begin { [weak self] result in
      guard result == .OK else { return }
      Task { @MainActor in self?.addFiles(panel.urls) }
    }
  }

  func addFiles(_ urls: [URL], at spot: CGPoint? = nil) {
    guard !urls.isEmpty else { return }
    let spots = spot.map { spot in
      urls.indices.map { CGPoint(x: spot.x + CGFloat($0) * 18, y: spot.y + CGFloat($0) * 18) }
    }
    adding(at: spots ?? []) { [core] in
      for url in urls {
        let (data, mime) = try await Task.detached {
          let size = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
          guard size.isRegularFile == true, let count = size.fileSize, count > 0,
            count <= 8 * 1024 * 1024
          else {
            throw CoreError(message: "Choose a nonempty file up to 8 MB: \(url.lastPathComponent)")
          }
          let data = try Data(contentsOf: url)
          let mime =
            UTType(filenameExtension: url.pathExtension)?.preferredMIMEType
            ?? "application/octet-stream"
          return (data, mime)
        }.value
        try await core.add(text: "", name: url.lastPathComponent, mime: mime, data: data)
      }
    }
  }

  func addImage(_ image: NSImage, at spot: CGPoint? = nil) {
    guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:])
    else {
      error = "This image could not be read."
      return
    }
    guard png.count <= 8 * 1024 * 1024 else {
      error = "Images must be smaller than 8 MB."
      return
    }
    adding(at: spot.map { [$0] } ?? []) { [core] in
      try await core.add(text: "", name: "Pasted image.png", mime: "image/png", data: png)
    }
  }

  func remove(_ note: Note) {
    Task {
      do {
        try await core.delete(note.id)
        await refresh()
      } catch { self.error = error.localizedDescription }
    }
  }

  func attachment(_ note: Note) async throws -> Data {
    try await core.attachment(owner: note.ownerId, id: note.id)
  }

  func copy(_ note: Note) {
    Task {
      do {
        if note.kind == "text" {
          NSPasteboard.general.clearContents()
          NSPasteboard.general.setString(note.text, forType: .string)
        } else if note.kind == "image" {
          let data = try await attachment(note)
          guard let image = NSImage(data: data) else {
            throw CoreError(message: "This image could not be read.")
          }
          NSPasteboard.general.clearContents()
          NSPasteboard.general.writeObjects([image])
        }
      } catch { self.error = error.localizedDescription }
    }
  }

  func save(_ note: Note) {
    Task {
      do {
        let data = try await attachment(note)
        let panel = NSSavePanel()
        panel.nameFieldStringValue = (note.name as NSString).lastPathComponent
        let result = await panel.begin()
        if result == .OK, let url = panel.url {
          try await Task.detached { try data.write(to: url, options: .atomic) }.value
        }
      } catch { self.error = error.localizedDescription }
    }
  }
}

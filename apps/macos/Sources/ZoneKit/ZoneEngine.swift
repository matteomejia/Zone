import CryptoKit
import Foundation
@preconcurrency import Network

/// A join request received while this device's connect panel is open.
private struct Incoming {
  let id: String
  let device: Device
  let commit: String
  let secret: P256.KeyAgreement.PrivateKey
  let started = milliseconds()
  var key: String?
  var code: String?
}

/// A join this device started and is waiting to have approved.
private struct Outgoing {
  let id: String
  let device: Device
  let key: String
  let publicKey: String
  let endpoint: NWEndpoint
}

public actor ZoneEngine {
  private var store: Store?
  private var device: Device?
  private var listener: NWListener?
  private var browser: NWBrowser?
  private var port: UInt16 = 0
  private var invitation: Invitation?
  private var discovered: [String: NWEndpoint] = [:]
  private var nearby: [String: String] = [:]
  private var attempted: [String: Int64] = [:]
  private var advertising = false
  private var accepting = false
  private var incoming: Incoming?
  private var outgoing: Outgoing?
  private var admit: String?
  private var connections: [UUID: WireConnection] = [:]
  private var poller: Task<Void, Never>?
  private var syncing = false
  private var discoveryError: String?
  private let queue = DispatchQueue(label: "app.zone.discovery")

  public init() {}

  public func open(path: URL, name: String, discover: Bool = true) async throws {
    guard store == nil else { return }
    let store = try Store(path: path, name: name)
    self.store = store
    device = await store.identity()
    do {
      let listener = try NWListener(using: .tcp)
      self.listener = listener
      advertising = discover
      advertise()
      listener.newConnectionHandler = { [weak self] connection in
        Task { await self?.accept(WireConnection(connection)) }
      }
      port = try await startListener(listener, queue: queue)
      if discover { startBrowser() }
      poller = Task { [weak self] in
        while !Task.isCancelled {
          await self?.sync()
          do { try await Task.sleep(for: .seconds(1)) } catch { break }
        }
      }
    } catch {
      listener?.cancel()
      listener = nil
      discoveryError = "Local sync is unavailable: \(error.localizedDescription)"
    }
  }

  public func close() async {
    poller?.cancel()
    poller = nil
    listener?.cancel()
    listener = nil
    browser?.cancel()
    browser = nil
    stopPairing()
    nearby.removeAll()
    attempted.removeAll()
    for connection in connections.values { connection.cancel() }
    connections.removeAll()
    if let store { await store.close() }
    store = nil
    device = nil
    port = 0
    discovered.removeAll()
  }

  private func storage() throws -> Store {
    guard let store else { throw CoreError(message: "Zone is not open.") }
    return store
  }

  public func snapshot() async throws -> Snapshot {
    let request = incoming.flatMap { pending in
      pending.code.map { JoinRequest(name: pending.device.name, code: $0) }
    }
    return try await storage().snapshot(
      discoveryError: discoveryError, nearby: nearby, request: request)
  }

  /// The service carries this device's ID and name so others can list it.
  private func advertise() {
    guard advertising, let device else { return }
    listener?.service = NWListener.Service(
      name: serviceName(device.id), type: serviceType, domain: nil,
      txtRecord: NWTXTRecord(["id": device.id, "name": device.name]))
  }

  @discardableResult
  public func add(text: String, name: String = "", mime: String = "", data: Data = Data())
    async throws -> String
  {
    try await storage().add(text: text, name: name, mime: mime, data: data)
  }

  public func delete(_ id: String) async throws { try await storage().delete(id) }
  public func rename(_ name: String) async throws {
    let store = try storage()
    try await store.rename(name)
    device = await store.identity()
    advertise()
  }
  public func forget(_ id: String) async throws { try await storage().forget(id) }

  public func attachment(owner: String, id: String) async throws -> Data {
    let item = try await storage().item(owner: owner, id: id)
    try require(!item.deleted, "This attachment was deleted.")
    guard let data = Data(base64Encoded: item.data) else {
      throw CoreError(message: "Invalid attachment.")
    }
    return data
  }

  public func createInvitation() throws -> String {
    guard let device, port > 0 else {
      throw CoreError(
        message: "Local networking has not started. Check local-network access, then reopen Zone.")
    }
    let value = Invitation(
      device: device, hosts: localAddresses(), port: port, key: Codec.randomKey(),
      expires: milliseconds() + 600_000)
    invitation = value
    return try value.code()
  }

  public func cancelInvitation() { invitation = nil }

  /// Join requests are answered only between these two calls, while the connect panel is open.
  public func startPairing() { accepting = true }
  public func stopPairing() {
    accepting = false
    incoming = nil
    outgoing = nil
    invitation = nil
    admit = nil
  }

  /// Asks a nearby device to connect. Returns the code to compare on both screens.
  public func join(_ id: String) async throws -> String {
    guard let endpoint = discovered[serviceName(id)] else {
      throw CoreError(message: "That device is no longer nearby.")
    }
    return try await join(id, endpoint: endpoint)
  }

  func join(_ id: String, endpoint: NWEndpoint) async throws -> String {
    guard let device, port > 0 else { throw CoreError(message: "Local networking is unavailable.") }
    let secret = P256.KeyAgreement.PrivateKey()
    let own = secret.publicKey.x963Representation
    let joinID = UUID().uuidString.lowercased()
    // The commitment goes first so neither side can steer the code after seeing the other's key.
    let reply = try await greet(
      Hello(id: joinID, action: "join", device: device, commit: Codec.commitment(own)),
      at: endpoint)
    try reply.device.validate()
    guard reply.id == joinID, reply.device.id == id,
      let remote = reply.public.flatMap({ Data(base64Encoded: $0) })
    else { throw CoreError(message: "The other device did not answer correctly.") }
    let secrets = try Codec.joinSecrets(secret, remote: remote, joiner: own, host: remote)
    let pending = Outgoing(
      id: joinID, device: reply.device, key: secrets.key, publicKey: own.base64EncodedString(),
      endpoint: endpoint)
    _ = try await greet(
      Hello(id: joinID, action: "reveal", public: pending.publicKey), at: endpoint)
    outgoing = pending
    return secrets.code
  }

  /// Waits until the other device approves the join started by `join`.
  public func awaitApproval() async throws {
    guard let pending = outgoing else { throw CoreError(message: "No connection in progress.") }
    defer { outgoing = nil }
    for _ in 0..<120 {
      try Task.checkCancellation()
      if try await claim(pending) { return }
      try await Task.sleep(for: .seconds(1))
      do {
        _ = try await greet(
          Hello(id: pending.id, action: "reveal", public: pending.publicKey), at: pending.endpoint)
      } catch {
        // Approval may have landed between the two calls.
        if try await claim(pending) { return }
        throw CoreError(message: "The other device declined or stopped waiting.")
      }
    }
    throw CoreError(message: "The other device did not approve in time.")
  }

  private func claim(_ pending: Outgoing) async throws -> Bool {
    guard let device else { throw CoreError(message: "Zone is closed.") }
    let request = Request(action: "pair", device: device, hosts: localAddresses(), port: port)
    guard
      let response = try? await exchange(
        request, peer: pending.device.id, key: pending.key, hosts: [], port: 0,
        extra: pending.endpoint), response.kind == "paired"
    else { return false }
    try await adopt(response, key: pending.key, hosts: [], port: 0)
    return true
  }

  /// Stores a peer that accepted pairing and joins its group.
  private func adopt(
    _ response: Response, key: String, hosts: [String], port: UInt16, explicit: Bool = true
  ) async throws {
    let store = try storage()
    try await store.pair(
      device: response.device, key: key, hosts: response.hosts ?? hosts,
      port: response.port ?? port, explicit: explicit)
    if explicit, let group = response.group { try await store.setGroup(group) }
  }

  public func approve() async throws {
    guard let device, let pending = incoming, let key = pending.key else {
      throw CoreError(message: "That request is no longer waiting.")
    }
    invitation = Invitation(
      device: device, hosts: localAddresses(), port: port, key: key,
      expires: milliseconds() + 30_000)
    admit = pending.device.id
    incoming = nil
  }

  public func decline() { incoming = nil }

  private func greet(_ hello: Hello, at endpoint: NWEndpoint) async throws -> HelloReply {
    let payload = try JSONEncoder().encode(hello)
    let wire = WireConnection(NWConnection(to: endpoint, using: .tcp))
    let id = UUID()
    connections[id] = wire
    wire.start()
    defer {
      wire.cancel()
      connections[id] = nil
    }
    return try await deadline(5, connection: wire) {
      try await wire.send(payload)
      let reply = try JSONDecoder().decode(
        HelloReply.self, from: try await wire.receive(limit: 64 * 1024))
      try await wire.send(Data([1]))
      return reply
    }
  }

  private func answer(_ hello: Hello) throws -> HelloReply {
    guard let device, accepting, hello.version == 1, UUID(uuidString: hello.id) != nil else {
      throw CoreError(message: "Not accepting connections.")
    }
    if hello.action == "join", let remote = hello.device, let commit = hello.commit {
      try remote.validate()
      try require(remote.id != device.id, "Invalid device.")
      if let incoming, incoming.code != nil, incoming.id != hello.id {
        try require(milliseconds() - incoming.started > 120_000, "Another request is waiting.")
      }
      let secret = P256.KeyAgreement.PrivateKey()
      incoming = Incoming(id: hello.id, device: remote, commit: commit, secret: secret)
      return HelloReply(
        id: hello.id, device: device,
        public: secret.publicKey.x963Representation.base64EncodedString())
    }
    guard hello.action == "reveal", var pending = incoming, pending.id == hello.id,
      milliseconds() - pending.started <= 120_000,
      let remote = hello.public.flatMap({ Data(base64Encoded: $0) })
    else { throw CoreError(message: "No such request.") }
    if pending.code == nil {
      try require(Codec.commitment(remote) == pending.commit, "Key does not match.")
      let secrets = try Codec.joinSecrets(
        pending.secret, remote: remote, joiner: remote,
        host: pending.secret.publicKey.x963Representation)
      pending.key = secrets.key
      pending.code = secrets.code
      incoming = pending
    }
    return HelloReply(id: hello.id, device: device)
  }

  /// Offers pairing to nearby devices; only those holding the same group key can accept.
  private func introduce(_ known: [StoredPeer]) async {
    guard let store, let device, port > 0, known.count < 16,
      let group = try? await store.group(), let blocked = try? await store.blocked()
    else { return }
    for id in nearby.keys.sorted() where !known.contains(where: { $0.device.id == id }) {
      let now = milliseconds()
      guard !blocked.contains(id), now - (attempted[id] ?? 0) > 30_000,
        let key = try? Codec.pairKey(group: group, device.id, id)
      else { continue }
      attempted[id] = now
      let request = Request(action: "pair", device: device, hosts: localAddresses(), port: port)
      guard
        let response = try? await exchange(request, peer: id, key: key, hosts: [], port: 0),
        response.kind == "paired"
      else { continue }
      try? await adopt(response, key: key, hosts: [], port: 0, explicit: false)
    }
  }

  public func pair(_ code: String) async throws {
    let invitation = try Invitation.parse(code)
    guard let device, port > 0 else { throw CoreError(message: "Local networking is unavailable.") }
    try require(device.id != invitation.device.id, "Use this invitation on your other device.")
    let store = try storage()
    let peers = try await store.peers()
    try require(
      peers.count < 16 || peers.contains { $0.device.id == invitation.device.id },
      "You can pair up to 16 devices.")
    let request = Request(action: "pair", device: device, hosts: localAddresses(), port: port)
    let response = try await exchange(
      request, peer: invitation.device.id, key: invitation.key, hosts: invitation.hosts,
      port: invitation.port)
    try require(
      response.kind == "paired" && response.device.id == invitation.device.id,
      "Pairing was not accepted. Create a new invitation.")
    try await adopt(
      response, key: invitation.key, hosts: invitation.hosts, port: invitation.port)
  }

  public func sync() async {
    guard !syncing, let store, device != nil else { return }
    syncing = true
    defer { syncing = false }
    guard let peers = try? await store.peers() else { return }
    await withTaskGroup(of: Void.self) { tasks in
      for peer in peers {
        tasks.addTask { await self.syncPeer(peer) }
      }
      tasks.addTask { await self.introduce(peers) }
    }
  }

  private func syncPeer(_ peer: StoredPeer) async {
    guard let store else { return }
    var cursor = peer.cursor
    do {
      for _ in 0..<64 {
        try Task.checkCancellation()
        let request = Request(action: "sync", since: cursor)
        let response = try await exchange(
          request, peer: peer.device.id, key: peer.key, hosts: peer.hosts, port: peer.port)
        try require(response.kind == "change", "Unexpected sync response.")
        try await store.apply(owner: peer.device.id, identity: response.device, item: response.item)
        guard let item = response.item else { return }
        cursor = item.revision
      }
    } catch { try? await store.markOffline(peer.device.id) }
  }

  private func exchange(
    _ request: Request, peer: String, key: String, hosts: [String], port: UInt16,
    extra: NWEndpoint? = nil
  ) async throws -> Response {
    guard let device else { throw CoreError(message: "Zone is closed.") }
    let payload = try Codec.seal(request, key: key, sender: device.id, recipient: peer)
    var endpoints: [NWEndpoint] = extra.map { [$0] } ?? []
    if let discovered = discovered[serviceName(peer)] { endpoints.append(discovered) }
    if let port = NWEndpoint.Port(rawValue: port) {
      endpoints += hosts.map { .hostPort(host: NWEndpoint.Host($0), port: port) }
    }
    var lastError: Error = CoreError(message: "No address for this device.")
    for endpoint in endpoints {
      try Task.checkCancellation()
      let wire = WireConnection(NWConnection(to: endpoint, using: .tcp))
      let id = UUID()
      connections[id] = wire
      wire.start()
      do {
        let response: Response = try await deadline(5, connection: wire) {
          try await wire.send(payload)
          let data = try await wire.receive()
          let envelope = try JSONDecoder().decode(Envelope.self, from: data)
          try require(
            envelope.sender == peer && envelope.recipient == device.id, "Peer identity mismatch.")
          let response = try Codec.open(envelope, key: key, as: Response.self)
          try require(
            response.id == request.id && response.device.id == peer,
            "Response does not match this request.")
          try await wire.send(Data([1]))
          return response
        }
        wire.cancel()
        connections[id] = nil
        return response
      } catch {
        wire.cancel()
        connections[id] = nil
        lastError = error
      }
    }
    throw lastError
  }

  private func accept(_ wire: WireConnection) async {
    guard connections.count < 32, store != nil else {
      wire.cancel()
      return
    }
    let id = UUID()
    connections[id] = wire
    wire.start()
    defer {
      wire.cancel()
      connections[id] = nil
    }
    do {
      try await deadline(10, connection: wire) {
        let data = try await wire.receive(limit: 64 * 1024)
        if let hello = try? JSONDecoder().decode(Hello.self, from: data) {
          try await wire.send(JSONEncoder().encode(try await self.answer(hello)))
        } else {
          let envelope = try JSONDecoder().decode(Envelope.self, from: data)
          let (response, key) = try await self.respond(envelope)
          try await wire.send(
            Codec.seal(response, key: key, sender: envelope.recipient, recipient: envelope.sender))
        }
        let receipt = try await wire.receive(limit: 1)
        try require(receipt == Data([1]), "Transfer was not acknowledged.")
      }
    } catch {
      // Unknown, malformed, or unauthenticated clients receive no content.
    }
  }

  private func respond(_ envelope: Envelope) async throws -> (Response, String) {
    guard let device, envelope.recipient == device.id else {
      throw CoreError(message: "Wrong recipient.")
    }
    let store = try storage()
    if let peer = try await store.peer(envelope.sender),
      let request = try? Codec.open(envelope, key: peer.key, as: Request.self),
      request.action == "sync"
    {
      try require(UUID(uuidString: request.id) != nil, "Invalid request.")
      let item = try await store.nextChange(after: request.since ?? -1)
      return (Response(id: request.id, kind: "change", device: device, item: item), peer.key)
    }
    let group = try await store.group()
    // A device in the same group pairs without asking, unless it was forgotten here.
    if let key = try? Codec.pairKey(group: group, device.id, envelope.sender),
      let request = try? Codec.open(envelope, key: key, as: Request.self),
      request.action == "pair", UUID(uuidString: request.id) != nil,
      let remote = request.device, remote.id == envelope.sender, let hosts = request.hosts,
      let port = request.port
    {
      try await store.pair(device: remote, key: key, hosts: hosts, port: port, explicit: false)
      return (
        Response(
          id: request.id, kind: "paired", device: device, hosts: localAddresses(), port: self.port),
        key
      )
    }
    guard let invitation, invitation.expires > milliseconds() else {
      throw CoreError(message: "No active invitation.")
    }
    let request = try Codec.open(envelope, key: invitation.key, as: Request.self)
    try require(admit == nil || admit == envelope.sender, "This approval is for another device.")
    try require(
      request.action == "pair" && UUID(uuidString: request.id) != nil, "Invalid pairing request.")
    guard let remote = request.device, remote.id == envelope.sender, let hosts = request.hosts,
      let port = request.port
    else { throw CoreError(message: "Invalid device details.") }
    self.invitation = nil
    admit = nil
    try await store.pair(device: remote, key: invitation.key, hosts: hosts, port: port)
    return (
      Response(
        id: request.id, kind: "paired", device: device, hosts: localAddresses(), port: self.port,
        group: group),
      invitation.key
    )
  }

  private func startBrowser() {
    let browser = NWBrowser(
      for: .bonjourWithTXTRecord(type: serviceType, domain: "local."), using: .tcp)
    self.browser = browser
    browser.browseResultsChangedHandler = { [weak self] results, _ in
      var endpoints: [String: NWEndpoint] = [:]
      var names: [String: String] = [:]
      for result in results {
        guard case .service(let name, _, _, _) = result.endpoint else { continue }
        endpoints[name] = result.endpoint
        if case .bonjour(let record) = result.metadata, let id = record["id"],
          serviceName(id) == name
        {
          names[id] = String((record["name"] ?? "Zone device").prefix(40))
        }
      }
      let (found, named) = (endpoints, names)
      Task { await self?.discoveryChanged(found, named) }
    }
    browser.stateUpdateHandler = { [weak self] state in
      if case .failed(let error) = state {
        Task { await self?.discoveryFailed(error.localizedDescription) }
      }
    }
    browser.start(queue: queue)
  }

  private func discoveryChanged(_ endpoints: [String: NWEndpoint], _ names: [String: String]) {
    discovered = endpoints
    nearby = names.filter { $0.key != device?.id }
  }

  /// Test seam: where this engine listens, and a way to make another engine "nearby".
  func endpoint() -> NWEndpoint? {
    NWEndpoint.Port(rawValue: port).map { .hostPort(host: "127.0.0.1", port: $0) }
  }
  func discover(_ id: String, name: String, at endpoint: NWEndpoint) {
    discovered[serviceName(id)] = endpoint
    nearby[id] = name
    attempted[id] = nil
  }
  func groupKey() async throws -> String { try await storage().group() }
  private func discoveryFailed(_ message: String) {
    discoveryError = "Nearby discovery is unavailable: \(message)"
  }
}

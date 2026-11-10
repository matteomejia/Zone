using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Zone.Core;

/// Nearby pairing and owner-only board replication. Group introductions are pending.
public sealed partial class PairingSession : IAsyncDisposable
{
  readonly PairingStore store;
  readonly object gate = new();
  readonly TimeProvider clock;
  readonly TcpListener listener = new(IPAddress.IPv6Any, 0);
  readonly CancellationTokenSource shutdown = new();
  readonly SemaphoreSlim capacity = new(32);
  readonly ConcurrentDictionary<long, Task> connections = new();
  readonly Dictionary<string, DiscoveredService> nearby = new(StringComparer.Ordinal);
  readonly Channel<PairingEvent> events = Channel.CreateUnbounded<PairingEvent>();
  readonly Task acceptLoop;
  Discovery? discovery;
  Task? discoveryLoop;
  Incoming? incoming;
  Approved? approved;
  Outgoing? outgoing;
  bool accepting;
  long connectionId;
  int disposed;
  public ushort Port { get; }
  public Device Device => store.Device;
  public ChannelReader<PairingEvent> Events => events.Reader;

  sealed class Incoming(string id, Device device, string commit, DateTimeOffset expires) : IDisposable
  {
    public string Id = id;
    public Device Device = device;
    public string Commit = commit;
    public DateTimeOffset Expires = expires;
    public ECDiffieHellman Secret = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public string? Key, Code;
    public void Dispose() => Secret.Dispose();
  }
  sealed record Approved(Device Device, string Key, DateTimeOffset Expires);
  sealed record Outgoing(string Id, Device Device, string Key, string Public, IPEndPoint Endpoint,
    DateTimeOffset Expires, CancellationTokenSource Cancellation)
  {
    public bool Ready, Completing;
  }

  internal PairingSession(PairingStore store, TimeProvider? clock = null)
  {
    this.store = store;
    this.clock = clock ?? TimeProvider.System;
    listener.Server.DualMode = true;
    listener.Start();
    Port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
    acceptLoop = AcceptLoop();
  }

  public static async Task<PairingSession> StartAsync(PairingStore store, bool discover = true, bool synchronize = false)
  {
    var session = new PairingSession(store);
    try
    {
      if (discover)
      {
        session.discovery = await Discovery.StartAsync(store.Device, session.Port);
        session.discoveryLoop = session.WatchDiscovery();
      }
      if (synchronize) session.poller = session.Poll();
      return session;
    }
    catch { await session.DisposeAsync(); throw; }
  }

  async Task WatchDiscovery()
  {
    try
    {
      await foreach (var update in discovery!.Events.ReadAllAsync(shutdown.Token))
      {
        lock (gate)
        {
          if (update.Service is { } service)
          {
            nearby[service.Id] = service;
            Publish(new("nearby", service.Name, Message: service.Id));
          }
          if (update.RemovedService is { } removed)
            foreach (var id in nearby.Where(p => p.Value.ServiceName.TrimEnd('.').Equals(removed.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray())
            {
              nearby.Remove(id);
              Publish(new("removed", "Device", Message: id));
            }
        }
        if (update.Error is { } error) Publish(new("error", "Discovery", Message: error));
      }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
  }

  public IReadOnlyList<Nearby> Nearby { get { lock (gate) return nearby.Values.Select(s => new Nearby(s.Id, s.Name)).ToArray(); } }
  public void OpenPairing() { lock (gate) { Check.Require(disposed == 0, "Zone is closed."); accepting = true; } }
  public void ClosePairing()
  {
    lock (gate)
    {
      accepting = false;
      incoming?.Dispose(); incoming = null;
      approved = null;
      outgoing?.Cancellation.Cancel();
      if (outgoing is { Ready: true, Completing: false }) outgoing.Cancellation.Dispose();
      outgoing = null;
    }
  }
  public void Approve()
  {
    lock (gate)
    {
      Check.Require(accepting && incoming is { Key: not null } && clock.GetUtcNow() < incoming.Expires,
        "That request is no longer waiting.");
      approved = new(incoming!.Device, incoming.Key!, clock.GetUtcNow().AddSeconds(30));
      incoming.Dispose(); incoming = null;
    }
  }
  public void Decline() { lock (gate) { incoming?.Dispose(); incoming = null; approved = null; } }

  public async Task<string> BeginJoinAsync(string id, CancellationToken cancellationToken = default)
  {
    DiscoveredService service;
    lock (gate) service = nearby.GetValueOrDefault(id) ?? throw new CoreException("That device is no longer nearby.");
    // Resolved numeric addresses only; never resolve an arbitrary discovered hostname.
    Exception? last = null;
    foreach (var address in service.Addresses.OrderBy(a => a.Contains(':')))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!IPAddress.TryParse(address, out var ip)) continue;
      try { return await BeginJoinAtAsync(id, new IPEndPoint(ip, service.Port), cancellationToken); }
      catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { last = error; }
    }
    throw new CoreException(last?.Message ?? "No reachable local address for this device.");
  }

  internal async Task<string> BeginJoinAtAsync(string id, IPEndPoint endpoint, CancellationToken cancellationToken = default)
  {
    using var secret = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var own = Codec.PublicKey(secret);
    var joinId = Check.NewId();
    Outgoing pending;
    lock (gate)
    {
      Check.Require(accepting && outgoing is null && incoming is null && approved is null && id != Device.Id, "Open pairing and finish any pending request first.");
      var cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellationToken);
      pending = new(joinId, new Device { Id = id, Name = "Joining", Platform = "" }, "", Convert.ToBase64String(own), endpoint, clock.GetUtcNow().AddMinutes(2), cancel);
      outgoing = pending;
    }
    try
    {
      var reply = await Greet(new(1, joinId, "join", Device, Codec.Commitment(own)), pending, pending.Cancellation.Token);
      var remote = Check.Base64(reply.Public) ?? throw new CoreException("Invalid host public key.");
      var secrets = Codec.JoinSecrets(secret, remote, own, remote);
      pending = pending with { Device = reply.Device, Key = secrets.Key };
      await Greet(new(1, joinId, "reveal", Public: pending.Public), pending, pending.Cancellation.Token);
      lock (gate)
      {
        pending.Cancellation.Token.ThrowIfCancellationRequested();
        Check.Require(outgoing?.Id == joinId && accepting, "Pairing was closed.");
        pending.Ready = true;
        outgoing = pending;
      }
      return secrets.Code;
    }
    catch
    {
      lock (gate) { if (outgoing?.Id == joinId) outgoing = null; }
      pending.Cancellation.Dispose();
      throw;
    }
  }

  public async Task CompleteJoinAsync(CancellationToken cancellationToken = default)
  {
    Outgoing pending;
    lock (gate)
    {
      pending = outgoing is { Ready: true, Completing: false } value ? value : throw new CoreException("No join is waiting, or it is already being completed.");
      pending.Completing = true;
    }
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(pending.Cancellation.Token, cancellationToken);
    try
    {
      while (clock.GetUtcNow() < pending.Expires)
      {
        linked.Token.ThrowIfCancellationRequested();
        if (await Claim(pending, linked.Token)) return;
        await Task.Delay(TimeSpan.FromSeconds(1), linked.Token);
        try { await Greet(new(1, pending.Id, "reveal", Public: pending.Public), pending, linked.Token); }
        catch (Exception) when (!linked.IsCancellationRequested)
        {
          // Approval may have landed between the unsuccessful pair and reveal.
          if (await Claim(pending, linked.Token)) return;
          throw new CoreException("The other device declined or stopped waiting.");
        }
      }
      throw new CoreException("The other device did not approve within two minutes.");
    }
    finally
    {
      lock (gate) { if (outgoing?.Id == pending.Id) outgoing = null; }
      pending.Cancellation.Dispose();
    }
  }

  async Task<bool> Claim(Outgoing pending, CancellationToken cancellationToken)
  {
    var request = new PairRequest(Check.NewId(), "pair", Device, LocalNetwork.Addresses(), Port);
    PairReply response;
    try
    {
      response = await Exchange(pending.Endpoint, Codec.Seal(request, pending.Key, Device.Id, pending.Device.Id), bytes =>
      {
        var reply = Codec.Open<PairReply>(Json.Parse<Envelope>(bytes), pending.Key, Device.Id, pending.Device.Id);
        Check.Require(reply.Id == request.Id && reply.Kind == "paired" && reply.Device?.Id == pending.Device.Id, "Pairing reply does not match this request.");
        reply.Device!.Validate();
        LocalNetwork.Validate(reply.Hosts, reply.Port);
        Check.Require(Check.Base64(reply.Group) is { Length: 32 }, "Missing or invalid group key.");
        return reply;
      }, cancellationToken);
    }
    catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is IOException or SocketException or OperationCanceledException) { return false; }
    lock (gate)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Check.Require(accepting && outgoing?.Id == pending.Id, "Pairing was closed.");
      store.Pair(response.Device, pending.Key, response.Hosts, response.Port, response.Group);
    }
    Publish(new("paired", response.Device.Name));
    return true;
  }

  Task<HelloReply> Greet(Hello hello, Outgoing pending, CancellationToken cancellationToken) => Exchange(pending.Endpoint, Json.Bytes(hello), bytes =>
  {
    var reply = Json.Parse<HelloReply>(bytes);
    Check.Require(reply.Id == hello.Id && reply.Device?.Id == pending.Device.Id, "Join reply identity does not match.");
    reply.Device!.Validate();
    return reply;
  }, cancellationToken);

  internal static async Task<T> Exchange<T>(IPEndPoint endpoint, byte[] payload, Func<byte[], T> validate, CancellationToken cancellationToken, int responseLimit = Framing.MaximumRequest)
  {
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(responseLimit > Framing.MaximumRequest ? 10 : 5));
    using var client = new TcpClient(endpoint.AddressFamily);
    await client.ConnectAsync(endpoint, deadline.Token);
    var stream = client.GetStream();
    await Framing.SendAsync(stream, payload, deadline.Token);
    var result = validate(await Framing.ReceiveAsync(stream, responseLimit, deadline.Token));
    await Framing.SendAsync(stream, new byte[] { 1 }, deadline.Token);
    return result;
  }

  async Task AcceptLoop()
  {
    try
    {
      while (!shutdown.IsCancellationRequested)
      {
        var client = await listener.AcceptTcpClientAsync(shutdown.Token);
        if (!capacity.Wait(0)) { client.Dispose(); continue; }
        var id = Interlocked.Increment(ref connectionId);
        connections[id] = Handle(client);
        foreach (var completed in connections.Where(p => p.Value.IsCompleted).Select(p => p.Key)) connections.TryRemove(completed, out _);
      }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    catch (SocketException) when (shutdown.IsCancellationRequested) { }
  }

  async Task Handle(TcpClient client)
  {
    using (client)
    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
    {
      deadline.CancelAfter(TimeSpan.FromSeconds(10));
      try
      {
        var stream = client.GetStream();
        var bytes = await Framing.ReceiveAsync(stream, Framing.MaximumRequest, deadline.Token);
        byte[] response;
        using var json = JsonDocument.Parse(bytes);
        lock (gate)
        {
          response = json.RootElement.TryGetProperty("action", out _)
            ? Json.Bytes(Answer(Json.Parse<Hello>(bytes))) : Respond(Json.Parse<Envelope>(bytes));
        }
        await Framing.SendAsync(stream, response, deadline.Token);
        await Framing.ReceiveReceiptAsync(stream, deadline.Token);
      }
      catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Pairing connection closed: {error.Message}"); }
      finally { capacity.Release(); }
    }
  }

  internal HelloReply Answer(Hello hello)
  {
    // Caller holds gate (tests use this seam without networking).
    Check.Require(accepting && hello.Version == 1 && Check.IsUuid(hello.Id), "Not accepting this join.");
    if (incoming is not null && clock.GetUtcNow() >= incoming.Expires) { incoming.Dispose(); incoming = null; }
    if (approved is not null && clock.GetUtcNow() >= approved.Expires) approved = null;
    if (hello.Action == "join")
    {
      var remote = hello.Device ?? throw new CoreException("Missing joining device.");
      remote.Validate();
      Check.Require(remote.Id != Device.Id && Check.Base64(hello.Commit) is { Length: 32 }, "Invalid join commitment.");
      Check.Require(incoming is null && approved is null && outgoing is null, "Another request is waiting.");
      incoming = new(hello.Id, remote, hello.Commit!, clock.GetUtcNow().AddMinutes(2));
      return new(hello.Id, Device, Convert.ToBase64String(Codec.PublicKey(incoming.Secret)));
    }
    var pending = incoming ?? throw new CoreException("No such join request.");
    Check.Require(hello.Action == "reveal" && pending.Id == hello.Id, "No such join request.");
    var key = Check.Base64(hello.Public) ?? throw new CoreException("Invalid revealed key.");
    Check.Require(Codec.Commitment(key) == pending.Commit, "Revealed key does not match the commitment.");
    if (pending.Code is null)
    {
      var secrets = Codec.JoinSecrets(pending.Secret, key, key, Codec.PublicKey(pending.Secret));
      pending.Key = secrets.Key; pending.Code = secrets.Code;
      Publish(new("request", pending.Device.Name, secrets.Code));
    }
    return new(hello.Id, Device);
  }

  internal byte[] Respond(Envelope envelope)
  {
    var peer = envelope.Sender is null ? null : store.Peer(envelope.Sender);
    if (peer is not null)
    {
      SyncRequest? sync = null;
      try { sync = Codec.Open<SyncRequest>(envelope, peer.Key, Device.Id, peer.Device.Id); }
      catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or CoreException) { }
      if (sync is { Action: "sync" })
      {
        Check.Require(Check.IsUuid(sync.Id) && sync.Since is >= 0, "Invalid sync request.");
        return Codec.Seal(new SyncReply(sync.Id, "change", Device, store.NextChange(sync.Since!.Value)), peer.Key, Device.Id, peer.Device.Id);
      }
    }
    var approval = approved ?? throw new CoreException("No approved join.");
    Check.Require(accepting && clock.GetUtcNow() < approval.Expires && envelope.Sender == approval.Device.Id, "This approval is expired or for another device.");
    var request = Codec.Open<PairRequest>(envelope, approval.Key, Device.Id, approval.Device.Id);
    Check.Require(Check.IsUuid(request.Id) && request.Action == "pair" && request.Device?.Id == envelope.Sender, "Invalid pair request.");
    request.Device!.Validate();
    LocalNetwork.Validate(request.Hosts, request.Port);
    approved = null; // Consume once, before committing or sending any reply.
    store.Pair(request.Device, approval.Key, request.Hosts, request.Port);
    var response = new PairReply(request.Id, "paired", Device, LocalNetwork.Addresses(), Port, store.Group);
    Publish(new("paired", request.Device.Name));
    return Codec.Seal(response, approval.Key, Device.Id, request.Device.Id);
  }

  void Publish(PairingEvent update) => events.Writer.TryWrite(update);
  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    ClosePairing(); shutdown.Cancel(); listener.Stop();
    if (poller is not null) await poller;
    await acceptLoop;
    await Task.WhenAll(connections.Values);
    if (discovery is not null) await discovery.DisposeAsync();
    if (discoveryLoop is not null) await discoveryLoop;
    events.Writer.TryComplete(); capacity.Dispose(); shutdown.Dispose();
  }
}

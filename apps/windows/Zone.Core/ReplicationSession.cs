using System.Net;

namespace Zone.Core;

public sealed partial class PairingSession
{
  readonly SemaphoreSlim syncGate = new(1);
  Task? poller;

  async Task Poll()
  {
    try
    {
      while (!shutdown.IsCancellationRequested)
      {
        await SyncOnceAsync(shutdown.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), shutdown.Token);
      }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
  }

  public async Task SyncOnceAsync(CancellationToken cancellationToken = default)
  {
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
    if (!await syncGate.WaitAsync(0, linked.Token)) return;
    try
    {
      var peers = store.Peers.Select(p => store.Peer(p.Id)).OfType<StoredPeer>().ToArray();
      await Task.WhenAll(peers.Select(p => SyncPeer(p, linked.Token)));
    }
    finally { syncGate.Release(); }
  }

  async Task SyncPeer(StoredPeer peer, CancellationToken cancellationToken)
  {
    var before = peer.Summary().Online;
    var changed = false;
    try
    {
      var endpoints = new List<IPEndPoint>();
      lock (gate)
        if (nearby.TryGetValue(peer.Device.Id, out var service))
          foreach (var host in service.Addresses)
            if (IPAddress.TryParse(host, out var address)) endpoints.Add(new(address, service.Port));
      endpoints.AddRange(peer.Hosts.Select(h => new IPEndPoint(IPAddress.Parse(h), peer.Port)));
      endpoints = endpoints.Distinct().OrderBy(e => e.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).ToList();
      var cursor = peer.Cursor;
      IPEndPoint? working = null;
      for (var i = 0; i < 64; i++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new SyncRequest(Check.NewId(), "sync", cursor);
        var payload = Codec.Seal(request, peer.Key, Device.Id, peer.Device.Id);
        SyncReply? response = null;
        Exception? lastError = null;
        foreach (var endpoint in working is null ? endpoints : new List<IPEndPoint> { working })
        {
          try
          {
            response = await Exchange(endpoint, payload, bytes =>
            {
              var reply = Codec.Open<SyncReply>(Json.Parse<Envelope>(bytes), peer.Key, Device.Id, peer.Device.Id);
              Check.Require(reply.Id == request.Id && reply.Kind == "change" && reply.Device?.Id == peer.Device.Id,
                "Sync response identity or request does not match.");
              reply.Device!.Validate(); reply.Item?.Validate();
              return reply;
            }, cancellationToken, Framing.MaximumFrame);
            working = endpoint;
            break;
          }
          catch (Exception error) when (!cancellationToken.IsCancellationRequested) { lastError = error; }
        }
        if (response is null) throw lastError ?? new CoreException("No reachable address for this device.");
        changed |= store.ApplyRemote(peer.Device.Id, peer.Key, response.Device, response.Item);
        if (response.Item is null) break;
        cursor = response.Item.Revision;
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (Exception error) { store.MarkOffline(peer.Device.Id, peer.Key, error.Message); }
    var after = store.Peer(peer.Device.Id)?.Summary().Online ?? false;
    if (changed) Publish(new("changes", peer.Device.Name, Message: peer.Device.Id));
    if (before != after) Publish(new("status", peer.Device.Name, Message: peer.Device.Id));
  }

  internal void DiscoverAt(string id, IPEndPoint endpoint)
  {
    lock (gate) nearby[id] = new(id, "Test device", Discovery.InstanceName(id) + "." + Discovery.ServiceType,
      "test.local", (ushort)endpoint.Port, [endpoint.Address.ToString()]);
  }
}

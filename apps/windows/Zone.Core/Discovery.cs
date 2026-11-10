using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Zone.Core;

public sealed record DiscoveredService(string Id, string Name, string ServiceName, string Host, ushort Port, IReadOnlyList<string> Addresses);
public sealed record DiscoveryEvent(DiscoveredService? Service, string? RemovedService, string? Error, bool Updated = false);

/// DNS-SD supplies untrusted nearby addresses, never pairing authorization.
public sealed class Discovery : IAsyncDisposable
{
  public const string ServiceType = "_zone-pad._tcp.local.";
  readonly Channel<(string Name, bool Removed)> names = Channel.CreateBounded<(string, bool)>(
    new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
  readonly Channel<DiscoveryEvent> events = Channel.CreateUnbounded<DiscoveryEvent>();
  readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
  readonly object pendingGate = new();
  readonly Dictionary<string, bool> pending = new(StringComparer.OrdinalIgnoreCase);
  readonly Device device;
  readonly NativeDns.Callback browseCallback;
  readonly CancellationTokenSource stopping = new();
  IntPtr instance, browseRequest, browseName, browseCancel;
  GCHandle browseRoot;
  Task? worker;
  int disposed;
  bool browsing, registered;

  Discovery(Device device) { this.device = device; browseCallback = OnBrowse; }
  public ChannelReader<DiscoveryEvent> Events => events.Reader;
  public static string InstanceName(string id) => "zone-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16)).ToLowerInvariant();

  public static async Task<Discovery> StartAsync(Device device, ushort port)
  {
    device.Validate();
    Check.Require(port != 0, "Discovery requires a listening TCP port.");
    var discovery = new Discovery(device);
    try { await discovery.Start(port); return discovery; }
    catch { await discovery.DisposeAsync(); throw; }
  }

  async Task Start(ushort port)
  {
    instance = NativeDns.DnsServiceConstructInstance(InstanceName(device.Id) + "." + ServiceType,
      Dns.GetHostName().Split('.')[0] + ".local.", IntPtr.Zero, IntPtr.Zero, port, 0, 0, 2,
      ["id", "name"], [device.Id, device.Name]);
    Check.Require(instance != IntPtr.Zero, "Windows could not construct the DNS-SD advertisement.");
    await Register(false);
    registered = true;
    // dnsapi's browse query uses "_service._tcp.local". On Windows 11 a final
    // dot here silently yields no results, although registration accepts it.
    browseName = Marshal.StringToHGlobalUni(ServiceType.TrimEnd('.'));
    browseCancel = NativeDns.Allocate(IntPtr.Zero);
    browseRequest = NativeDns.Allocate(new NativeDns.Query { Version = 1, Name = browseName,
      Callback = Marshal.GetFunctionPointerForDelegate(browseCallback) });
    browseRoot = GCHandle.Alloc(this);
    var status = NativeDns.DnsServiceBrowse(browseRequest, browseCancel);
    if (status != NativeDns.Pending) { browseRoot.Free(); throw NativeDns.Error(status); }
    browsing = true;
    worker = ProcessNames();
  }

  async Task Register(bool remove)
  {
    var operation = new NativeDns.Operation<bool>(_ => true);
    var request = operation.Keep(NativeDns.Allocate(new NativeDns.Registration
      { Version = 1, Instance = instance, Callback = operation.CallbackPointer }));
    // Registration cancellation is optional; the operation owns the request until completion.
    operation.Started(remove ? NativeDns.DnsServiceDeRegister(request, IntPtr.Zero) : NativeDns.DnsServiceRegister(request, IntPtr.Zero));
    await operation.Task;
  }

  void OnBrowse(int status, IntPtr context, IntPtr records)
  {
    System.Diagnostics.Trace.WriteLine($"DNS browse status={status} records={records}");
    try
    {
      if (status == NativeDns.Cancelled) { stopped.TrySetResult(); return; }
      if (status != 0) { events.Writer.TryWrite(new(null, null, NativeDns.Error(status).Message)); return; }
      for (var pointer = records; pointer != IntPtr.Zero;)
      {
        var record = Marshal.PtrToStructure<NativeDns.Record>(pointer);
        System.Diagnostics.Trace.WriteLine($"DNS record type={record.Type} name={Marshal.PtrToStringUni(record.Name)} ttl={record.Ttl}");
        pointer = record.Next;
        if (record.Type != 12) continue;
        var name = Marshal.PtrToStringUni(record.Target);
        if (name is not null && name.TrimEnd('.').EndsWith("." + ServiceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
          QueueName(name, record.Ttl == 0);
      }
    }
    catch (Exception error) { events.Writer.TryWrite(new(null, null, error.Message)); }
    finally { if (records != IntPtr.Zero) NativeDns.DnsRecordListFree(records, 1); }
  }

  async Task ProcessNames()
  {
    var cache = new DiscoveryCache(device.Id, ResolveWithDeadline, update => events.Writer.TryWrite(update));
    try
    {
      await foreach (var (name, removed) in names.Reader.ReadAllAsync(stopping.Token))
      {
        bool latestRemoved;
        lock (pendingGate)
        {
          if (!pending.Remove(name, out latestRemoved)) continue;
        }
        await cache.RefreshAsync(name, latestRemoved, stopping.Token);
      }
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
  }

  void QueueName(string name, bool removed)
  {
    name = name.TrimEnd('.');
    lock (pendingGate)
    {
      if (pending.ContainsKey(name)) { pending[name] = removed; return; }
      // Keep the number of pending distinct services bounded. Repeated packets
      // update the queued state, including a goodbye received during a lookup.
      if (pending.Count >= 256) return;
      pending.Add(name, removed);
      if (!names.Writer.TryWrite((name, removed))) pending.Remove(name);
    }
  }

  async Task<DiscoveredService> ResolveWithDeadline(string name, CancellationToken cancellationToken)
  {
    // A timeout stops waiting, not the native operation's lifetime. Reuse that
    // operation on retry rather than allocating another request while it is active.
    if (!resolving.TryGetValue(name, out var task) || task.IsCompleted)
      resolving[name] = task = Resolve(name);
    try { return await task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
    finally { if (task.IsCompleted) resolving.Remove(name); }
  }

  readonly Dictionary<string, Task<DiscoveredService>> resolving = new(StringComparer.OrdinalIgnoreCase);

  static Task<DiscoveredService> Resolve(string name)
  {
    var operation = new NativeDns.Operation<DiscoveredService>(pointer =>
    {
      Check.Require(pointer != IntPtr.Zero, "Empty DNS-SD result.");
      var result = Marshal.PtrToStructure<NativeDns.Instance>(pointer);
      Check.Require(result.PropertyCount <= 128, "Too many DNS-SD properties.");
      var properties = new Dictionary<string, string>(StringComparer.Ordinal);
      for (var i = 0; i < result.PropertyCount; i++)
      {
        var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(result.Keys, i * IntPtr.Size));
        var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(result.Values, i * IntPtr.Size));
        if (key is not null && value is not null) properties[key] = value;
      }
      var id = properties.GetValueOrDefault("id", "");
      var displayName = properties.GetValueOrDefault("name", "");
      return ValidateService(name, id, displayName, Marshal.PtrToStringUni(result.Host) ?? "", result.Port,
        Addresses(result));
    });
    var queryName = operation.Keep(Marshal.StringToHGlobalUni(name));
    var request = operation.Keep(NativeDns.Allocate(new NativeDns.Query { Version = 1, Name = queryName, Callback = operation.CallbackPointer }));
    var cancel = operation.Keep(NativeDns.Allocate(IntPtr.Zero));
    operation.Started(NativeDns.DnsServiceResolve(request, cancel));
    return operation.Task;
  }

  internal static DiscoveredService ValidateService(string name, string id, string displayName, string host, ushort port, string[] addresses)
  {
    new Device { Id = id, Name = displayName, Platform = "" }.Validate();
    Check.Require(string.Equals(name.TrimEnd('.'), InstanceName(id) + "." + ServiceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase),
      "DNS-SD instance does not match its device ID.");
    Check.Require(port > 0 && host.TrimEnd('.').EndsWith(".local", StringComparison.OrdinalIgnoreCase), "Invalid local service endpoint.");
    return new(id, displayName, name, host, port, addresses);
  }

  static string[] Addresses(NativeDns.Instance instance)
  {
    var addresses = new List<string>();
    foreach (var (pointer, count) in new[] { (instance.IPv4, 4), (instance.IPv6, 16) })
    {
      if (pointer == IntPtr.Zero) continue;
      var bytes = new byte[count];
      Marshal.Copy(pointer, bytes, 0, count);
      var address = new IPAddress(bytes);
      if (address.IsIPv6LinkLocal) address.ScopeId = instance.InterfaceIndex;
      addresses.Add(address.ToString());
    }
    return addresses.ToArray();
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    stopping.Cancel();
    if (browsing)
    {
      var status = NativeDns.DnsServiceBrowseCancel(browseCancel);
      if (status != 0) throw NativeDns.Error(status); // Retain native state if cancellation failed.
      await stopped.Task;
      browseRoot.Free();
    }
    if (worker is not null) await worker;
    names.Writer.TryComplete();
    try { if (registered) await Register(true); }
    finally
    {
      if (instance != IntPtr.Zero) NativeDns.DnsServiceFreeInstance(instance);
      if (browseRequest != IntPtr.Zero) Marshal.FreeHGlobal(browseRequest);
      if (browseName != IntPtr.Zero) Marshal.FreeHGlobal(browseName);
      if (browseCancel != IntPtr.Zero) Marshal.FreeHGlobal(browseCancel);
      stopping.Dispose();
      events.Writer.TryComplete();
    }
  }
}

using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class DiscoveryCacheTests
{
  const string Own = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
  const string Remote = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
  static readonly string Name = Discovery.InstanceName(Remote) + "." + Discovery.ServiceType.TrimEnd('.');
  static DiscoveredService Mac(ushort port = 1234) => new(Remote, "MacBook", Name, "mac.local", port, ["192.168.1.2"]);
  sealed class Clock : TimeProvider
  {
    public DateTimeOffset Now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan elapsed) => Now += elapsed;
  }

  [Fact]
  public async Task RepeatedAnnouncementsResolveAndPublishOnce()
  {
    var clock = new Clock();
    var calls = 0;
    var output = new List<DiscoveryEvent>();
    var cache = new DiscoveryCache(Own, (_, _) => { calls++; return Task.FromResult(Mac()); }, output.Add, clock);
    for (var i = 0; i < 20; i++) await cache.RefreshAsync(i % 2 == 0 ? Name : Name.ToUpperInvariant() + ".", false, default);
    Assert.Equal(1, calls);
    Assert.Single(output);
    clock.Advance(DiscoveryCache.RefreshInterval);
    await cache.RefreshAsync(Name, false, default);
    Assert.Equal(2, calls);
    Assert.Single(output); // A successful refresh is not another device.
  }

  [Fact]
  public async Task AddressOrderDoesNotDuplicateButChangedPortUpdates()
  {
    var clock = new Clock();
    var service = Mac() with { Addresses = ["192.168.1.2", "fe80::1%4"] };
    var output = new List<DiscoveryEvent>();
    var cache = new DiscoveryCache(Own, (_, _) => Task.FromResult(service), output.Add, clock);
    await cache.RefreshAsync(Name, false, default);
    service = service with { Addresses = ["fe80::1%4", "192.168.1.2"], Host = "MAC.LOCAL." };
    clock.Advance(DiscoveryCache.RefreshInterval);
    await cache.RefreshAsync(Name, false, default);
    Assert.Single(output);
    service = service with { Port = 5678 };
    clock.Advance(DiscoveryCache.RefreshInterval);
    await cache.RefreshAsync(Name, false, default);
    Assert.Equal(2, output.Count);
    Assert.Equal(5678, output[1].Service!.Port);
    Assert.False(output[0].Updated);
    Assert.True(output[1].Updated);
  }

  [Fact]
  public async Task RetriesTransientTimeoutWithoutPublishingAnError()
  {
    var calls = 0;
    var output = new List<DiscoveryEvent>();
    var cache = new DiscoveryCache(Own, (_, _) => ++calls == 1
      ? Task.FromException<DiscoveredService>(new TimeoutException()) : Task.FromResult(Mac()), output.Add);
    await cache.RefreshAsync(Name, false, default);
    Assert.Equal(2, calls);
    Assert.Single(output);
    Assert.NotNull(output[0].Service);
    Assert.Null(output[0].Error);
  }

  [Fact]
  public async Task FailedRefreshKeepsDeviceAndThrottlesRepeatedErrors()
  {
    var clock = new Clock();
    var fail = false;
    var calls = 0;
    var output = new List<DiscoveryEvent>();
    var cache = new DiscoveryCache(Own, (_, _) =>
    {
      calls++;
      return fail ? Task.FromException<DiscoveredService>(new TimeoutException("Lookup timed out.")) : Task.FromResult(Mac());
    }, output.Add, clock);
    await cache.RefreshAsync(Name, false, default);
    fail = true;
    clock.Advance(DiscoveryCache.RefreshInterval);
    await cache.RefreshAsync(Name, false, default);
    await cache.RefreshAsync(Name, false, default);
    Assert.Equal(3, calls); // Initial success and two timeout attempts.
    Assert.Equal(2, output.Count);
    Assert.Contains("refresh MacBook", output[1].Error);
    clock.Advance(DiscoveryCache.FailureInterval);
    await cache.RefreshAsync(Name, false, default);
    Assert.Equal(2, output.Count); // Same persistent failure is not printed again.
    fail = false;
    clock.Advance(DiscoveryCache.FailureInterval);
    await cache.RefreshAsync(Name, false, default);
    Assert.Equal(2, output.Count); // Recovery preserves the existing device.
    await cache.RefreshAsync(Name, true, default);
    Assert.Equal(Name, output[2].RemovedService); // The cached device was retained.
    await cache.RefreshAsync(Name, false, default);
    Assert.NotNull(output[3].Service);
  }

  [Fact]
  public async Task OwnAnnouncementsAndCancellationDoNotProduceErrors()
  {
    var calls = 0;
    var output = new List<DiscoveryEvent>();
    var cache = new DiscoveryCache(Own, (_, token) =>
    {
      calls++;
      token.ThrowIfCancellationRequested();
      return Task.FromResult(Mac());
    }, output.Add);
    await cache.RefreshAsync(Discovery.InstanceName(Own) + "." + Discovery.ServiceType, false, default);
    Assert.Equal(0, calls);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RefreshAsync(Name, false, cancelled.Token));
    Assert.Empty(output);
  }
}

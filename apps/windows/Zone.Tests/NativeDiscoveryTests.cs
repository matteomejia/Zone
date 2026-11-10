using System.Net;
using System.Net.Sockets;
using Xunit;
using Zone.Core;

namespace Zone.Tests;

public sealed class NativeDiscoveryFactAttribute : FactAttribute
{
  public NativeDiscoveryFactAttribute()
  {
    if (Environment.GetEnvironmentVariable("ZONE_TEST_DISCOVERY") != "1")
      Skip = "Opt-in LAN test: set ZONE_TEST_DISCOVERY=1 to exercise native Windows DNS-SD.";
  }
}

public class NativeDiscoveryTests
{
  [NativeDiscoveryFact]
  public async Task NativeBrowseResolvesBothAdvertisements()
  {
    var a = new Device { Id = Guid.NewGuid().ToString("D"), Name = "Zone discovery test A", Platform = "windows" };
    var b = new Device { Id = Guid.NewGuid().ToString("D"), Name = "Zone discovery test B", Platform = "windows" };
    var listenerA = new TcpListener(IPAddress.Any, 0);
    var listenerB = new TcpListener(IPAddress.Any, 0);
    try
    {
      listenerA.Start();
      listenerB.Start();
      var portA = (ushort)((IPEndPoint)listenerA.LocalEndpoint).Port;
      var portB = (ushort)((IPEndPoint)listenerB.LocalEndpoint).Port;
      await using var discoveryA = await Discovery.StartAsync(a, portA);
      await using var discoveryB = await Discovery.StartAsync(b, portB);
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      async Task<DiscoveredService> Find(Discovery discovery, string id)
      {
        await foreach (var update in discovery.Events.ReadAllAsync(deadline.Token))
          if (update.Service is { } service && service.Id == id) return service;
        throw new Exception("Discovery stopped before the other device appeared.");
      }
      var found = await Task.WhenAll(Find(discoveryA, b.Id), Find(discoveryB, a.Id));
      Assert.Equal(b.Name, found[0].Name);
      Assert.Equal(portB, found[0].Port);
      Assert.Equal(a.Name, found[1].Name);
      Assert.Equal(portA, found[1].Port);
      Assert.All(found, service => Assert.NotEmpty(service.Addresses));
    }
    finally { listenerA.Stop(); listenerB.Stop(); }
  }
}

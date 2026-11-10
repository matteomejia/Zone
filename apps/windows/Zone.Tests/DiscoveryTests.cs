using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class DiscoveryTests
{
  const string Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

  [Fact]
  public void InstanceHashUsesFirstSixteenSha256Bytes()
  {
    Assert.Equal("zone-303617b9730210ef3c86c52dc2aecc4d", Discovery.InstanceName(Id));
  }

  [Fact]
  public void ValidatesTxtIdentityAndLocalEndpointBeforeListing()
  {
    var name = Discovery.InstanceName(Id) + "." + Discovery.ServiceType;
    Assert.Equal(Id, Discovery.ValidateService(name, Id, "Mac", "mac.local.", 1234, []).Id);
    Assert.Throws<CoreException>(() => Discovery.ValidateService(name, "other", "Mac", "mac.local.", 1234, []));
    Assert.Throws<CoreException>(() => Discovery.ValidateService(name, Id, "", "mac.local.", 1234, []));
    Assert.Throws<CoreException>(() => Discovery.ValidateService(name, Id, "Mac", "example.com", 1234, []));
    Assert.Throws<CoreException>(() => Discovery.ValidateService(name, Id, "Mac", "mac.local.", 0, []));
  }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Zone.Core;

internal static class LocalNetwork
{
  public static bool IsLocalHost(string? host)
  {
    if (host is null || !IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != host) return false;
    var b = address.GetAddressBytes();
    return b[0] is 10 or 127 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
  }
  public static void Validate(string[]? hosts, ushort port) => Check.Require(
    port > 0 && hosts is { Length: >= 1 and <= 16 } && hosts.All(IsLocalHost), "Invalid local addresses or TCP port.");
  public static string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString())
    .Where(a => IsLocalHost(a) && !a.StartsWith("127.", StringComparison.Ordinal))
    .Distinct().Order(StringComparer.Ordinal).Take(15).Append("127.0.0.1").ToArray();
}

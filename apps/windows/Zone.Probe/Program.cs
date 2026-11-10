using System.Net;
using System.Net.Sockets;
using Zone.Core;

if (args.Length > 0 && args[0] == "--pair") return await PairingProbe.Run(args[1..]);

if (Environment.GetEnvironmentVariable("ZONE_DISCOVERY_TRACE") == "1")
  System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));

// A disposable discovery identity, deliberately separate from the future app store.
// This listener closes every connection; no pairing or content exchange is offered.
if (args.Length > 3 || args.Length == 1 && args[0] is "--help" or "-h")
{
  Console.WriteLine("Zone.Probe [display-name] [duration-seconds=30] [device-uuid=random]\nZone.Probe --pair [display-name] [duration-seconds=600]");
  return 0;
}
if (args.Length >= 2 && (!int.TryParse(args[1], out var seconds) || seconds < 1 || seconds > 3600))
{
  Console.Error.WriteLine("Duration must be between 1 and 3600 seconds.");
  return 1;
}
var device = new Device { Id = args.Length >= 3 ? args[2] : Guid.NewGuid().ToString("D"),
  Name = args.Length >= 1 ? args[0] : Environment.MachineName + " (Zone probe)", Platform = "windows" };
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(args.Length >= 2 ? int.Parse(args[1]) : 30));
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += cancel;
var listener = new TcpListener(IPAddress.IPv6Any, 0);
listener.Server.DualMode = true;
Task? accept = null;
try
{
  listener.Start();
  var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
  accept = CloseConnections();
  await using var discovery = await Discovery.StartAsync(device, port);
  Console.WriteLine($"Advertising {device.Name}: {Discovery.InstanceName(device.Id)} port={port} id={device.Id}");
  Console.WriteLine("Discovery only. Open + on the Mac and look for this name. Ctrl+C stops and withdraws the service.");
  await foreach (var update in discovery.Events.ReadAllAsync(stop.Token))
  {
    if (update.Error is { } error) Console.Error.WriteLine($"Discovery error: {error}");
    if (update.RemovedService is { } removed) Console.WriteLine($"Removed {removed}");
    if (update.Service is { } service) Console.WriteLine($"{(update.Updated ? "Updated" : "Nearby")} {service.Name} id={service.Id} host={service.Host} port={service.Port} addresses={string.Join(",", service.Addresses)}");
  }
  return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
finally
{
  stop.Cancel();
  listener.Stop();
  if (accept is not null) await accept;
  Console.CancelKeyPress -= cancel;
}

async Task CloseConnections()
{
  try
  {
    while (!stop.IsCancellationRequested)
    {
      using var client = await listener.AcceptTcpClientAsync(stop.Token);
    }
  }
  catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
  catch (SocketException) when (stop.IsCancellationRequested) { }
}

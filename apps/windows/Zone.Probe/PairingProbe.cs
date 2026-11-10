using Zone.Core;

internal static class PairingProbe
{
  public static async Task<int> Run(string[] args)
  {
    if (args.Length > 2 || args.Length >= 2 && (!int.TryParse(args[1], out var duration) || duration is < 1 or > 3600))
    { Console.Error.WriteLine("Usage: Zone.Probe --pair [display-name] [duration-seconds=600, max 3600]"); return 1; }
    var name = args.Length > 0 ? args[0] : Environment.MachineName;
    var directory = Environment.GetEnvironmentVariable("ZONE_DATA_DIR") ??
      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zone", "PairingProbe");
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(args.Length >= 2 ? int.Parse(args[1]) : 600));
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
      using var store = new PairingStore(directory, name);
      await using var session = await PairingSession.StartAsync(store);
      session.OpenPairing();
      var gate = new object();
      var ids = new List<string>();
      Task joinWork = Task.CompletedTask;
      Console.WriteLine($"Pairing as {session.Device.Name} ({session.Device.Id}), TCP {session.Port}");
      Console.WriteLine($"Saved identity and keys: {Path.GetFullPath(directory)}");
      Console.WriteLine("Pairing is open. Open + on the Mac. Compare codes before approving.");
      Console.WriteLine("Commands: list, connect <number>, approve, decline, close, open, peers, quit");
      Console.WriteLine("This pairing-only probe saves keys. Use the desktop app to sync notes.");
      var watcher = PrintEvents();
      try
      {
        while (!stop.IsCancellationRequested)
        {
          var line = await Console.In.ReadLineAsync(stop.Token);
          if (line is null) break;
          var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
          if (parts.Length == 0) continue;
          try
          {
            switch (parts[0].ToLowerInvariant())
            {
              case "list":
                foreach (var peer in session.Nearby) ShowNearby(peer);
                break;
              case "connect":
                if (!joinWork.IsCompleted) throw new CoreException("A join is already waiting. Use close to cancel it.");
                string id;
                lock (gate)
                {
                  if (parts.Length < 2 || !int.TryParse(parts[1], out var number) || number < 1 || number > ids.Count)
                    throw new CoreException("Use list, then connect followed by the device number.");
                  id = ids[number - 1];
                }
                joinWork = Join(id);
                break;
              case "approve": session.Approve(); Console.WriteLine("Approved. Waiting for the other device to claim the pair."); break;
              case "decline": session.Decline(); Console.WriteLine("Declined."); break;
              case "close": session.ClosePairing(); Console.WriteLine("Pairing closed; pending joins and approvals revoked."); break;
              case "open": session.OpenPairing(); Console.WriteLine("Pairing open."); break;
              case "peers":
                foreach (var peer in store.Peers) Console.WriteLine($"Saved pair: {peer.Name} ({peer.Id})");
                if (store.Peers.Count == 0) Console.WriteLine("No saved pairs.");
                break;
              case "quit": stop.Cancel(); break;
              default: Console.WriteLine("Commands: list, connect <number>, approve, decline, close, open, peers, quit"); break;
            }
          }
          catch (Exception error) { Console.Error.WriteLine(error.Message); }
        }
      }
      catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
      finally
      {
        stop.Cancel(); session.ClosePairing();
        await joinWork;
        await watcher;
      }
      return 0;

      void ShowNearby(Nearby peer)
      {
        lock (gate)
        {
          var number = ids.IndexOf(peer.Id);
          if (number < 0) { number = ids.Count; ids.Add(peer.Id); }
          Console.WriteLine($"Nearby {number + 1}: {peer.Name} ({peer.Id})");
        }
      }
      async Task PrintEvents()
      {
        try
        {
          await foreach (var update in session.Events.ReadAllAsync(stop.Token))
            switch (update.Kind)
            {
              case "nearby": ShowNearby(new(update.Message!, update.Name)); break;
              case "request": Console.WriteLine($"{update.Name} requests pairing. Code: {update.Code}. If both screens match, type approve; otherwise decline."); break;
              case "paired": Console.WriteLine($"Paired with {update.Name}. Identity and pairing key saved. Open the desktop app to sync notes."); break;
              case "error": Console.Error.WriteLine($"{update.Name}: {update.Message}"); break;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
      }
      async Task Join(string id)
      {
        try
        {
          var code = await session.BeginJoinAsync(id, stop.Token);
          Console.WriteLine($"Pairing code: {code}. Compare both screens, then approve on the Mac. Waiting up to two minutes.");
          await session.CompleteJoinAsync(stop.Token);
        }
        catch (OperationCanceledException) { Console.WriteLine("Join cancelled."); }
        catch (Exception error) { Console.Error.WriteLine($"Join failed: {error.Message}"); }
      }
    }
    catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    finally { Console.CancelKeyPress -= cancel; }
  }
}

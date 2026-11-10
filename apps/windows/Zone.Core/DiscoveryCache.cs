namespace Zone.Core;

// Used by the single browse worker. Repeated mDNS announcements refresh one entry;
// they do not create devices or start another native lookup for every packet.
internal sealed class DiscoveryCache(
  string ownId,
  Func<string, CancellationToken, Task<DiscoveredService>> resolve,
  Action<DiscoveryEvent> publish,
  TimeProvider? clock = null)
{
  readonly TimeProvider time = clock ?? TimeProvider.System;
  readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
  internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
  internal static readonly TimeSpan FailureInterval = TimeSpan.FromSeconds(5);
  sealed record Entry(DiscoveredService? Service, DateTimeOffset NextAttempt, string? Error);

  public async Task RefreshAsync(string name, bool removed, CancellationToken cancellationToken)
  {
    name = name.TrimEnd('.');
    if (name.Equals(Discovery.InstanceName(ownId) + "." + Discovery.ServiceType.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) return;
    entries.TryGetValue(name, out var previous);
    if (removed)
    {
      entries.Remove(name);
      if (previous?.Service is not null) publish(new(null, name, null));
      return;
    }
    if (previous is not null && time.GetUtcNow() < previous.NextAttempt) return;
    try
    {
      DiscoveredService service;
      try { service = await resolve(name, cancellationToken); }
      catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
      {
        // One transient lookup failure is not a browse failure. Retry once, then
        // retain the previous entry and report a service-specific refresh error.
        service = await resolve(name, cancellationToken);
      }
      cancellationToken.ThrowIfCancellationRequested();
      entries[name] = new(service, time.GetUtcNow() + RefreshInterval, null);
      if (service.Id != ownId && (previous?.Service is not { } old || !Same(old, service)))
        publish(new(service, null, null, Updated: previous?.Service is not null));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (Exception error)
    {
      var message = $"Could not {(previous?.Service is null ? "resolve" : "refresh")} {previous?.Service?.Name ?? name}: {error.Message}";
      entries[name] = new(previous?.Service, time.GetUtcNow() + FailureInterval, message);
      if (message != previous?.Error) publish(new(null, null, message));
    }
  }

  static bool Same(DiscoveredService a, DiscoveredService b) =>
    a.Id == b.Id && a.Name == b.Name && a.Port == b.Port &&
    a.Host.TrimEnd('.').Equals(b.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
    a.Addresses.Order(StringComparer.Ordinal).SequenceEqual(b.Addresses.Order(StringComparer.Ordinal));
}

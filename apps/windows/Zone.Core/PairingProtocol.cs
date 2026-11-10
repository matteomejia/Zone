namespace Zone.Core;

internal sealed record Hello(int Version, string Id, string Action, Device? Device = null, string? Commit = null, string? Public = null);
internal sealed record HelloReply(string Id, Device Device, string? Public = null);
internal sealed record PairRequest(string Id, string Action, Device Device, string[] Hosts, ushort Port);
internal sealed record PairReply(string Id, string Kind, Device Device, string[] Hosts, ushort Port, string? Group = null);
public sealed record PairingEvent(string Kind, string Name, string? Code = null, string? Message = null);
internal sealed record SyncRequest(string Id, string Action, long? Since);
internal sealed record SyncReply(string Id, string Kind, Device Device, StoredItem? Item = null);

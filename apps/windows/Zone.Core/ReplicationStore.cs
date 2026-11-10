namespace Zone.Core;

public sealed partial class PairingStore
{
  internal StoredItem? NextChange(long since)
  {
    Check.Require(since >= 0, "Invalid replication cursor.");
    lock (gate)
    {
      using var command = Command("SELECT body FROM items WHERE owner=$owner AND revision>CAST($since AS INTEGER) ORDER BY revision LIMIT 1",
        null, ("$owner", Device.Id), ("$since", since.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      return command.ExecuteScalar() is string body ? Json.Parse<StoredItem>(body) : null;
    }
  }

  internal bool ApplyRemote(string owner, string expectedKey, Device identity, StoredItem? item)
  {
    identity.Validate();
    Check.Require(identity.Id == owner && owner != Device.Id, "Remote owner identity mismatch.");
    item?.Validate();
    lock (gate)
    {
      var peer = ReadPeer(owner) ?? throw new CoreException("Device is no longer paired.");
      Check.Require(peer.Key == expectedKey, "Pairing changed during transfer. Retry with the current key.");
      if (item is not null) Check.Require(item.Revision > peer.Cursor, "Replayed or out-of-order change.");
      var changed = item is not null || peer.Device != identity;
      using var transaction = db.BeginTransaction();
      if (item is not null) WriteItem(owner, item, transaction);
      WritePeer(peer with { Device = identity, Cursor = item?.Revision ?? peer.Cursor, LastSync = Check.Milliseconds(), Error = null }, transaction);
      transaction.Commit();
      return changed;
    }
  }

  internal void MarkOffline(string owner, string expectedKey, string error)
  {
    lock (gate)
    {
      var peer = ReadPeer(owner);
      if (peer is null || peer.Key != expectedKey) return;
      WritePeer(peer with { Error = error });
    }
  }

  void WritePeer(StoredPeer peer, Microsoft.Data.Sqlite.SqliteTransaction? transaction = null) => Execute(
    "UPDATE native_peers SET body=$body WHERE id=$id", transaction, ("$id", peer.Device.Id), ("$body", Json.Text(peer)));

  public byte[] Attachment(string owner, string id)
  {
    lock (gate)
    {
      Check.Require(owner == Device.Id || ReadPeer(owner) is not null, "Device is not paired.");
      using var command = Command("SELECT body FROM items WHERE owner=$owner AND id=$id", null, ("$owner", owner), ("$id", id));
      var body = command.ExecuteScalar() as string ?? throw new CoreException("Attachment not found.");
      var item = Json.Parse<StoredItem>(body);
      Check.Require(!item.Deleted && item.Kind is "file" or "image", "This attachment is unavailable.");
      return Convert.FromBase64String(item.Data);
    }
  }

  public string AddAttachment(string name, string mime, byte[] bytes)
  {
    Check.Require(bytes.Length <= StoredItem.MaximumFile, "Files must be 8 MiB or smaller.");
    name = SuggestedFileName(name);
    var kind = mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif" ? "image" : "file";
    lock (gate)
    {
      using var transaction = db.BeginTransaction();
      var item = new StoredItem { Id = Check.NewId(), Revision = NextRevision(transaction), CreatedAt = Check.Milliseconds(),
        Kind = kind, Text = "", Name = name, Mime = mime, Data = Convert.ToBase64String(bytes), Deleted = false };
      item.Validate(); WriteItem(item, transaction); transaction.Commit(); return item.Id;
    }
  }

  public static string SuggestedFileName(string name)
  {
    var last = (name ?? "").Replace('\\', '/').Split('/').Last();
    var safe = new string(last.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
    return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? "attachment" : safe;
  }
}

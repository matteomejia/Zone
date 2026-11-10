using Microsoft.Data.Sqlite;

namespace Zone.Core;

/// Durable identity and trust. A later sync store can extend these tables.
public sealed partial class PairingStore : IDisposable
{
  readonly object gate = new();
  readonly FileStream writerLock;
  readonly SqliteConnection db;
  public Device Device { get; }

  public PairingStore(string directory, string name)
  {
    Directory.CreateDirectory(directory);
    try { writerLock = new FileStream(Path.Combine(directory, "zone.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { throw new CoreException("Zone is already using this data directory."); }
    db = new SqliteConnection(new SqliteConnectionStringBuilder
      { DataSource = Path.Combine(directory, "zone.sqlite3"), Pooling = false }.ToString());
    try
    {
      db.Open();
      Execute("PRAGMA journal_mode=WAL");
      Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
      Execute("CREATE TABLE IF NOT EXISTS native_peers (id TEXT PRIMARY KEY, body TEXT NOT NULL)");
      Execute("CREATE TABLE IF NOT EXISTS items (owner TEXT NOT NULL, id TEXT NOT NULL, revision INTEGER NOT NULL, body TEXT NOT NULL, PRIMARY KEY(owner,id))");
      Execute("CREATE INDEX IF NOT EXISTS item_revision ON items(owner,revision)");
      var saved = Setting("device");
      Device = saved is null ? new Device { Id = Check.NewId(), Name = name, Platform = "windows" } : Json.Parse<Device>(saved);
      Device.Validate();
      using var transaction = db.BeginTransaction();
      Set("device", Json.Text(Device), transaction);
      Set("group", Setting("group", transaction) ?? Codec.RandomKey(), transaction);
      transaction.Commit();
      Check.Require(Check.Base64(Group) is { Length: 32 }, "Invalid stored group key.");
    }
    catch { db.Dispose(); writerLock.Dispose(); throw; }
  }

  internal string Group { get { lock (gate) return Setting("group") ?? throw new CoreException("Missing group key."); } }
  public IReadOnlyList<Peer> Peers { get { lock (gate) return ReadPeers().Select(p => p.Summary()).ToArray(); } }
  internal StoredPeer? Peer(string id) { lock (gate) return ReadPeer(id); }
  internal void Pair(Device remote, string key, string[] hosts, ushort port, string? group = null)
  {
    remote.Validate();
    Check.Require(remote.Id != Device.Id && Check.Base64(key) is { Length: 32 }, "Invalid pairing identity or key.");
    LocalNetwork.Validate(hosts, port);
    if (group is not null) Check.Require(Check.Base64(group) is { Length: 32 }, "Invalid group key.");
    lock (gate)
    {
      var existing = ReadPeer(remote.Id);
      Check.Require(existing is not null || ReadPeers().Count < 16, "You can pair up to 16 devices.");
      var peer = new StoredPeer { Device = remote, Key = key, Hosts = hosts, Port = port, Cursor = existing?.Cursor ?? 0 };
      using var transaction = db.BeginTransaction();
      Execute("INSERT INTO native_peers(id,body) VALUES ($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body",
        transaction, ("$id", remote.Id), ("$body", Json.Text(peer)));
      if (group is not null) Set("group", group, transaction);
      transaction.Commit();
    }
  }

  StoredPeer? ReadPeer(string id)
  {
    using var command = Command("SELECT body FROM native_peers WHERE id=$id", null, ("$id", id));
    return command.ExecuteScalar() is string body ? Json.Parse<StoredPeer>(body) : null;
  }
  List<StoredPeer> ReadPeers()
  {
    using var command = Command("SELECT body FROM native_peers ORDER BY id");
    using var rows = command.ExecuteReader();
    var peers = new List<StoredPeer>();
    while (rows.Read()) peers.Add(Json.Parse<StoredPeer>(rows.GetString(0)));
    return peers;
  }
  string? Setting(string key, SqliteTransaction? transaction = null)
  {
    using var command = Command("SELECT value FROM settings WHERE key=$key", transaction, ("$key", key));
    return command.ExecuteScalar() as string;
  }
  void Set(string key, string value, SqliteTransaction transaction) => Execute(
    "INSERT INTO settings(key,value) VALUES ($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
    transaction, ("$key", key), ("$value", value));
  SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Key, string Value)[] values)
  {
    var command = db.CreateCommand();
    command.CommandText = sql;
    command.Transaction = transaction;
    foreach (var value in values) command.Parameters.AddWithValue(value.Key, value.Value);
    return command;
  }
  void Execute(string sql, SqliteTransaction? transaction = null, params (string Key, string Value)[] values)
  { using var command = Command(sql, transaction, values); command.ExecuteNonQuery(); }
  public void Dispose() { lock (gate) { db.Dispose(); writerLock.Dispose(); } }
}

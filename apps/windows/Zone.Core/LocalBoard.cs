namespace Zone.Core;

public sealed partial class PairingStore
{
  public IReadOnlyList<Note> Notes(string owner)
  {
    lock (gate)
    {
      using var command = Command("SELECT body FROM items WHERE owner=$owner ORDER BY revision DESC", null, ("$owner", owner));
      using var rows = command.ExecuteReader();
      var notes = new List<Note>();
      while (rows.Read())
      {
        var item = Json.Parse<StoredItem>(rows.GetString(0));
        if (!item.Deleted) notes.Add(item.Summary(owner));
      }
      return notes;
    }
  }

  public string AddText(string text)
  {
    Check.Require(!string.IsNullOrWhiteSpace(text), "Type a note first.");
    lock (gate)
    {
      using var transaction = db.BeginTransaction();
      var item = new StoredItem { Id = Check.NewId(), Revision = NextRevision(transaction), CreatedAt = Check.Milliseconds(),
        Kind = "text", Text = text, Name = "", Mime = "", Data = "", Deleted = false };
      item.Validate();
      WriteItem(item, transaction);
      transaction.Commit();
      return item.Id;
    }
  }

  public void DeleteNote(string id)
  {
    lock (gate)
    {
      using var transaction = db.BeginTransaction();
      using var command = Command("SELECT body FROM items WHERE owner=$owner AND id=$id", transaction, ("$owner", Device.Id), ("$id", id));
      var body = command.ExecuteScalar() as string ?? throw new CoreException("This note is not on your board.");
      var item = Json.Parse<StoredItem>(body);
      if (item.Deleted) return;
      WriteItem(item with { Revision = NextRevision(transaction), Deleted = true, Text = "", Data = "" }, transaction);
      transaction.Commit();
    }
  }

  long NextRevision(Microsoft.Data.Sqlite.SqliteTransaction transaction)
  {
    using var command = Command("SELECT COALESCE(MAX(revision),0) FROM items WHERE owner=$owner", transaction, ("$owner", Device.Id));
    return checked((long)command.ExecuteScalar()! + 1);
  }
  void WriteItem(StoredItem item, Microsoft.Data.Sqlite.SqliteTransaction transaction) => WriteItem(Device.Id, item, transaction);
  void WriteItem(string owner, StoredItem item, Microsoft.Data.Sqlite.SqliteTransaction transaction) => Execute(
    "INSERT INTO items(owner,id,revision,body) VALUES ($owner,$id,$revision,$body) ON CONFLICT(owner,id) DO UPDATE SET revision=excluded.revision,body=excluded.body",
    transaction, ("$owner", owner), ("$id", item.Id), ("$revision", item.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$body", Json.Text(item)));
}

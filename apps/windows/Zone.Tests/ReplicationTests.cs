using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class ReplicationTests
{
  sealed class Files : IDisposable
  {
    public string Root = Path.Combine(Path.GetTempPath(), "zone-sync-" + Guid.NewGuid());
    public PairingStore Open(string name) => new(Path.Combine(Root, name), name);
    public void Dispose() => Directory.Delete(Root, true);
  }
  static void Pair(PairingStore a, PairingStore b, PairingSession sessionA, PairingSession sessionB)
  {
    var key = Codec.RandomKey();
    a.Pair(b.Device, key, ["127.0.0.1"], sessionB.Port);
    b.Pair(a.Device, key, ["127.0.0.1"], sessionA.Port);
  }

  [Fact]
  public async Task SyncsBothOwnersAttachmentsAndDeletionsWithPairingClosed()
  {
    using var files = new Files(); using var a = files.Open("A"); using var b = files.Open("B");
    await using var sa = await PairingSession.StartAsync(a, false);
    await using var sb = await PairingSession.StartAsync(b, false);
    Pair(a, b, sa, sb);
    var textA = a.AddText("Hello from A"); var textB = b.AddText("Hello from B");
    var bytes = new byte[8 * 1024 * 1024]; Random.Shared.NextBytes(bytes);
    var file = a.AddAttachment("binary.dat", "application/octet-stream", bytes);
    var image = b.AddAttachment("pixel.png", "image/png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
    await Task.WhenAll(sa.SyncOnceAsync(), sb.SyncOnceAsync());
    Assert.Equal(2, a.Notes(b.Device.Id).Count); Assert.Equal(2, b.Notes(a.Device.Id).Count);
    Assert.Equal(bytes, b.Attachment(a.Device.Id, file));
    Assert.Equal("image", a.Notes(b.Device.Id).Single(n => n.Id == image).Kind);
    Assert.Equal(b.Attachment(b.Device.Id, image), a.Attachment(b.Device.Id, image));
    Assert.True(a.Peers[0].Online); Assert.True(b.Peers[0].Online);
    Assert.Throws<CoreException>(() => a.DeleteNote(textB));
    a.DeleteNote(textA); a.DeleteNote(file);
    await sb.SyncOnceAsync();
    Assert.Empty(b.Notes(a.Device.Id));
    Assert.Throws<CoreException>(() => b.Attachment(a.Device.Id, file));
    Assert.NotNull(a.NextChange(0)); // Own tombstones remain available.
  }

  [Fact]
  public async Task OfflineCacheCursorAndDeletionRecoverAfterAddressChangeAndRestart()
  {
    using var files = new Files();
    string aId, bId, deleted;
    using (var a = files.Open("A"))
    using (var b = files.Open("B"))
    {
      await using var sa = await PairingSession.StartAsync(a, false);
      await using (var sb = await PairingSession.StartAsync(b, false))
      {
        Pair(a, b, sa, sb); aId = a.Device.Id; bId = b.Device.Id;
        deleted = b.AddText("Delete while offline");
        await sa.SyncOnceAsync();
      }
      await sa.SyncOnceAsync();
      Assert.False(a.Peers[0].Online); Assert.Single(a.Notes(bId));
      b.DeleteNote(deleted); b.AddText("Added while offline");
      await using var restarted = await PairingSession.StartAsync(b, false);
      sa.DiscoverAt(bId, new IPEndPoint(IPAddress.Loopback, restarted.Port));
      await sa.SyncOnceAsync();
      Assert.True(a.Peers[0].Online);
      Assert.Equal("Added while offline", Assert.Single(a.Notes(bId)).Text);
      Assert.Equal(3, a.Peer(bId)!.Cursor);
    }
    using var reopened = files.Open("A");
    Assert.Equal(aId, reopened.Device.Id); Assert.Equal(3, reopened.Peer(bId)!.Cursor);
    Assert.Equal("Added while offline", Assert.Single(reopened.Notes(bId)).Text);
  }

  [Fact]
  public void FailedPeerUpdateRollsBackItemAndCursorTogether()
  {
    using var files = new Files(); using var a = files.Open("A"); using var b = files.Open("B");
    var key = Codec.RandomKey(); a.Pair(b.Device, key, ["127.0.0.1"], 1234);
    b.AddText("Atomic"); var item = b.NextChange(0)!;
    using var db = new SqliteConnection($"Data Source={Path.Combine(files.Root, "A", "zone.sqlite3")};Pooling=False"); db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "CREATE TRIGGER reject_cursor BEFORE UPDATE ON native_peers BEGIN SELECT RAISE(ABORT, 'test failure'); END";
    command.ExecuteNonQuery();
    Assert.Throws<SqliteException>(() => a.ApplyRemote(b.Device.Id, key, b.Device, item));
    Assert.Empty(a.Notes(b.Device.Id)); Assert.Equal(0, a.Peer(b.Device.Id)!.Cursor);
    command.CommandText = "DROP TRIGGER reject_cursor"; command.ExecuteNonQuery();
    a.ApplyRemote(b.Device.Id, key, b.Device, item);
    Assert.Single(a.Notes(b.Device.Id)); Assert.Equal(1, a.Peer(b.Device.Id)!.Cursor);
    Assert.Throws<CoreException>(() => a.ApplyRemote(b.Device.Id, key, b.Device, item));
    Assert.Throws<CoreException>(() => a.ApplyRemote(b.Device.Id, key, a.Device, item with { Revision = 2 }));
    Assert.Throws<CoreException>(() => a.ApplyRemote(b.Device.Id, Codec.RandomKey(), b.Device, item with { Revision = 2 }));
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task InterruptedOrMismatchedResponseDoesNotAdvanceCursor(bool truncate)
  {
    using var files = new Files(); using var a = files.Open("A"); using var b = files.Open("B");
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try
    {
      var key = Codec.RandomKey();
      a.Pair(b.Device, key, ["127.0.0.1"], (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
      b.AddText("Not committed");
      var server = Task.Run(async () =>
      {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var envelope = Json.Parse<Envelope>(await Framing.ReceiveAsync(stream));
        var request = Codec.Open<SyncRequest>(envelope, key, b.Device.Id, a.Device.Id);
        var bytes = Codec.Seal(new SyncReply(truncate ? request.Id : Check.NewId(), "change", b.Device, b.NextChange(0)), key, b.Device.Id, a.Device.Id);
        if (!truncate) await Framing.SendAsync(stream, bytes);
        else
        {
          var header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
          await stream.WriteAsync(header); await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
        }
      });
      await using var session = await PairingSession.StartAsync(a, false);
      await session.SyncOnceAsync(); await server;
      Assert.Empty(a.Notes(b.Device.Id)); Assert.Equal(0, a.Peer(b.Device.Id)!.Cursor);
      Assert.False(a.Peers[0].Online);
    }
    finally { listener.Stop(); }
  }

  [Fact]
  public async Task LimitsEachPollTo64AndNeverRelaysCachedBoards()
  {
    using var files = new Files(); using var a = files.Open("A"); using var b = files.Open("B"); using var c = files.Open("C");
    await using var sa = await PairingSession.StartAsync(a, false);
    await using var sb = await PairingSession.StartAsync(b, false);
    Pair(a, b, sa, sb);
    for (var i = 0; i < 65; i++) b.AddText($"Note {i}");
    await sa.SyncOnceAsync(); Assert.Equal(64, a.Notes(b.Device.Id).Count);
    await sa.SyncOnceAsync(); Assert.Equal(65, a.Notes(b.Device.Id).Count);
    var key = Codec.RandomKey(); a.Pair(c.Device, key, ["127.0.0.1"], 1234);
    c.AddText("Third-party note"); a.ApplyRemote(c.Device.Id, key, c.Device, c.NextChange(0));
    Assert.Null(a.NextChange(0));
    Assert.Throws<CoreException>(() => a.AddAttachment("too-big", "application/octet-stream", new byte[8 * 1024 * 1024 + 1]));
    Assert.Equal("report.txt", PairingStore.SuggestedFileName("../../report.txt"));
  }

  [Fact]
  public async Task AttachmentReaderChecksTheLimitDuringReading()
  {
    using var tooLarge = new MemoryStream(new byte[AttachmentIO.MaximumBytes + 1]);
    await Assert.ThrowsAsync<CoreException>(() => AttachmentIO.ReadBoundedAsync(tooLarge));
    var bytes = new byte[AttachmentIO.MaximumBytes]; Random.Shared.NextBytes(bytes);
    using var exact = new MemoryStream(bytes);
    Assert.Equal(bytes, await AttachmentIO.ReadBoundedAsync(exact));
  }
}

using System.Net;
using System.Security.Cryptography;
using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class PairingTests
{
  sealed class Fixture : IDisposable
  {
    public readonly string Directory = Path.Combine(Path.GetTempPath(), "zone-pair-tests-" + Guid.NewGuid());
    public PairingStore Open(string name) => new(Path.Combine(Directory, name), name);
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
  }
  sealed class Clock : TimeProvider
  {
    public DateTimeOffset Now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
  }
  static IPEndPoint Endpoint(PairingSession session) => new(IPAddress.Loopback, session.Port);

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task TcpJoinMatchesCodesAndPersistsBothPeers(bool reverse)
  {
    using var files = new Fixture();
    string aId, bId, group, key;
    using (var a = files.Open("A"))
    using (var b = files.Open("B"))
    {
      await using var sessionA = await PairingSession.StartAsync(a, discover: false);
      await using var sessionB = await PairingSession.StartAsync(b, discover: false);
      var joiner = reverse ? sessionB : sessionA;
      var host = reverse ? sessionA : sessionB;
      sessionA.OpenPairing(); sessionB.OpenPairing();
      var code = await joiner.BeginJoinAtAsync(host.Device.Id, Endpoint(host));
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      var request = await host.Events.ReadAsync(deadline.Token);
      Assert.Equal("request", request.Kind);
      Assert.Equal(code, request.Code);
      Assert.Matches("^[0-9]{6}$", code);
      Assert.Empty(a.Peers); Assert.Empty(b.Peers); // Revealing is not approval.
      host.Approve();
      await joiner.CompleteJoinAsync(deadline.Token);
      aId = a.Device.Id; bId = b.Device.Id;
      Assert.Equal(bId, Assert.Single(a.Peers).Id);
      Assert.Equal(aId, Assert.Single(b.Peers).Id);
      Assert.Equal(a.Group, b.Group);
      group = a.Group;
      key = a.Peer(bId)!.Key;
      Assert.Equal(key, b.Peer(aId)!.Key);
      Assert.Null(a.Peer(bId)!.LastSync); // Pairing is not a successful sync.
    }
    using var reopenedA = files.Open("A");
    using var reopenedB = files.Open("B");
    Assert.Equal(aId, reopenedA.Device.Id);
    Assert.Equal(bId, reopenedB.Device.Id);
    Assert.Equal(group, reopenedA.Group);
    Assert.Equal(key, reopenedA.Peer(bId)!.Key);
    Assert.Equal(key, reopenedB.Peer(aId)!.Key);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task DecliningOrClosingDoesNotPair(bool close)
  {
    using var files = new Fixture();
    using var a = files.Open("A"); using var b = files.Open("B");
    await using var joiner = await PairingSession.StartAsync(a, false);
    await using var host = await PairingSession.StartAsync(b, false);
    joiner.OpenPairing(); host.OpenPairing();
    await joiner.BeginJoinAtAsync(b.Device.Id, Endpoint(host));
    if (close) { host.Approve(); host.ClosePairing(); } else host.Decline();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await Assert.ThrowsAsync<CoreException>(() => joiner.CompleteJoinAsync(deadline.Token));
    Assert.Empty(a.Peers); Assert.Empty(b.Peers);
  }

  [Fact]
  public async Task ClosedHostRejectsJoinAndClosingJoinerCancelsWait()
  {
    using var files = new Fixture();
    using var a = files.Open("A"); using var b = files.Open("B");
    await using var joiner = await PairingSession.StartAsync(a, false);
    await using var host = await PairingSession.StartAsync(b, false);
    joiner.OpenPairing();
    await Assert.ThrowsAnyAsync<IOException>(() => joiner.BeginJoinAtAsync(b.Device.Id, Endpoint(host)));
    host.OpenPairing();
    await joiner.BeginJoinAtAsync(b.Device.Id, Endpoint(host));
    var waiting = joiner.CompleteJoinAsync();
    joiner.ClosePairing();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    Assert.Empty(a.Peers); Assert.Empty(b.Peers);
  }

  static (Hello Reveal, string Key) Handshake(PairingSession host, Device joiner, ECDiffieHellman secret)
  {
    var own = Codec.PublicKey(secret);
    var id = Check.NewId();
    var response = host.Answer(new(1, id, "join", joiner, Codec.Commitment(own)));
    var remote = Convert.FromBase64String(response.Public!);
    return (new(1, id, "reveal", Public: Convert.ToBase64String(own)), Codec.JoinSecrets(secret, remote, own, remote).Key);
  }
  static Envelope Pair(PairingSession host, Device joiner, string key, string[]? hosts = null) =>
    Json.Parse<Envelope>(Codec.Seal(new PairRequest(Check.NewId(), "pair", joiner, hosts ?? ["127.0.0.1"], 1234), key, joiner.Id, host.Device.Id));

  [Fact]
  public async Task CommitmentApprovalIdentityAndSingleUseAreEnforced()
  {
    using var files = new Fixture();
    using var a = files.Open("A"); using var b = files.Open("B");
    await using var host = await PairingSession.StartAsync(b, false);
    host.OpenPairing();
    using var secret = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var (reveal, key) = Handshake(host, a.Device, secret);
    Assert.Throws<CoreException>(() => host.Answer(reveal with { Public = Convert.ToBase64String(new byte[65]) }));
    host.Answer(reveal);
    var pair = Pair(host, a.Device, key);
    Assert.Throws<CoreException>(() => host.Respond(pair)); // No approval yet.
    host.Approve();
    Assert.Throws<CoreException>(() => host.Respond(pair with { Sender = "other" }));
    Assert.Throws<CoreException>(() => host.Respond(Pair(host, a.Device, key, ["8.8.8.8"])));
    Assert.ThrowsAny<CryptographicException>(() => host.Respond(Pair(host, a.Device, Codec.RandomKey())));
    var reply = Codec.Open<PairReply>(Json.Parse<Envelope>(host.Respond(pair)), key, a.Device.Id, b.Device.Id);
    Assert.Equal("paired", reply.Kind);
    Assert.Equal(b.Group, reply.Group);
    Assert.Throws<CoreException>(() => host.Respond(pair));
    Assert.Single(b.Peers);
  }

  [Fact]
  public async Task PendingAndApprovedJoinsExpireAndCannotBeOverwritten()
  {
    using var files = new Fixture();
    using var a = files.Open("A"); using var b = files.Open("B");
    var clock = new Clock();
    await using var host = new PairingSession(b, clock);
    host.OpenPairing();
    using var secret = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var first = Handshake(host, a.Device, secret);
    Assert.Throws<CoreException>(() => Handshake(host, a.Device, secret));
    clock.Now = clock.Now.AddMinutes(2);
    Assert.Throws<CoreException>(() => host.Answer(first.Reveal));
    var second = Handshake(host, a.Device, secret);
    host.Answer(second.Reveal); host.Approve();
    clock.Now = clock.Now.AddSeconds(30);
    Assert.Throws<CoreException>(() => host.Respond(Pair(host, a.Device, second.Key)));
    Assert.Empty(b.Peers);
  }

  [Fact]
  public void StoreHasOneWriterAndInvalidGroupDoesNotCommitPeer()
  {
    using var files = new Fixture();
    using var a = files.Open("A"); using var b = files.Open("B");
    Assert.Throws<CoreException>(() => files.Open("A"));
    var original = a.Group;
    Assert.Throws<CoreException>(() => a.Pair(b.Device, Codec.RandomKey(), ["127.0.0.1"], 1234, "bad"));
    Assert.Empty(a.Peers);
    Assert.Equal(original, a.Group);
  }
}

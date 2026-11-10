using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class CodecTests
{
  static JsonElement Fixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json"))).RootElement;
  static string Field(JsonElement f, string key) => f.GetProperty(key).GetString()!;

  [Fact]
  public void SwiftEncryptionFixtureMatchesExactly()
  {
    var f = Fixture("codec");
    var e = Json.Parse<Envelope>(f.GetProperty("envelope").GetRawText());
    var plaintext = Encoding.UTF8.GetBytes(Field(f, "plaintext"));
    Assert.Equal(plaintext, Codec.OpenBytes(e, Field(f, "key"), e.Recipient, e.Sender));
    Assert.Equal(e, Codec.SealBytes(plaintext, Field(f, "key"), e.Sender, e.Recipient, Enumerable.Range(0, 12).Select(i => (byte)i).ToArray()));
  }

  [Fact]
  public void AuthenticationRejectsModifiedCiphertextNonceTagAndHeaders()
  {
    var f = Fixture("codec");
    var e = Json.Parse<Envelope>(f.GetProperty("envelope").GetRawText());
    void Reject(Envelope changed, string? key = null) => Assert.ThrowsAny<Exception>(() => Codec.OpenBytes(changed, key ?? Field(f, "key"), e.Recipient, e.Sender));
    foreach (var offset in new[] { 0, 12, Convert.FromBase64String(e.Sealed).Length - 1 })
    {
      var bytes = Convert.FromBase64String(e.Sealed);
      bytes[offset] ^= 1;
      Reject(e with { Sealed = Convert.ToBase64String(bytes) });
    }
    Reject(e with { Sender = "impostor" });
    Reject(e with { Recipient = "impostor" });
    Reject(e with { Version = 2 });
    Reject(e, Codec.RandomKey());
    Reject(e with { Sealed = "" });
    // Even when the caller expects changed identities, the GCM AAD must reject them.
    Assert.ThrowsAny<CryptographicException>(() => Codec.OpenBytes(e with { Sender = "impostor" }, Field(f, "key"), e.Recipient, "impostor"));
  }

  [Fact]
  public void BothPrivateScalarsReproduceSwiftPairingFixture()
  {
    var f = Fixture("pairing");
    ECDiffieHellman Import(string field) => ECDiffieHellman.Create(new ECParameters
      { Curve = ECCurve.NamedCurves.nistP256, D = Convert.FromBase64String(Field(f, field)) });
    using var joiner = Import("joinerPrivate");
    using var host = Import("hostPrivate");
    var j = Codec.PublicKey(joiner);
    var h = Codec.PublicKey(host);
    Assert.Equal(Field(f, "joinerPublic"), Convert.ToBase64String(j));
    Assert.Equal(Field(f, "hostPublic"), Convert.ToBase64String(h));
    Assert.Equal(Field(f, "commit"), Codec.Commitment(j));
    var expected = (Field(f, "key"), Field(f, "code"));
    Assert.Equal(expected, Codec.JoinSecrets(joiner, h, j, h));
    Assert.Equal(expected, Codec.JoinSecrets(host, j, j, h));
    Assert.Equal(Field(f, "pairKey"), Codec.PairKey(Field(f, "group"), Field(f, "deviceA"), Field(f, "deviceB")));
    Assert.Equal(Field(f, "pairKey"), Codec.PairKey(Field(f, "group"), Field(f, "deviceB"), Field(f, "deviceA")));
  }

  [Fact]
  public void ProductionMessagesUseFreshNonces()
  {
    var key = Codec.RandomKey();
    var first = Codec.Seal(new { text = "hello" }, key, "a", "b");
    var second = Codec.Seal(new { text = "hello" }, key, "a", "b");
    Assert.NotEqual(first, second);
    Assert.Equal("hello", Codec.Open<JsonElement>(Json.Parse<Envelope>(first), key, "b", "a").GetProperty("text").GetString());
  }
}

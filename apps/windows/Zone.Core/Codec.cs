using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Zone.Core;

internal sealed record Envelope(int Version, string Sender, string Recipient, string Sealed);

internal static class Codec
{
  internal const int MaximumPlaintext = 12 * 1024 * 1024;
  public static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

  static byte[] Key(string key) => Check.Base64(key) is { Length: 32 } bytes
    ? bytes : throw new CoreException("Invalid pairing key.");

  static byte[] Aad(string sender, string recipient)
  {
    Check.Require(!string.IsNullOrEmpty(sender) && !string.IsNullOrEmpty(recipient) &&
      Check.Utf8(sender) <= 128 && Check.Utf8(recipient) <= 128 &&
      !sender.Contains('\n') && !recipient.Contains('\n'), "Invalid envelope identity.");
    return Encoding.UTF8.GetBytes($"zone/1\n{sender}\n{recipient}");
  }

  public static byte[] Seal<T>(T value, string key, string sender, string recipient) =>
    Json.Bytes(SealBytes(Json.Bytes(value), key, sender, recipient, RandomNumberGenerator.GetBytes(12)));

  // Explicit nonces are internal and used only by fixture tests.
  internal static Envelope SealBytes(byte[] plaintext, string key, string sender, string recipient, byte[] nonce)
  {
    Check.Require(plaintext.Length <= MaximumPlaintext && nonce.Length == 12, "Invalid message size or nonce.");
    var combined = new byte[plaintext.Length + 28];
    nonce.CopyTo(combined, 0);
    using var aes = new AesGcm(Key(key), 16);
    aes.Encrypt(nonce, plaintext, combined.AsSpan(12, plaintext.Length), combined.AsSpan(12 + plaintext.Length), Aad(sender, recipient));
    return new(1, sender, recipient, Convert.ToBase64String(combined));
  }

  public static T Open<T>(Envelope envelope, string key, string recipient, string sender) =>
    Json.Parse<T>(OpenBytes(envelope, key, recipient, sender));

  internal static byte[] OpenBytes(Envelope envelope, string key, string recipient, string sender)
  {
    Check.Require(envelope.Version == 1 && envelope.Recipient == recipient && envelope.Sender == sender,
      "Unexpected encrypted message identity or version.");
    var aad = Aad(envelope.Sender, envelope.Recipient);
    Check.Require(envelope.Sealed is not null && envelope.Sealed.Length <= (MaximumPlaintext + 30) / 3 * 4,
      "Encrypted message is too large.");
    var combined = Check.Base64(envelope.Sealed) ?? throw new CoreException("Invalid encrypted message.");
    Check.Require(combined.Length >= 28 && combined.Length <= MaximumPlaintext + 28, "Invalid encrypted message size.");
    var plaintext = new byte[combined.Length - 28];
    using var aes = new AesGcm(Key(key), 16);
    aes.Decrypt(combined.AsSpan(0, 12), combined.AsSpan(12, plaintext.Length), combined.AsSpan(12 + plaintext.Length), plaintext, aad);
    return plaintext;
  }

  public static string PairKey(string group, string a, string b)
  {
    _ = Aad(a, b);
    if (Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b)) > 0) (a, b) = (b, a);
    return Convert.ToBase64String(HKDF.DeriveKey(HashAlgorithmName.SHA256, Key(group), 32,
      info: Encoding.UTF8.GetBytes($"zone/1 pair\n{a}\n{b}")));
  }

  public static byte[] PublicKey(ECDiffieHellman key)
  {
    var p = key.ExportParameters(false);
    return [4, ..p.Q.X!, ..p.Q.Y!];
  }

  public static string Commitment(byte[] publicKey) => Convert.ToBase64String(SHA256.HashData(publicKey));

  public static (string Key, string Code) JoinSecrets(ECDiffieHellman key, byte[] remote, byte[] joiner, byte[] host)
  {
    Check.Require(remote.Length == 65 && remote[0] == 4 && joiner.Length == 65 && host.Length == 65,
      "Invalid P-256 public key.");
    using var peer = ECDiffieHellman.Create(new ECParameters
    {
      Curve = ECCurve.NamedCurves.nistP256,
      Q = new ECPoint { X = remote[1..33], Y = remote[33..65] }
    });
    var shared = key.DeriveRawSecretAgreement(peer.PublicKey);
    try
    {
      byte[] Derive(string label, int count) => HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, count,
        info: [..Encoding.UTF8.GetBytes(label), ..joiner, ..host]);
      return (Convert.ToBase64String(Derive("zone/1 join key", 32)),
        (BinaryPrimitives.ReadUInt32BigEndian(Derive("zone/1 join code", 4)) % 1_000_000).ToString("D6"));
    }
    finally { CryptographicOperations.ZeroMemory(shared); }
  }
}

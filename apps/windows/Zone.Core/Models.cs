using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zone.Core;

public sealed class CoreException(string message) : Exception(message);

internal static class Check
{
  public static void Require(bool condition, string message)
  {
    if (!condition) throw new CoreException(message);
  }

  public static long Milliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  public static int Utf8(string? text) => text is null ? int.MaxValue : Encoding.UTF8.GetByteCount(text);
  public static string NewId() => Guid.NewGuid().ToString("D");
  public static bool IsUuid(string? text) => Guid.TryParseExact(text, "D", out _);

  /// Standard padded base64 only, as the Mac's decoder accepts it.
  public static byte[]? Base64(string? text)
  {
    if (text is null || text.Length % 4 != 0 || text.AsSpan().IndexOfAny(" \t\r\n") >= 0) return null;
    try { return Convert.FromBase64String(text); } catch (FormatException) { return null; }
  }
}

/// JSON as the Mac writes and reads it: camelCase keys, absent rather than null optionals.
internal static class Json
{
  static readonly JsonSerializerOptions Options = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };

  public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
  public static string Text<T>(T value) => JsonSerializer.Serialize(value, Options);
  public static T Parse<T>(string text) => Parse<T>(Encoding.UTF8.GetBytes(text));

  public static T Parse<T>(ReadOnlySpan<byte> data)
  {
    try
    {
      return JsonSerializer.Deserialize<T>(data, Options) ?? throw new CoreException("Invalid message.");
    }
    catch (JsonException) { throw new CoreException("Invalid message."); }
  }
}

public sealed record Device
{
  public required string Id { get; init; }
  public required string Name { get; init; }
  public required string Platform { get; init; }

  internal void Validate()
  {
    Check.Require(
      !string.IsNullOrEmpty(Id) && Check.Utf8(Id) <= 128 && !Id.Contains('\n'),
      "Invalid device identity.");
    Check.Require(
      !string.IsNullOrWhiteSpace(Name) && Check.Utf8(Name) <= 100,
      "Device names must be between 1 and 100 bytes.");
    Check.Require(Check.Utf8(Platform) <= 32, "Invalid platform.");
  }
}

public sealed record Peer(
  string Id, string Name, string Platform, long? LastSync, string? Error, bool Online);

public sealed record Note(
  string Id, string OwnerId, long Revision, long CreatedAt, string Kind, string Text, string Name,
  string Mime, int Size)
{
  public DateTimeOffset Date => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
}

/// A Zone device seen on the local network that is not connected yet.
public sealed record Nearby(string Id, string Name);

/// Another device asking to connect. Both screens show the same code.
public sealed record JoinRequest(string Name, string Code);

public sealed record Snapshot(
  Device Device, IReadOnlyList<Peer> Peers, IReadOnlyList<Note> Items, string? DiscoveryError,
  IReadOnlyList<Nearby> Nearby, JoinRequest? Request);

internal sealed record StoredItem
{
  public const int MaximumFile = 8 * 1024 * 1024;

  public required string Id { get; init; }
  public required long Revision { get; init; }
  public required long CreatedAt { get; init; }
  public required string Kind { get; init; }
  public required string Text { get; init; }
  public required string Name { get; init; }
  public required string Mime { get; init; }
  public required string Data { get; init; }
  public required bool Deleted { get; init; }

  public void Validate()
  {
    Check.Require(
      Check.IsUuid(Id) && Revision > 0 && CreatedAt >= 0, "Invalid item identity or revision.");
    Check.Require(Kind is "text" or "image" or "file", "Unsupported item type.");
    Check.Require(
      Check.Utf8(Text) <= 1024 * 1024 && Check.Utf8(Name) <= 255 && Check.Utf8(Mime) <= 128,
      "This item is too large.");
    Check.Require(
      Data is not null && Data.Length <= (MaximumFile + 2) / 3 * 4, "Files must be 8 MB or smaller.");
    var decoded = Check.Base64(Data) ?? throw new CoreException("Invalid attachment encoding.");
    Check.Require(decoded.Length <= MaximumFile, "Files must be 8 MB or smaller.");
    Check.Require((Kind != "text" || Data!.Length == 0) && (!Deleted || Text!.Length == 0 && Data!.Length == 0), "Invalid text item or deletion payload.");
  }

  public Note Summary(string owner) =>
    new(Id, owner, Revision, CreatedAt, Kind, Text, Name, Mime, DecodedSize(Data));

  static int DecodedSize(string data)
  {
    if (data.Length == 0 || data.Length % 4 != 0) return 0;
    var padding = data[^1] != '=' ? 0 : data[^2] != '=' ? 1 : 2;
    return data.Length / 4 * 3 - padding;
  }
}

internal sealed record StoredPeer
{
  public required Device Device { get; init; }
  public required string Key { get; init; }
  public required string[] Hosts { get; init; }
  public required ushort Port { get; init; }
  public long Cursor { get; init; }
  public long? LastSync { get; init; }
  public string? Error { get; init; }

  public Peer Summary() =>
    new(
      Device.Id, Device.Name, Device.Platform, LastSync, Error,
      Error is null && LastSync is long last && Check.Milliseconds() - last < 10_000);
}

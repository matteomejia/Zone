using System.Buffers.Binary;

namespace Zone.Core;

internal static class Framing
{
  public const int MaximumFrame = 18 * 1024 * 1024;
  public const int MaximumRequest = 64 * 1024;

  public static async Task<byte[]> ReceiveAsync(Stream stream, int limit = MaximumFrame, CancellationToken cancellationToken = default)
  {
    var header = new byte[4];
    await stream.ReadExactlyAsync(header, cancellationToken);
    var count = BinaryPrimitives.ReadUInt32BigEndian(header);
    Check.Require(count > 0 && count <= Math.Min(limit, MaximumFrame), "Invalid frame length.");
    var body = new byte[(int)count];
    await stream.ReadExactlyAsync(body, cancellationToken);
    return body;
  }

  public static async Task SendAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
  {
    Check.Require(body.Length > 0 && body.Length <= MaximumFrame, "Invalid frame length.");
    var header = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(body, cancellationToken);
  }

  public static async Task ReceiveReceiptAsync(Stream stream, CancellationToken cancellationToken = default)
  {
    var receipt = await ReceiveAsync(stream, 1, cancellationToken);
    Check.Require(receipt[0] == 1, "Invalid transfer receipt.");
  }
}

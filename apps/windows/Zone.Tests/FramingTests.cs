using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class FramingTests
{
  sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
  {
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
      base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
  }

  [Fact]
  public async Task HandlesPartialReadsAndBigEndianLengths()
  {
    var content = new byte[1027];
    Random.Shared.NextBytes(content);
    using var output = new MemoryStream();
    await Framing.SendAsync(output, content);
    Assert.Equal(new byte[] { 0, 0, 4, 3 }, output.ToArray()[..4]);
    using var input = new FragmentedStream(output.ToArray());
    Assert.Equal(content, await Framing.ReceiveAsync(input));
  }

  [Theory]
  [InlineData(0, 0, 0, 0)]
  [InlineData(1, 32, 0, 1)]
  [InlineData(255, 255, 255, 255)]
  public async Task RejectsInvalidLengths(byte a, byte b, byte c, byte d)
  {
    using var input = new MemoryStream([a, b, c, d]);
    await Assert.ThrowsAsync<CoreException>(() => Framing.ReceiveAsync(input));
  }

  [Fact]
  public async Task RejectsTruncationRequestOverflowAndBadReceipt()
  {
    using var truncated = new FragmentedStream([0, 0, 0, 2, 1]);
    await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReceiveAsync(truncated));
    using var oversized = new MemoryStream([0, 1, 0, 1]);
    await Assert.ThrowsAsync<CoreException>(() => Framing.ReceiveAsync(oversized, Framing.MaximumRequest));
    using var badReceipt = new MemoryStream([0, 0, 0, 1, 0]);
    await Assert.ThrowsAsync<CoreException>(() => Framing.ReceiveReceiptAsync(badReceipt));
  }
}

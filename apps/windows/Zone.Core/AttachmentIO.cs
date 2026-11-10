namespace Zone.Core;

public static class AttachmentIO
{
  public const int MaximumBytes = 8 * 1024 * 1024;
  public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken = default)
  {
    using var output = new MemoryStream();
    var buffer = new byte[81920];
    while (true)
    {
      var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)output.Length)), cancellationToken);
      if (count == 0) return output.ToArray();
      Check.Require(output.Length + count <= MaximumBytes, "Files must be 8 MiB or smaller.");
      await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
    }
  }
  public static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
  {
    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
    _ => "application/octet-stream"
  };
}

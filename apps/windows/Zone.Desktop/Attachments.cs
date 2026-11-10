using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Zone.Core;

namespace Zone.Desktop;

public sealed partial class MainWindow
{
  async Task Attach()
  {
    if (store is null || closing) return;
    var added = 0;
    try
    {
      var picker = new FileOpenPicker(AppWindow.Id) { CommitButtonText = "Add" };
      foreach (var file in await picker.PickMultipleFilesAsync())
      {
        await using var stream = File.OpenRead(file.Path);
        var bytes = await AttachmentIO.ReadBoundedAsync(stream, lifetime.Token);
        if (closing) return;
        store.AddAttachment(Path.GetFileName(file.Path), AttachmentIO.Mime(file.Path), bytes); added++;
      }
    }
    catch (OperationCanceledException) when (closing) { }
    catch (Exception error) { if (!closing) Error(error.Message); }
    finally { if (added > 0) ShowAdded(); }
  }

  /// Ctrl+V with focus on the board rather than in the sink.
  async Task PasteClipboard()
  {
    try { await Ingest(Clipboard.GetContent(), true, null); }
    catch (Exception error) { if (!closing) Error(error.Message); }
  }

  /// Adds whatever a clipboard or drop holds to this PC's board: files first, then an image,
  /// then (if allowed) a link or text. When text and a bitmap are both offered, as Office apps
  /// do for copied text, the text wins. `at` is where the user dropped it on the canvas.
  async Task<bool> Ingest(DataPackageView data, bool text, (double X, double Y)? at, bool quiet = false)
  {
    if (store is null || closing) return false;
    var added = 0;
    void Spot() { if (at is { } point) drops.Enqueue((point.X + added * 18, point.Y + added * 18)); }
    try
    {
      var hasText = text && data.Contains(StandardDataFormats.Text);
      if (data.Contains(StandardDataFormats.StorageItems))
      {
        foreach (var file in (await data.GetStorageItemsAsync()).OfType<StorageFile>())
        {
          using var stream = await file.OpenStreamForReadAsync();
          var bytes = await AttachmentIO.ReadBoundedAsync(stream, lifetime.Token);
          if (closing) return false;
          Spot(); store.AddAttachment(file.Name, AttachmentIO.Mime(file.Name), bytes); added++;
        }
        if (added == 0) throw new CoreException("Folders cannot be added. Choose files instead.");
      }
      else if (data.Contains(StandardDataFormats.Bitmap) && !hasText)
      {
        var png = await ReadPng(data);
        if (closing) return false;
        Spot(); store.AddAttachment("Pasted image.png", "image/png", png); added++;
      }
      else if (hasText)
      {
        var value = await data.GetTextAsync();
        if (closing || string.IsNullOrWhiteSpace(value)) return false;
        Spot(); store.AddText(value); added++;
      }
      else if (text && data.Contains(StandardDataFormats.WebLink))
      {
        var link = await data.GetWebLinkAsync();
        if (closing) return false;
        Spot(); store.AddText(link.AbsoluteUri); added++;
      }
      else
      {
        if (!quiet) Error("The clipboard has no text, image or files to paste.");
        return false;
      }
      return true;
    }
    catch (OperationCanceledException) when (closing) { return false; }
    catch (Exception error) { if (!closing) Error(error.Message); return false; }
    finally { if (added > 0) ShowAdded(); else drops.Clear(); }
  }

  /// Clipboard bitmaps are not guaranteed to be PNG; transcode to preserve a
  /// portable raster payload with an honest MIME type.
  async Task<byte[]> ReadPng(DataPackageView data)
  {
    using var bitmap = await (await data.GetBitmapAsync()).OpenReadAsync();
    using var input = bitmap.AsStreamForRead();
    var bytes = await AttachmentIO.ReadBoundedAsync(input, lifetime.Token);
    using var decodedStream = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(decodedStream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
    decodedStream.Seek(0);
    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(decodedStream);
    if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 64_000_000) throw new CoreException("This clipboard image is too large to decode.");
    using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
    using var png = new InMemoryRandomAccessStream();
    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png);
    encoder.SetSoftwareBitmap(softwareBitmap); await encoder.FlushAsync(); png.Seek(0);
    using var pngInput = png.AsStreamForRead();
    return await AttachmentIO.ReadBoundedAsync(pngInput, lifetime.Token);
  }

  async Task<bool> Copy(Note note)
  {
    InMemoryRandomAccessStream? bitmapStream = null;
    try
    {
      var data = new DataPackage();
      if (note.Kind == "text") data.SetText(note.Text);
      else
      {
        bitmapStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(bitmapStream)) { writer.WriteBytes(store!.Attachment(note.OwnerId, note.Id)); await writer.StoreAsync(); writer.DetachStream(); }
        bitmapStream.Seek(0); data.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmapStream));
      }
      // Another desktop app can briefly hold the clipboard open. Retry the
      // entire publication before surfacing a transient COM failure to the user.
      for (var attempt = 0; ; attempt++)
      {
        try { Clipboard.SetContent(data); Clipboard.Flush(); break; }
        catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
        { await Task.Delay(60 * (attempt + 1), lifetime.Token); }
      }
      return true;
    }
    catch (Exception error) { if (!closing) Error($"Could not copy to the clipboard. {error.Message} (0x{error.HResult:X8})"); return false; }
    finally { bitmapStream?.Dispose(); }
  }

  async Task Save(Note note)
  {
    try
    {
      var bytes = store!.Attachment(note.OwnerId, note.Id);
      var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = PairingStore.SuggestedFileName(note.Name) };
      var result = await picker.PickSaveFileAsync();
      if (result is not null) await File.WriteAllBytesAsync(result.Path, bytes, lifetime.Token);
    }
    catch (OperationCanceledException) when (closing) { }
    catch (Exception error) { if (!closing) Error(error.Message); }
  }

  /// Fills an image card's frame once its attachment decodes.
  async Task LoadImage(Border frame, Note note)
  {
    try
    {
      using var stream = new InMemoryRandomAccessStream();
      using (var writer = new DataWriter(stream)) { writer.WriteBytes(store!.Attachment(note.OwnerId, note.Id)); await writer.StoreAsync(); writer.DetachStream(); }
      stream.Seek(0);
      var source = new BitmapImage { DecodePixelWidth = 504 };
      await source.SetSourceAsync(stream);
      if (closing) return;
      frame.Background = new ImageBrush { ImageSource = source, Stretch = Stretch.UniformToFill };
      frame.Child = null;
    }
    catch (Exception) { /* Opaque attachment remains savable even if its preview cannot decode. */ }
  }
}

using System.Numerics;
using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Zone.Core;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace Zone.Desktop;

/// The free-form board: a dotted canvas of cards that can be dragged anywhere.
/// Positions live in `BoardLayout` on this PC only; they are never synced.
public sealed partial class MainWindow
{
  const double DotStep = 20, Inset = 11, TextLine = 17, CollapsedText = TextLine * 7, ExpandedText = 380;
  const int LiftedZ = 999_999;
  readonly Dictionary<string, CardView> cardViews = [];
  readonly Dictionary<string, (double Width, double Height)> cardSizes = [];
  readonly Queue<(double X, double Y)> drops = new();
  readonly TextBlock ruler = BodyText();
  string? boardOwner;

  static TextBlock BodyText() => new()
  {
    FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = TextLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
  };

  /// One dashed line per row; zero-length dashes with round caps draw as dots.
  void DrawDots(double width, double height)
  {
    var rows = new GeometryGroup();
    for (var y = DotStep / 2; y < height + DotStep * 2; y += DotStep)
      rows.Children.Add(new LineGeometry { StartPoint = new Point(DotStep / 2, y), EndPoint = new Point(width + DotStep * 2, y) });
    dotPath.Data = rows;
  }

  static Uri? Link(Note note)
  {
    if (note.Kind != "text" || Encoding.UTF8.GetByteCount(note.Text) > 2048) return null;
    var text = note.Text.Trim();
    if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
    return uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri : null;
  }

  static string Excerpt(Note note, bool expanded)
  {
    var limit = expanded ? 20_000 : 1_200;
    var text = (note.Text.Length > limit + 200 ? note.Text[..(limit + 200)] : note.Text).Trim();
    if (text.Length > limit) text = text[..limit];
    return text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
  }

  double TextHeight(string text, double cardWidth)
  {
    ruler.Text = text;
    ruler.Measure(new Size(cardWidth - 2 - Inset * 2, double.PositiveInfinity));
    return Math.Ceiling(ruler.DesiredSize.Height);
  }

  bool Truncated(Note note) => note.Kind == "text" && Link(note) is null
    && (Encoding.UTF8.GetByteCount(note.Text) > 1_200 || TextHeight(Excerpt(note, false), BoardLayout.CardWidth) > CollapsedText + 1);

  /// Collapsed card size, derived from the note alone so new cards can be placed before layout.
  (double Width, double Height) SizeOf(Note note)
  {
    if (cardSizes.TryGetValue(note.Id, out var size)) return size;
    size = note.Kind switch
    {
      "image" => (BoardLayout.CardWidth, 120),
      "file" => (BoardLayout.CardWidth, 56),
      _ => Link(note) is not null ? (BoardLayout.CardWidth, 56)
        : (BoardLayout.CardWidth, Math.Min(TextHeight(Excerpt(note, false), BoardLayout.CardWidth), CollapsedText) + Inset * 2 + 2),
    };
    return cardSizes[note.Id] = size;
  }

  static string ByteCount(long size) => size < 1024 ? $"{size} bytes"
    : size < 1024 * 1024 ? $"{size / 1024.0:0.#} KB" : $"{size / (1024.0 * 1024):0.#} MB";

  void RefreshBoard(bool reveal = false)
  {
    if (store is null || closing) return;
    var owner = selected ?? store.Device.Id;
    var local = owner == store.Device.Id;
    IReadOnlyList<Note> notes;
    try { notes = store.Notes(owner); } catch (Exception error) { Error(error.Message); return; }
    if (boardOwner != owner)
    {
      canvas.Children.Clear(); cardViews.Clear(); cardSizes.Clear();
      boardOwner = owner; boardScroll.ChangeView(0, 0, null, true);
    }
    var width = boardScroll.ActualWidth > 0 ? boardScroll.ActualWidth : PanelWidth;
    var placed = layout.Arrange(owner, notes, width, SizeOf, local ? drops : null);
    drops.Clear();
    var live = notes.Select(note => note.Id).ToHashSet();
    foreach (var (id, view) in cardViews.Where(pair => !live.Contains(pair.Key)).ToArray())
    {
      canvas.Children.Remove(view.Host); cardViews.Remove(id); cardSizes.Remove(id);
    }
    foreach (var note in notes)
    {
      if (!cardViews.TryGetValue(note.Id, out var view))
      {
        cardViews[note.Id] = view = new CardView(this, note, local);
        canvas.Children.Add(view.Host);
      }
      view.Place();
    }
    emptyTitle.Text = local ? "Paste anything." : $"Nothing from {store.Peers.FirstOrDefault(p => p.Id == owner)?.Name ?? "this device"} yet.";
    emptyDetail.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
    emptyState.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    UpdateExtent();
    if (reveal && local && placed.Count > 0 && cardViews.TryGetValue(placed[^1], out var fresh)) Reveal(fresh);
  }

  /// Shows this PC's board with the newest item in view. Adding always lands there, whichever tab is open.
  void ShowAdded()
  {
    if (store is null || closing) return;
    selected = store.Device.Id; RefreshTabs(); RefreshBoard(reveal: true);
  }

  /// The canvas is at least the viewport and grows to contain every card, with room for the sink below.
  void UpdateExtent()
  {
    double right = 0, bottom = 0;
    foreach (var view in cardViews.Values)
    {
      right = Math.Max(right, view.X + view.Width + BoardLayout.Pad);
      bottom = Math.Max(bottom, view.Y + view.Height + BoardLayout.Pad + SinkInset);
    }
    canvas.Width = Math.Max(boardScroll.ActualWidth, right);
    canvas.Height = Math.Max(boardScroll.ActualHeight, bottom);
  }

  void Reveal(CardView view)
  {
    boardScroll.UpdateLayout();
    boardScroll.ChangeView(
      view.X + view.Width / 2 - boardScroll.ActualWidth / 2,
      view.Y + view.Height / 2 - (boardScroll.ActualHeight - SinkInset) / 2, null);
  }

  bool InSink(object source)
  {
    for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
      if (ReferenceEquals(element, sinkPanel)) return true;
    return false;
  }

  static bool Rich(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems) || data.Contains(StandardDataFormats.Bitmap);

  void OnDragOver(object sender, DragEventArgs e)
  {
    if (store is null || closing) return;
    var data = e.DataView;
    var plain = data.Contains(StandardDataFormats.Text) || data.Contains(StandardDataFormats.WebLink);
    // Text dragged onto the sink goes into the draft, as the text box already does.
    if (!Rich(data) && (!plain || InSink(e.OriginalSource))) return;
    e.AcceptedOperation = DataPackageOperation.Copy; e.Handled = true;
    if (e.DragUIOverride is { } hint) { hint.Caption = "Add to board"; hint.IsGlyphVisible = false; }
    dropOutline.Visibility = Visibility.Visible;
  }

  async void OnDrop(object sender, DragEventArgs e)
  {
    dropOutline.Visibility = Visibility.Collapsed;
    if (store is null || closing) return;
    var data = e.DataView;
    var onSink = InSink(e.OriginalSource);
    if (!Rich(data) && onSink) return;
    e.Handled = true;
    var point = e.GetPosition(canvas);
    var deferral = e.GetDeferral();
    try { await Ingest(data, true, onSink ? null : (point.X, point.Y)); }
    finally { deferral.Complete(); }
  }

  /// One card. Click copies (or saves a file); drag moves it; hover or keyboard focus reveals its actions.
  sealed class CardView
  {
    readonly MainWindow window;
    readonly Note note;
    readonly bool own;
    readonly Border face, actions, copied;
    readonly Border? picture;
    bool expanded, armed, dragging, hovering, focused;
    Point origin, delta;

    public ContentControl Host { get; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Width => Host.Width;
    public double Height => Host.ActualHeight > 0 ? Host.ActualHeight : window.SizeOf(note).Height;

    public CardView(MainWindow window, Note note, bool own)
    {
      (this.window, this.note, this.own) = (window, note, own);
      face = new Border
      {
        Background = window.cardFill, BorderBrush = window.cardStroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
        Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 6), TranslationTransition = new Vector3Transition(),
      };
      actions = new Border
      {
        CornerRadius = new CornerRadius(12), Background = window.raised, BorderBrush = window.chipStroke, BorderThickness = new Thickness(1),
        Padding = new Thickness(3, 0, 3, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(5), Visibility = Visibility.Collapsed,
      };
      var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
      badge.Children.Add(new FontIcon { Glyph = "", FontSize = 10, FontFamily = new FontFamily(Icons), Foreground = window.ink });
      badge.Children.Add(new TextBlock { Text = "Copied", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = window.ink });
      copied = new Border
      {
        Child = badge, CornerRadius = new CornerRadius(13), Background = window.raised, BorderBrush = window.chipStroke, BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 5, 10, 6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
      };
      if (note.Kind == "image")
      {
        picture = new Border
        {
          Height = 118, CornerRadius = new CornerRadius(10), Background = window.wash,
          Child = new FontIcon { Glyph = "", FontSize = 18, FontFamily = new FontFamily(Icons), Foreground = window.tertiary },
        };
        _ = window.LoadImage(picture, note);
      }
      var layers = new Grid();
      layers.Children.Add(face); layers.Children.Add(actions); layers.Children.Add(copied);
      Host = new ContentControl
      {
        Content = layers, IsTabStop = true, UseSystemFocusVisuals = true, CornerRadius = new CornerRadius(11),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        ScaleTransition = new Vector3Transition(),
      };
      var title = note.Kind == "text" ? Excerpt(note, false) : note.Name;
      AutomationProperties.SetName(Host, title.Length > 200 ? title[..200] : title);
      ToolTipService.SetToolTip(Host, note.Date.LocalDateTime.ToString("g"));
      Build();

      Host.PointerEntered += (_, _) => { hovering = true; ShowActions(); };
      Host.PointerExited += (_, _) => { hovering = false; ShowActions(); };
      Host.GotFocus += (_, _) => { focused = Host.FocusState == FocusState.Keyboard; ShowActions(); };
      Host.LostFocus += (_, _) => { focused = false; ShowActions(); };
      Host.SizeChanged += (_, _) => window.UpdateExtent();
      Host.PointerPressed += Press;
      Host.PointerMoved += Move;
      Host.PointerReleased += (_, e) => Finish(e.Pointer, tap: true);
      Host.PointerCanceled += (_, e) => Finish(e.Pointer, tap: false);
      Host.PointerCaptureLost += (_, e) => Finish(e.Pointer, tap: false);
      Host.KeyDown += Key;
    }

    void Build()
    {
      Host.Width = expanded ? BoardLayout.WideWidth : BoardLayout.CardWidth;
      face.Child = Content();
      var row = new StackPanel { Orientation = Orientation.Horizontal };
      var menu = new MenuFlyout();
      void Add(string glyph, string label, Action run, bool inMenu = true)
      {
        var button = window.IconButton(glyph, 11, 22, label, window.ink, 11);
        button.IsTabStop = false; button.Click += (_, _) => run(); row.Children.Add(button);
        if (!inMenu) return;
        var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => run(); menu.Items.Add(item);
      }
      if (expanded || window.Truncated(note)) Add(expanded ? "" : "", expanded ? "Collapse" : "Expand", Toggle);
      if (note.Kind != "file") Add("", "Copy", () => _ = Copy());
      if (Link(note) is { } link)
      {
        var open = new MenuFlyoutItem { Text = "Open link" };
        open.Click += async (_, _) => { try { await Launcher.LaunchUriAsync(link); } catch (Exception error) { window.Error(error.Message); } };
        menu.Items.Add(open);
      }
      if (note.Kind != "text") Add("", "Save…", () => _ = window.Save(note));
      if (own)
      {
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("", "Delete", Delete);
      }
      actions.Child = row;
      Host.ContextFlyout = menu;
    }

    FrameworkElement Content()
    {
      if (picture is not null) return picture;
      if (note.Kind == "file") return Row("", note.Name, ByteCount(note.Size), symbol: false);
      if (Link(note) is { } link)
        return Row("", link.Host, link.PathAndQuery is "" or "/" ? link.Scheme : link.PathAndQuery, symbol: true);
      var text = BodyText();
      text.Text = Excerpt(note, expanded); text.Foreground = window.ink; text.Margin = new Thickness(Inset);
      if (!expanded) { text.MaxLines = 7; text.TextTrimming = TextTrimming.CharacterEllipsis; return text; }
      text.IsTextSelectionEnabled = true;
      return new ScrollViewer { Content = text, MaxHeight = ExpandedText + Inset * 2, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false };
    }

    Grid Row(string glyph, string title, string detail, bool symbol)
    {
      var row = new Grid { Height = 54, Padding = new Thickness(Inset, 0, Inset, 0), ColumnSpacing = 9 };
      row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      row.ColumnDefinitions.Add(new ColumnDefinition());
      var icon = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
      if (symbol) icon.Children.Add(new Ellipse { Fill = window.wash });
      icon.Children.Add(new FontIcon { Glyph = glyph, FontSize = symbol ? 13 : 24, FontFamily = new FontFamily(Icons), Foreground = window.secondary });
      var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
      labels.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = window.ink, TextTrimming = TextTrimming.CharacterEllipsis });
      labels.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = window.secondary, TextTrimming = TextTrimming.CharacterEllipsis });
      row.Children.Add(icon); row.Children.Add(labels); Grid.SetColumn(labels, 1);
      return row;
    }

    /// Moves the card to its saved spot, unless the user is holding it.
    public void Place()
    {
      if (dragging || window.layout.Find(note.OwnerId, note.Id) is not { } spot) return;
      (X, Y) = (spot.X, spot.Y);
      Canvas.SetLeft(Host, X); Canvas.SetTop(Host, Y); Canvas.SetZIndex(Host, Math.Min(spot.Z, LiftedZ - 1));
    }

    void ShowActions() => actions.Visibility = (hovering || focused) && !dragging ? Visibility.Visible : Visibility.Collapsed;

    void Lift(bool up)
    {
      dragging = up;
      Host.CenterPoint = new Vector3((float)Host.ActualWidth / 2, (float)Host.ActualHeight / 2, 0);
      Host.Scale = new Vector3(up ? 1.035f : 1f);
      face.Translation = new Vector3(0, 0, up ? 32 : 6);
      if (up) Canvas.SetZIndex(Host, LiftedZ);
      ShowActions();
    }

    void Press(object sender, PointerRoutedEventArgs e)
    {
      var point = e.GetCurrentPoint(null);
      if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch && !point.Properties.IsLeftButtonPressed) return;
      armed = Host.CapturePointer(e.Pointer);
      origin = point.Position; delta = default;
    }

    void Move(object sender, PointerRoutedEventArgs e)
    {
      if (!armed) return;
      var position = e.GetCurrentPoint(null).Position;
      delta = new Point(position.X - origin.X, position.Y - origin.Y);
      if (!dragging && Math.Abs(delta.X) <= 3 && Math.Abs(delta.Y) <= 3) return;
      if (!dragging) Lift(true);
      Canvas.SetLeft(Host, Math.Max(BoardLayout.Edge, X + delta.X));
      Canvas.SetTop(Host, Math.Max(BoardLayout.Edge, Y + delta.Y));
    }

    void Finish(Pointer pointer, bool tap)
    {
      if (!armed) return;
      armed = false;
      Host.ReleasePointerCapture(pointer);
      if (dragging)
      {
        Lift(false);
        window.layout.Move(note.OwnerId, note.Id, delta.X, delta.Y);
        Place(); window.UpdateExtent();
      }
      else if (tap) Tap();
    }

    void Tap()
    {
      window.layout.Raise(note.OwnerId, note.Id); Place();
      if (note.Kind == "file") _ = window.Save(note);
      else if (!expanded) _ = Copy();
    }

    void Toggle()
    {
      expanded = !expanded; Build();
      window.layout.Raise(note.OwnerId, note.Id); Place();
    }

    async Task Copy()
    {
      if (!await window.Copy(note)) return;
      copied.Visibility = Visibility.Visible;
      await Task.Delay(900);
      copied.Visibility = Visibility.Collapsed;
    }

    void Delete() => window.Try(() => { window.store!.DeleteNote(note.Id); window.RefreshBoard(); });

    void Key(object sender, KeyRoutedEventArgs e)
    {
      if (!ReferenceEquals(e.OriginalSource, Host)) return;
      var step = e.Key switch
      {
        VirtualKey.Left => new Point(-DotStep, 0), VirtualKey.Right => new Point(DotStep, 0),
        VirtualKey.Up => new Point(0, -DotStep), VirtualKey.Down => new Point(0, DotStep), _ => default,
      };
      if (step != default && Down(VirtualKey.Menu))
      {
        window.layout.Move(note.OwnerId, note.Id, step.X, step.Y); Place(); window.UpdateExtent(); Host.StartBringIntoView();
      }
      else if (e.Key is VirtualKey.Enter or VirtualKey.Space) Tap();
      else if (e.Key == VirtualKey.Delete && own) Delete();
      else if (e.Key == VirtualKey.C && Down(VirtualKey.Control) && note.Kind != "file") _ = Copy();
      else return;
      e.Handled = true;
    }
  }
}

using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Zone.Core;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Zone.Desktop;

/// The whole app: a compact panel with device tabs, a free-form board and a sink.
/// Mirrors the Mac notebook (apps/macos/Sources/Zone/Board.swift).
public sealed partial class MainWindow : Window
{
  const double HeaderHeight = 44, PanelWidth = 392, PanelHeight = 540, MinimumWidth = 320, MinimumHeight = 340;
  const string Icons = "Segoe Fluent Icons,Segoe MDL2 Assets";

  // Paper tones follow the Mac's `Paper`; every brush is recoloured in place when the theme changes.
  readonly SolidColorBrush paper = new(), cardFill = new(), raised = new(), ink = new(), secondary = new(), tertiary = new(),
    hairline = new(), cardStroke = new(), chipStroke = new(), dotInk = new(), wash = new(), hover = new(), pressed = new(),
    sendIdle = new(), sendReady = new(), offline = new(), clear = new(Colors.Transparent),
    online = new(Color.FromArgb(255, 51, 184, 107));
  readonly AcrylicBrush glass = new() { TintOpacity = 0.6 };

  readonly Grid root = new();
  readonly Grid header = new() { Height = HeaderHeight, Padding = new Thickness(8, 0, 8, 0) };
  readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
  readonly ScrollViewer tabScroll = new()
  {
    HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0),
  };
  readonly Button connect, tuck, attach, send;
  readonly Ellipse sendDisc = new() { Width = 22, Height = 22 };
  readonly Grid boardArea = new() { AllowDrop = true };
  readonly Grid dotHost = new() { IsHitTestVisible = false };
  readonly ShapePath dotPath = new() { StrokeThickness = 1.5, StrokeDashCap = PenLineCap.Round, StrokeDashArray = [0, DotStep / 1.5] };
  readonly TranslateTransform dotShift = new();
  readonly Canvas canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
  readonly ScrollViewer boardScroll = new()
  {
    HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto, ZoomMode = ZoomMode.Disabled,
    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false,
  };
  readonly StackPanel emptyState = new() { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
  readonly TextBlock emptyTitle = new() { FontFamily = new FontFamily("Georgia"), FontStyle = Windows.UI.Text.FontStyle.Italic, FontSize = 22, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
  readonly TextBlock emptyDetail = new() { FontSize = 12, TextAlignment = TextAlignment.Center, Text = "Ctrl+V, drop a file, or type below.\nIt appears on your other devices." };
  readonly Rectangle dropOutline = new() { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.5, StrokeDashArray = [4, 3.3], Margin = new Thickness(6), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
  readonly Border sinkPanel = new() { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Bottom };
  readonly TextBox sink = new()
  {
    PlaceholderText = "Paste, type, or drop anything", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13,
    BorderThickness = new Thickness(0), Padding = new Thickness(5, 5, 5, 6), MinHeight = 0, MaxHeight = 114, VerticalAlignment = VerticalAlignment.Center,
  };
  readonly InfoBar notice = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
  readonly DispatcherTimer presence = new() { Interval = TimeSpan.FromSeconds(3) };
  readonly DispatcherTimer draftSaver = new() { Interval = TimeSpan.FromMilliseconds(800) };
  readonly CancellationTokenSource lifetime = new();
  readonly BoardLayout layout;
  readonly string dataDirectory;
  readonly nint handle;
  PairingStore? store;
  PairingSession? session;
  Task? watcher;
  Task joinWork = Task.CompletedTask;
  Task initialization = Task.CompletedTask;
  Task pairingWork = Task.CompletedTask;
  string? selected;
  bool closing, canClose, started, composing;
  RectInt32 restoredFrame;
  ContentDialog? pairing;
  CancellationTokenSource? pairCancellation;
  readonly StackPanel nearbyRows = new() { Spacing = 8 };
  readonly TextBlock pairMessage = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Text = "Open + on your other device to find it nearby." };
  readonly TextBlock code = new() { FontSize = 40, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80 };
  readonly Button approve = new() { Content = "Approve", Visibility = Visibility.Collapsed };
  readonly Button decline = new() { Content = "Decline", Visibility = Visibility.Collapsed };

  [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);

  public MainWindow()
  {
    Title = "Zone";
    handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
    dataDirectory = Environment.GetEnvironmentVariable("ZONE_DATA_DIR") ?? Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zone", "PairingProbe");
    layout = BoardLayout.Load(Path.Combine(dataDirectory, "board-layout.json"));
    ShapeWindow();

    connect = IconButton("", 12, 26, "Connect a device", secondary);
    tuck = IconButton("", 12, 26, "Minimize (Esc)", secondary);
    attach = IconButton("", 15, 28, "Attach files (Ctrl+O)", secondary);
    send = IconButton("", 10, 28, "Add to board (Enter)", paper);
    connect.IsEnabled = false;
    ((Grid)send.Content).Children.Insert(0, sendDisc);
    ((FontIcon)((Grid)send.Content).Children[1]).FontWeight = FontWeights.Bold;

    root.Background = paper;
    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    root.RowDefinitions.Add(new RowDefinition());

    header.Background = clear;
    header.ColumnDefinitions.Add(new ColumnDefinition());
    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    tabScroll.Content = tabStrip;
    header.Children.Add(tabScroll); header.Children.Add(connect); header.Children.Add(tuck);
    Grid.SetColumn(connect, 1); Grid.SetColumn(tuck, 2);
    header.ContextFlyout = HeaderMenu();
    root.Children.Add(header);
    root.Children.Add(new Border { Height = 1, Background = hairline, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false });

    dotPath.Stroke = dotInk; dotPath.RenderTransform = dotShift;
    dotHost.Children.Add(dotPath);
    boardScroll.Content = canvas; canvas.Background = clear;
    emptyTitle.Foreground = ink; emptyDetail.Foreground = secondary;
    emptyState.Children.Add(emptyTitle); emptyState.Children.Add(emptyDetail);
    dropOutline.Stroke = secondary;
    boardArea.Background = clear;
    foreach (var layer in new FrameworkElement[] { dotHost, boardScroll, emptyState, dropOutline, BuildSink(), notice }) boardArea.Children.Add(layer);
    Grid.SetRow(boardArea, 1); root.Children.Add(boardArea);
    Content = root;

    connect.Click += async (_, _) =>
    {
      try { pairingWork = ShowPairing(); await pairingWork; }
      catch (Exception error) { Error(error.Message); }
    };
    tuck.Click += (_, _) => Minimize();
    approve.Click += (_, _) =>
    {
      try { session!.Approve(); SetPairMessage("Approved. Waiting for the other device…"); }
      catch (Exception error) { SetPairMessage(error.Message); }
    };
    decline.Click += (_, _) => { session?.Decline(); SetPairMessage("Request declined. You can connect another device."); };

    root.KeyDown += OnKey;
    root.PreviewKeyDown += (_, e) =>
    {
      if (e.Key != VirtualKey.Tab || !Down(VirtualKey.Control) || store is null) return;
      var ids = new[] { store.Device.Id }.Concat(store.Peers.Select(p => p.Id)).ToArray();
      var next = (Array.IndexOf(ids, selected) + (Down(VirtualKey.Shift) ? ids.Length - 1 : 1)) % ids.Length;
      e.Handled = true; Select(ids[next]);
    };
    root.ActualThemeChanged += (_, _) => { ApplyTheme(); RefreshTabs(); };
    header.SizeChanged += (_, _) => UpdateDragRegion();
    tabScroll.SizeChanged += (_, _) => UpdateDragRegion();
    boardArea.SizeChanged += (_, e) =>
    {
      dotHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
      DrawDots(e.NewSize.Width, e.NewSize.Height); UpdateExtent();
    };
    boardScroll.ViewChanged += (_, _) =>
    {
      dotShift.X = -(boardScroll.HorizontalOffset % DotStep); dotShift.Y = -(boardScroll.VerticalOffset % DotStep);
    };
    sinkPanel.SizeChanged += (_, _) => { emptyState.Margin = new Thickness(24, 0, 24, SinkInset); UpdateExtent(); };
    boardArea.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnDragOver), true);
    boardArea.AddHandler(UIElement.DropEvent, new DragEventHandler(OnDrop), true);
    boardArea.DragLeave += (_, _) => dropOutline.Visibility = Visibility.Collapsed;
    presence.Tick += (_, _) => { if (!closing) RefreshTabs(); };
    draftSaver.Tick += (_, _) => { draftSaver.Stop(); layout.Draft = sink.Text; layout.Save(); };
    root.Loaded += async (_, _) =>
    {
      ApplyTheme(); UpdateDragRegion();
      if (!started) { started = true; initialization = Start(); await initialization; }
    };
    AppWindow.Changed += (_, change) =>
    {
      if ((change.DidPositionChange || change.DidSizeChange) && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        restoredFrame = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    };
    AppWindow.Closing += OnClosing;
    ApplyTheme();
  }

  /// A borderless-looking panel: no title bar, the header strip moves the window, and the frame still resizes and snaps.
  void ShapeWindow()
  {
    var presenter = (OverlappedPresenter)AppWindow.Presenter;
    presenter.SetBorderAndTitleBar(true, false);
    presenter.IsAlwaysOnTop = layout.Topmost;
    var scale = GetDpiForWindow(handle) / 96.0;
    presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
    presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
    var size = new SizeInt32((int)(PanelWidth * scale), (int)(PanelHeight * scale));
    var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
    var frame = new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
    if (layout.Frame is [var x, var y, var width, var height] && width >= presenter.PreferredMinimumWidth && height >= presenter.PreferredMinimumHeight)
    {
      // Only reuse a remembered frame that is still reachable on a connected display.
      var saved = new RectInt32(x, y, width, height);
      var home = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest).WorkArea;
      if (x + 80 <= home.X + home.Width && x + width >= home.X + 80 && y >= home.Y - 8 && y + 40 <= home.Y + home.Height) frame = saved;
    }
    AppWindow.MoveAndResize(frame);
    restoredFrame = frame;
  }

  void ApplyTheme()
  {
    var dark = root.ActualTheme == ElementTheme.Dark;
    static Color Gray(double white, double alpha = 1) => Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(white * 255), (byte)Math.Round(white * 255), (byte)Math.Round(white * 255));
    Color Ink(double alpha) => Gray(dark ? 1 : 0, alpha);
    paper.Color = Gray(dark ? 0.105 : 0.945); cardFill.Color = Gray(dark ? 0.185 : 1); raised.Color = Gray(dark ? 0.25 : 0.98, 0.96);
    ink.Color = Ink(dark ? 0.92 : 0.88); secondary.Color = Ink(dark ? 0.56 : 0.52); tertiary.Color = Ink(0.28);
    hairline.Color = Ink(0.07); cardStroke.Color = Ink(0.09); chipStroke.Color = Ink(0.1); dotInk.Color = Ink(0.13);
    wash.Color = Ink(0.06); hover.Color = Ink(0.07); pressed.Color = Ink(0.11);
    sendIdle.Color = Ink(0.18); sendReady.Color = Ink(0.85); offline.Color = Ink(0.2);
    glass.TintColor = Gray(dark ? 0.3 : 0.99); glass.FallbackColor = Gray(dark ? 0.21 : 0.99, 0.94);
  }

  static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

  /// A flat glyph button. Colours come from the shared brushes, so they follow the theme.
  Button IconButton(string glyph, double glyphSize, double box, string label, Brush foreground, double radius = 6)
  {
    var content = new Grid();
    content.Children.Add(new FontIcon { Glyph = glyph, FontSize = glyphSize, FontFamily = new FontFamily(Icons), Foreground = foreground });
    var button = new Button { Content = content, Width = box, Height = box, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
      BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(radius), VerticalAlignment = VerticalAlignment.Center };
    Flatten(button, clear, hover, pressed);
    AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
    return button;
  }

  void Flatten(Button button, Brush rest, Brush over, Brush down)
  {
    button.Resources["ButtonBackground"] = rest; button.Resources["ButtonBackgroundPointerOver"] = over;
    button.Resources["ButtonBackgroundPressed"] = down; button.Resources["ButtonBackgroundDisabled"] = clear;
    foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
    {
      button.Resources["ButtonBorderBrush" + state] = clear;
      button.Resources["ButtonForeground" + state] = state == "Disabled" ? tertiary : ink;
    }
  }

  MenuFlyout HeaderMenu()
  {
    var menu = new MenuFlyout();
    void Item(string text, Action run)
    {
      var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => run(); menu.Items.Add(item);
    }
    Item("New item", () => { SelectLocal(); sink.Focus(FocusState.Programmatic); });
    Item("Attach files…", () => _ = Attach());
    Item("Connect a device…", () => { if (connect.IsEnabled) pairingWork = ShowPairing(); });
    menu.Items.Add(new MenuFlyoutSeparator());
    var top = new ToggleMenuFlyoutItem { Text = "Keep on top", IsChecked = layout.Topmost };
    top.Click += (_, _) =>
    {
      layout.Topmost = top.IsChecked; layout.Save();
      if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = top.IsChecked;
    };
    menu.Items.Add(top);
    Item("Minimize", Minimize);
    Item("Quit Zone", Close);
    return menu;
  }

  FrameworkElement BuildSink()
  {
    var row = new Grid { ColumnSpacing = 4 };
    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    row.ColumnDefinitions.Add(new ColumnDefinition());
    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    attach.VerticalAlignment = send.VerticalAlignment = VerticalAlignment.Bottom;
    send.CornerRadius = new CornerRadius(14);
    row.Children.Add(attach); row.Children.Add(sink); row.Children.Add(send);
    Grid.SetColumn(sink, 1); Grid.SetColumn(send, 2);
    sinkPanel.Background = glass; sinkPanel.BorderBrush = chipStroke; sinkPanel.Child = row;

    foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
    {
      sink.Resources["TextControlBackground" + state] = clear;
      sink.Resources["TextControlBorderBrush" + state] = clear;
      sink.Resources["TextControlForeground" + state] = ink;
      sink.Resources["TextControlPlaceholderForeground" + state] = tertiary;
    }
    sink.Resources["TextControlElevationBorderBrush"] = clear;
    sink.Resources["TextControlElevationBorderFocusedBrush"] = clear;
    sink.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
    sink.Foreground = ink; sink.PlaceholderForeground = tertiary;
    sink.Text = layout.Draft;
    AutomationProperties.SetName(sink, "Add to board");

    attach.Click += async (_, _) => await Attach();
    send.Click += (_, _) => AddDraft();
    sink.TextCompositionStarted += (_, _) => composing = true;
    sink.TextCompositionEnded += (_, _) => composing = false;
    // Enter adds the item; Shift+Enter inserts a line break.
    sink.PreviewKeyDown += (_, e) =>
    {
      if (e.Key == VirtualKey.Enter && !composing && !Down(VirtualKey.Shift)) { e.Handled = true; AddDraft(); }
    };
    sink.TextChanged += (_, _) => { RefreshSend(); draftSaver.Stop(); draftSaver.Start(); };
    // Pasting into an empty sink adds the clipboard straight to the board; files and images always do.
    sink.Paste += (_, e) =>
    {
      try
      {
        var data = Clipboard.GetContent();
        var empty = string.IsNullOrWhiteSpace(sink.Text);
        var text = data.Contains(StandardDataFormats.Text);
        if (!data.Contains(StandardDataFormats.StorageItems) && !(data.Contains(StandardDataFormats.Bitmap) && !text) && !(text && empty)) return;
        e.Handled = true; _ = Ingest(data, empty, null, quiet: true);
      }
      catch (Exception error) { Error(error.Message); }
    };
    RefreshSend();
    return sinkPanel;
  }

  void RefreshSend()
  {
    var empty = string.IsNullOrWhiteSpace(sink.Text);
    sendDisc.Fill = empty ? sendIdle : sendReady; send.IsEnabled = !empty;
  }

  double SinkInset => sinkPanel.ActualHeight + sinkPanel.Margin.Top + sinkPanel.Margin.Bottom;
  void Error(string message) { notice.Message = string.IsNullOrWhiteSpace(message) ? "The action could not be completed. Please try again." : message; notice.IsOpen = true; }
  void Try(Action action) { try { action(); } catch (Exception error) { Error(error.Message); } }
  void Minimize() { if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize(); }

  /// The empty stretch of the header moves the window, like the Mac panel's top bar.
  void UpdateDragRegion()
  {
    if (root.XamlRoot is null || header.ActualWidth <= 0) return;
    try
    {
      var scale = root.XamlRoot.RasterizationScale;
      var left = tabScroll.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(tabScroll.ActualWidth, 0)).X;
      var right = connect.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
      var region = right - left >= 1
        ? new[] { new RectInt32((int)Math.Ceiling(left * scale), 0, (int)((right - left) * scale), (int)(HeaderHeight * scale)) }
        : [];
      InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Caption, region);
    }
    catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Drag region: {error.Message}"); }
  }

  void OnKey(object sender, KeyRoutedEventArgs e)
  {
    if (e.Handled) return;
    var control = Down(VirtualKey.Control);
    if (e.Key == VirtualKey.Escape) { e.Handled = true; Minimize(); }
    // The sink's Paste event owns clipboard ingestion there. Its key event may
    // still bubble after Paste was handled; ingesting again would add two cards.
    else if (control && e.Key == VirtualKey.V && !InSink(e.OriginalSource)) { e.Handled = true; _ = PasteClipboard(); }
    else if (control && e.Key == VirtualKey.N) { e.Handled = true; SelectLocal(); sink.Focus(FocusState.Programmatic); }
    else if (control && e.Key == VirtualKey.O) { e.Handled = true; _ = Attach(); }
  }

  async Task Start()
  {
    try
    {
      store = new PairingStore(dataDirectory, Environment.MachineName);
      selected = store.Device.Id;
      RefreshTabs(); RefreshBoard();
      sink.Focus(FocusState.Programmatic);
      session = await PairingSession.StartAsync(store, synchronize: true);
      if (closing) { await session.DisposeAsync(); session = null; return; }
      connect.IsEnabled = true;
      layout.Keep(new[] { store.Device.Id }.Concat(store.Peers.Select(p => p.Id)));
      watcher = Watch(); RefreshTabs(); presence.Start();
    }
    catch (Exception error)
    {
      Error(store is null ? $"Could not open your board. {error.Message}" : $"Your board is ready, but nearby networking is unavailable. {error.Message}");
    }
  }

  async Task Watch()
  {
    try
    {
      await foreach (var update in session!.Events.ReadAllAsync(lifetime.Token))
        DispatcherQueue.TryEnqueue(() =>
        {
          if (closing) return;
          switch (update.Kind)
          {
            case "nearby": case "removed": RefreshNearby(); break;
            case "status": RefreshTabs(); break;
            case "changes":
              RefreshTabs();
              if (selected == update.Message) RefreshBoard();
              break;
            case "request":
              if (pairing is not null)
              {
                pairMessage.Text = $"{update.Name} wants to connect. Approve only if both screens show the same code.";
                code.Text = update.Code;
                code.Visibility = Visibility.Visible;
                approve.Visibility = decline.Visibility = Visibility.Visible;
              }
              break;
            case "paired": SetPairMessage($"Connected to {update.Name}. Pairing is saved."); RefreshTabs(); RefreshNearby(); break;
            case "error": Error(update.Message ?? "Discovery is unavailable."); break;
          }
        });
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
  }

  sealed record Tab(string Id, Button Button, Ellipse Dot, TextBlock Label, SolidColorBrush Fill, SolidColorBrush Over);
  readonly List<Tab> tabItems = [];

  /// Device tabs: this PC first, then saved peers, each with a presence dot.
  void RefreshTabs()
  {
    if (store is null || closing) return;
    var peers = store.Peers;
    if (selected != store.Device.Id && peers.All(p => p.Id != selected)) { selected = store.Device.Id; RefreshBoard(); }
    var devices = new[] { (store.Device.Id, store.Device.Name, Online: true, Hint: store.Device.Name + " · this PC") }.Concat(peers.Select(p => (p.Id, p.Name, p.Online,
      Hint: p.Online ? p.Name : $"{p.Name} · offline, showing saved items{(p.Error is null ? "" : " · " + p.Error)}"))).ToArray();
    if (!tabItems.Select(t => t.Id).SequenceEqual(devices.Select(d => d.Id)))
    {
      tabStrip.Children.Clear(); tabItems.Clear();
      foreach (var device in devices)
      {
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(dot); content.Children.Add(label);
        var button = new Button { Content = content, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(10, 0, 10, 0),
          BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8) };
        var tab = new Tab(device.Id, button, dot, label, new SolidColorBrush(), new SolidColorBrush());
        Flatten(button, tab.Fill, tab.Over, tab.Over);
        button.Click += (_, _) => Select(tab.Id);
        tabStrip.Children.Add(button); tabItems.Add(tab);
      }
    }
    for (var i = 0; i < devices.Length; i++)
    {
      var (tab, device, active) = (tabItems[i], devices[i], devices[i].Id == selected);
      tab.Dot.Fill = device.Online ? online : offline;
      tab.Label.Text = device.Name; tab.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
      tab.Label.Foreground = active ? ink : secondary;
      tab.Fill.Color = active ? cardFill.Color : Colors.Transparent;
      tab.Over.Color = active ? cardFill.Color : hover.Color;
      AutomationProperties.SetName(tab.Button, $"{device.Hint}{(active ? ", selected" : "")}");
      ToolTipService.SetToolTip(tab.Button, device.Hint);
    }
    UpdateDragRegion();
  }

  void Select(string id)
  {
    if (store is null || selected == id) return;
    selected = id; RefreshTabs(); RefreshBoard();
    tabItems.FirstOrDefault(t => t.Id == id)?.Button.StartBringIntoView();
  }

  void SelectLocal() { if (store is not null) Select(store.Device.Id); }

  void AddDraft() => Try(() =>
  {
    if (store is null || closing || string.IsNullOrWhiteSpace(sink.Text)) return;
    var text = sink.Text;
    store.AddText(text); sink.Text = ""; layout.Draft = ""; layout.Save();
    ShowAdded(); sink.Focus(FocusState.Programmatic);
  });

  void SetPairMessage(string message)
  {
    pairMessage.Text = message; code.Text = ""; code.Visibility = Visibility.Collapsed;
    approve.Visibility = decline.Visibility = Visibility.Collapsed;
  }

  void RefreshNearby()
  {
    if (session is null || pairing is null) return;
    nearbyRows.Children.Clear();
    foreach (var device in session.Nearby.OrderBy(p => p.Name))
    {
      var row = new Grid { ColumnSpacing = 16 };
      row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      row.Children.Add(new TextBlock { Text = device.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
      var button = new Button { Content = "Connect", IsEnabled = joinWork.IsCompleted };
      button.Click += (_, _) =>
      {
        if (!joinWork.IsCompleted) return;
        joinWork = Join(device);
        RefreshNearby();
      };
      row.Children.Add(button); Grid.SetColumn(button, 1); nearbyRows.Children.Add(row);
    }
    if (nearbyRows.Children.Count == 0) nearbyRows.Children.Add(new TextBlock { Text = "Looking for nearby devices…", FontSize = 13, Opacity = 0.65 });
  }

  async Task Join(Nearby device)
  {
    try
    {
      SetPairMessage($"Connecting to {device.Name}…");
      code.Text = await session!.BeginJoinAsync(device.Id, pairCancellation!.Token);
      code.Visibility = Visibility.Visible;
      pairMessage.Text = $"Compare this code on {device.Name}, then approve there. Waiting for approval…";
      await session.CompleteJoinAsync(pairCancellation.Token);
    }
    catch (OperationCanceledException) { }
    catch (Exception error) { SetPairMessage(error.Message); }
    finally { DispatcherQueue.TryEnqueue(RefreshNearby); }
  }

  async Task ShowPairing()
  {
    if (session is null || pairing is not null) return;
    session.OpenPairing(); pairCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    SetPairMessage("Open + on your other device to find it nearby.");
    var body = new StackPanel { Spacing = 14 };
    body.Children.Add(pairMessage); body.Children.Add(code);
    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
    actions.Children.Add(approve); actions.Children.Add(decline); body.Children.Add(actions);
    var nearbyScroll = new ScrollViewer { Content = nearbyRows, MaxHeight = 180 };
    body.Children.Add(nearbyScroll);
    pairing = new ContentDialog { Title = "Connect a device", Content = body, CloseButtonText = "Done", XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme };
    RefreshNearby();
    try { await pairing.ShowAsync(); }
    finally
    {
      pairCancellation.Cancel(); session.ClosePairing(); await joinWork;
      nearbyScroll.Content = null; pairing.Content = null; body.Children.Clear(); actions.Children.Clear();
      pairing = null; pairCancellation.Dispose(); pairCancellation = null;
    }
  }

  async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
  {
    if (canClose) return;
    args.Cancel = true;
    if (closing) return;
    closing = true;
    presence.Stop(); draftSaver.Stop();
    layout.Draft = sink.Text;
    layout.Frame = [restoredFrame.X, restoredFrame.Y, restoredFrame.Width, restoredFrame.Height];
    layout.Save();
    pairing?.Hide(); lifetime.Cancel(); session?.ClosePairing();
    try
    {
      await initialization;
      await pairingWork;
      await joinWork;
      if (session is not null) await session.DisposeAsync();
      if (watcher is not null) await watcher;
    }
    catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Shutdown: {error.Message}"); }
    finally { store?.Dispose(); canClose = true; Close(); }
  }
}

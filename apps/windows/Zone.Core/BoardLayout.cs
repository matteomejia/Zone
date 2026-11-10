using System.Text.Json;

namespace Zone.Core;

/// Where a card sits on this PC's board. Arrangement is local and never synced.
public sealed record Spot(double X, double Y, int Z);

public readonly record struct Box(double X, double Y, double Width, double Height)
{
  public bool Intersects(Box other) =>
    X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
}

/// Card positions and other view-only state, kept in a plain file beside the store.
/// Nothing here is part of the replicated board.
public sealed class BoardLayout
{
  public const double CardWidth = 168, WideWidth = 296, Pad = 12, Gap = 10, Edge = 4;

  sealed class Saved
  {
    public Dictionary<string, Dictionary<string, Spot>> Boards { get; set; } = [];
    public string Draft { get; set; } = "";
    public int[]? Frame { get; set; }
    public bool Topmost { get; set; }
  }

  static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  readonly string? path;
  Saved state = new();

  BoardLayout(string? path) => this.path = path;

  public string Draft { get => state.Draft; set => state.Draft = value ?? ""; }
  /// Window x, y, width and height in screen pixels.
  public int[]? Frame { get => state.Frame; set => state.Frame = value; }
  public bool Topmost { get => state.Topmost; set => state.Topmost = value; }

  /// Opens the layout at `path`. An unreadable file starts a fresh arrangement; `null` keeps it in memory.
  public static BoardLayout Load(string? path)
  {
    var layout = new BoardLayout(path);
    try
    {
      if (path is not null && File.Exists(path) && new FileInfo(path).Length <= 4 * 1024 * 1024)
        layout.state = JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(path), Options) ?? new();
    }
    catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
    layout.state.Boards ??= [];
    layout.state.Draft ??= "";
    foreach (var owner in layout.state.Boards.Keys.ToArray())
    {
      var board = layout.state.Boards[owner];
      if (board is null) { layout.state.Boards.Remove(owner); continue; }
      foreach (var (id, spot) in board.ToArray())
        if (spot is null || !double.IsFinite(spot.X) || !double.IsFinite(spot.Y)) board.Remove(id);
    }
    if (layout.state.Frame is { Length: not 4 }) layout.state.Frame = null;
    return layout;
  }

  public void Save()
  {
    if (path is null) return;
    try
    {
      var temporary = path + ".tmp";
      File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, Options));
      File.Move(temporary, path, overwrite: true);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
  }

  public Spot? Find(string owner, string id) =>
    state.Boards.TryGetValue(owner, out var board) && board.TryGetValue(id, out var spot) ? spot : null;

  /// First gap, reading top to bottom, where a card of this size fits without touching another.
  public static (double X, double Y) FreeSpot(double width, double height, IReadOnlyList<Box> taken, double boardWidth)
  {
    var columns = Math.Max(1, (int)((boardWidth - Pad * 2 + Gap) / (CardWidth + Gap)));
    foreach (var y in taken.Select(box => box.Y + box.Height + Gap).Prepend(Pad).Order())
      for (var column = 0; column < columns; column++)
      {
        var x = Pad + column * (CardWidth + Gap);
        var grown = new Box(x + 1 - Gap, y + 1 - Gap, width + 2 * (Gap - 1), height + 2 * (Gap - 1));
        if (!taken.Any(grown.Intersects)) return (x, y);
      }
    return (Pad, Pad);
  }

  /// Gives every unplaced note on one board a spot and forgets notes that no longer exist.
  /// `drops` are centre points the user dropped onto; they are used first, oldest note first.
  /// Returns the ids placed by this call, oldest first.
  public IReadOnlyList<string> Arrange(
    string owner, IReadOnlyList<Note> notes, double boardWidth, Func<Note, (double Width, double Height)> size,
    Queue<(double X, double Y)>? drops = null)
  {
    if (!state.Boards.TryGetValue(owner, out var board)) state.Boards[owner] = board = [];
    var live = notes.Select(note => note.Id).ToHashSet();
    var changed = false;
    foreach (var id in board.Keys.Where(id => !live.Contains(id)).ToArray()) { board.Remove(id); changed = true; }
    var placed = new List<string>();
    var missing = notes.Where(note => !board.ContainsKey(note.Id)).OrderBy(note => note.CreatedAt).ThenBy(note => note.Revision).ToArray();
    if (missing.Length > 0)
    {
      var taken = notes.Where(note => board.ContainsKey(note.Id)).Select(note =>
      {
        var (width, height) = size(note);
        return new Box(board[note.Id].X, board[note.Id].Y, width, height);
      }).ToList();
      var top = board.Values.Select(spot => spot.Z).DefaultIfEmpty(0).Max();
      foreach (var note in missing)
      {
        var (width, height) = size(note);
        var origin = FreeSpot(width, height, taken, boardWidth);
        if (drops is { Count: > 0 })
        {
          var drop = drops.Dequeue();
          origin = (Math.Max(Edge, drop.X - width / 2), Math.Max(Edge, drop.Y - height / 2));
        }
        board[note.Id] = new Spot(origin.X, origin.Y, ++top);
        taken.Add(new Box(origin.X, origin.Y, width, height));
        placed.Add(note.Id);
      }
      changed = true;
    }
    if (changed) Save();
    return placed;
  }

  public void Move(string owner, string id, double dx, double dy)
  {
    if (Find(owner, id) is not { } spot) return;
    state.Boards[owner][id] = spot with { X = Math.Max(Edge, spot.X + dx), Y = Math.Max(Edge, spot.Y + dy) };
    if (!Raise(owner, id)) Save();
  }

  /// Brings a card to the front. Returns whether anything changed (and was saved).
  public bool Raise(string owner, string id)
  {
    if (Find(owner, id) is not { } spot) return false;
    var top = state.Boards[owner].Values.Max(other => other.Z);
    if (spot.Z >= top && state.Boards[owner].Values.Count(other => other.Z == top) == 1) return false;
    state.Boards[owner][id] = spot with { Z = top + 1 };
    Save();
    return true;
  }

  /// Forgets the arrangement of boards whose device is no longer known.
  public void Keep(IEnumerable<string> owners)
  {
    var known = owners.ToHashSet();
    var stale = state.Boards.Keys.Where(owner => !known.Contains(owner)).ToArray();
    foreach (var owner in stale) state.Boards.Remove(owner);
    if (stale.Length > 0) Save();
  }
}

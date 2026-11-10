using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class BoardLayoutTests
{
  static Note Text(string id, long created) => new(id, "owner", created, created, "text", "note", "", "", 0);
  static (double, double) Size(Note _) => (BoardLayout.CardWidth, 54);

  [Fact]
  public void NewCardsFillColumnsThenRowsWithoutTouching()
  {
    var layout = BoardLayout.Load(null);
    var notes = new[] { Text("a", 1), Text("b", 2), Text("c", 3) };
    Assert.Equal(["a", "b", "c"], layout.Arrange("owner", notes, 392, Size));
    Assert.Equal(new Spot(12, 12, 1), layout.Find("owner", "a"));
    Assert.Equal(new Spot(190, 12, 2), layout.Find("owner", "b"));
    Assert.Equal(new Spot(12, 76, 3), layout.Find("owner", "c"));
    // A narrow board has a single column.
    var narrow = BoardLayout.Load(null);
    narrow.Arrange("owner", notes, 320, Size);
    Assert.Equal(new Spot(12, 76, 2), narrow.Find("owner", "b"));
  }

  [Fact]
  public void DropsMovesAndDeletionsAreRememberedPerBoard()
  {
    var directory = Path.Combine(Path.GetTempPath(), "zone-layout-tests-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try
    {
      var path = Path.Combine(directory, "layout.json");
      var layout = BoardLayout.Load(path);
      var drops = new Queue<(double, double)>([(300, 400)]);
      layout.Arrange("owner", [Text("a", 1), Text("b", 2)], 392, Size, drops);
      Assert.Equal(new Spot(300 - 84, 400 - 27, 1), layout.Find("owner", "a"));
      Assert.Equal(new Spot(12, 12, 2), layout.Find("owner", "b"));
      layout.Move("owner", "a", -1000, 10);
      Assert.Equal(new Spot(BoardLayout.Edge, 383, 3), layout.Find("owner", "a"));
      Assert.False(layout.Raise("owner", "a"));
      Assert.True(layout.Raise("owner", "b"));
      layout.Arrange("other", [Text("a", 1)], 392, Size);
      layout.Draft = "unsent"; layout.Frame = [1, 2, 3, 4]; layout.Topmost = true; layout.Save();

      var reopened = BoardLayout.Load(path);
      Assert.Equal(new Spot(BoardLayout.Edge, 383, 3), reopened.Find("owner", "a"));
      Assert.Equal(4, reopened.Find("owner", "b")!.Z);
      Assert.Equal("unsent", reopened.Draft);
      Assert.Equal([1, 2, 3, 4], reopened.Frame!);
      Assert.True(reopened.Topmost);
      Assert.Empty(reopened.Arrange("owner", [Text("b", 2)], 392, Size));
      Assert.Null(reopened.Find("owner", "a"));
      reopened.Keep(["owner"]);
      Assert.Null(BoardLayout.Load(path).Find("other", "a"));
      Assert.NotNull(BoardLayout.Load(path).Find("owner", "b"));

      File.WriteAllText(path, "{ not json");
      Assert.Null(BoardLayout.Load(path).Find("owner", "b"));
    }
    finally { Directory.Delete(directory, true); }
  }
}

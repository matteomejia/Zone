using Xunit;
using Zone.Core;

namespace Zone.Tests;

public class LocalBoardTests
{
  [Fact]
  public void NotesAndMonotonicDeletionHistorySurviveRestart()
  {
    var directory = Path.Combine(Path.GetTempPath(), "zone-board-tests-" + Guid.NewGuid());
    try
    {
      string owner, deleted;
      using (var store = new PairingStore(directory, "Desktop"))
      {
        owner = store.Device.Id;
        deleted = store.AddText("First note");
        store.AddText("Second note\nwith another line");
        Assert.Equal(2, store.Notes(owner).Count);
        store.DeleteNote(deleted);
        Assert.Single(store.Notes(owner));
        Assert.Throws<CoreException>(() => store.DeleteNote(Guid.NewGuid().ToString("D")));
      }
      using var reopened = new PairingStore(directory, "Desktop");
      Assert.Equal(owner, reopened.Device.Id);
      Assert.Equal("Second note\nwith another line", Assert.Single(reopened.Notes(owner)).Text);
      var id = reopened.AddText("After restart");
      Assert.Equal(4, reopened.Notes(owner).Single(n => n.Id == id).Revision);
      reopened.DeleteNote(deleted); // Deleting an existing tombstone is idempotent.
      Assert.Equal(2, reopened.Notes(owner).Count);
    }
    finally { Directory.Delete(directory, true); }
  }

  [Fact]
  public void RejectsEmptyAndOversizedUtf8NotesWithoutConsumingRevision()
  {
    var directory = Path.Combine(Path.GetTempPath(), "zone-board-tests-" + Guid.NewGuid());
    try
    {
      using var store = new PairingStore(directory, "Desktop");
      Assert.Throws<CoreException>(() => store.AddText("  \n"));
      Assert.Throws<CoreException>(() => store.AddText(new string('界', 400_000)));
      store.AddText("Valid");
      Assert.Equal(1, Assert.Single(store.Notes(store.Device.Id)).Revision);
    }
    finally { Directory.Delete(directory, true); }
  }
}

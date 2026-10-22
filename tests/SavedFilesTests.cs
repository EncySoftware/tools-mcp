using ToolsMcp;
using Xunit;

public class SavedFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tools-saved-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void SavesUnderTheServersNameInTheFolderGiven()
    {
        string path = SavedFiles.Save(new DownloadedFile("Drills_D1-20.mKit", [1, 2, 3]), _folder);

        Assert.Equal(Path.Combine(_folder, "Drills_D1-20.mKit"), path);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    /** A file the person already has is theirs: a second kit of the same name goes beside it. */
    [Fact]
    public void NeverOverwrites()
    {
        string first = SavedFiles.Save(new DownloadedFile("Kit.mKit", [1]), _folder);
        string second = SavedFiles.Save(new DownloadedFile("Kit.mKit", [2]), _folder);
        string third = SavedFiles.Save(new DownloadedFile("Kit.mKit", [3]), _folder);

        Assert.Equal(Path.Combine(_folder, "Kit (2).mKit"), second);
        Assert.Equal(Path.Combine(_folder, "Kit (3).mKit"), third);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
    }

    /** Names come from the catalogue and from people: Ø, slashes, colons and quotes are all in them. */
    [Fact]
    public void MakesANameSafeForWindowsAndKeepsItsLetters()
    {
        Assert.Equal("Ø10 _ ER16_ _test_.mKit", SavedFiles.Safe("Ø10 / ER16: \"test\".mKit"));
        Assert.Equal("Токарка.mKit", SavedFiles.Safe("Токарка.mKit"));
    }

    [Fact]
    public void DefaultFolderIsEncyToolsInDownloads()
    {
        Assert.EndsWith(Path.Combine("Downloads", "ENCY tools"), SavedFiles.DefaultFolder);
    }
}

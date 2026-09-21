using System.Security.Cryptography;
using System.Text.Json;
using BlinkObserverTool.ProfileSync;
using Xunit;

namespace BlinkObserverTool.ProfileSync.Tests;

public sealed class DefaultProfileSynchronizerTests : IDisposable
{
    private readonly string rootDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "TestWorkspaces",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Synchronize_CopiesNewProfilesAndLaterManifestAdditions()
    {
        var source = CreateSource(("default-a", "Profiles/Default A", "original"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();

        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("original", ReadProfile(destination, "Default A"));
        Assert.True(File.Exists(state));

        WriteManifest(source, "2", ("default-a", "Profiles/Default A", "original"), ("default-b", "Profiles/Default B", "new"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("original", ReadProfile(destination, "Default A"));
        Assert.Equal("new", ReadProfile(destination, "Default B"));
    }

    [Fact]
    public void Synchronize_UpdatesAPreviouslyUnmodifiedDefault()
    {
        var source = CreateSource(("default-a", "Profiles/Default A", "version one"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();
        synchronizer.Synchronize(source, destination, state);

        WriteManifest(source, "2", ("default-a", "Profiles/Default A", "version two"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("version two", ReadProfile(destination, "Default A"));
    }

    [Fact]
    public void Synchronize_DoesNotOverwriteAnEditedDefault()
    {
        var source = CreateSource(("default-a", "Profiles/Default A", "version one"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();
        synchronizer.Synchronize(source, destination, state);
        File.WriteAllText(ProfilePath(destination, "Default A"), "user edit");

        WriteManifest(source, "2", ("default-a", "Profiles/Default A", "version two"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("user edit", ReadProfile(destination, "Default A"));
    }

    [Fact]
    public void Synchronize_DoesNotOverwriteAnUntrackedProfileWithTheSamePath()
    {
        var source = CreateSource(("default-a", "Profiles/My Profile", "packaged"));
        var destination = WorkspacePath("destination");
        var destinationProfile = ProfilePath(destination, "My Profile");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationProfile)!);
        File.WriteAllText(destinationProfile, "user-created");

        new DefaultProfileSynchronizer().Synchronize(source, destination, WorkspacePath("state.json"));

        Assert.Equal("user-created", File.ReadAllText(destinationProfile));
    }

    [Fact]
    public void Synchronize_AdoptsAnExistingExactLegacyDefaultAndUpdatesItLater()
    {
        var source = CreateSource(("default-a", "Profiles/Default A", "legacy default"));
        var destination = WorkspacePath("destination");
        var destinationProfile = ProfilePath(destination, "Default A");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationProfile)!);
        File.WriteAllText(destinationProfile, "legacy default");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();

        synchronizer.Synchronize(source, destination, state);
        WriteManifest(source, "2", ("default-a", "Profiles/Default A", "updated default"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("updated default", File.ReadAllText(destinationProfile));
    }

    [Fact]
    public void Synchronize_TreatsAddedFilesAsUserEdits()
    {
        var source = CreateSource(("default-a", "Profiles/Default A", "version one"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();
        synchronizer.Synchronize(source, destination, state);
        File.WriteAllText(Path.Combine(destination, "Profiles", "Default A", "notes.txt"), "user file");

        WriteManifest(source, "2", ("default-a", "Profiles/Default A", "version two"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("version one", ReadProfile(destination, "Default A"));
        Assert.Equal("user file", File.ReadAllText(Path.Combine(destination, "Profiles", "Default A", "notes.txt")));
    }

    [Fact]
    public void Synchronize_MovesAnUnmodifiedDefaultToItsNewManifestPath()
    {
        var source = CreateSource(("default-a", "Profiles/Old Name", "version one"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();
        synchronizer.Synchronize(source, destination, state);

        WriteManifest(source, "2", ("default-a", "Profiles/New Name", "version two"));
        synchronizer.Synchronize(source, destination, state);

        Assert.False(Directory.Exists(Path.Combine(destination, "Profiles", "Old Name")));
        Assert.Equal("version two", ReadProfile(destination, "New Name"));
    }

    [Fact]
    public void Synchronize_PreservesAnEditedOldPathWhenInstallingTheRenamedDefault()
    {
        var source = CreateSource(("default-a", "Profiles/Old Name", "version one"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();
        synchronizer.Synchronize(source, destination, state);
        File.WriteAllText(ProfilePath(destination, "Old Name"), "user edit");

        WriteManifest(source, "2", ("default-a", "Profiles/New Name", "version two"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("user edit", ReadProfile(destination, "Old Name"));
        Assert.Equal("version two", ReadProfile(destination, "New Name"));
    }

    [Fact]
    public void Synchronize_PreservesBothPathsWhenRenameDestinationCollides()
    {
        var source = CreateSource(("default-a", "Profiles/Old Name", "version one"));
        var destination = WorkspacePath("destination");
        var state = WorkspacePath("state.json");
        var synchronizer = new DefaultProfileSynchronizer();
        synchronizer.Synchronize(source, destination, state);
        var collidingProfile = ProfilePath(destination, "New Name");
        Directory.CreateDirectory(Path.GetDirectoryName(collidingProfile)!);
        File.WriteAllText(collidingProfile, "user-created");

        WriteManifest(source, "2", ("default-a", "Profiles/New Name", "version two"));
        synchronizer.Synchronize(source, destination, state);

        Assert.Equal("version one", ReadProfile(destination, "Old Name"));
        Assert.Equal("user-created", ReadProfile(destination, "New Name"));
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private string CreateSource(params (string Id, string RelativePath, string Content)[] profiles)
    {
        var source = WorkspacePath("source");
        WriteManifest(source, "1", profiles);
        return source;
    }

    private static void WriteManifest(
        string source,
        string version,
        params (string Id, string RelativePath, string Content)[] profiles)
    {
        if (Directory.Exists(source))
        {
            Directory.Delete(source, recursive: true);
        }

        Directory.CreateDirectory(source);
        var entries = new List<object>();
        foreach (var profile in profiles)
        {
            var profileDirectory = Path.Combine(source, profile.RelativePath);
            Directory.CreateDirectory(profileDirectory);
            var profilePath = Path.Combine(profileDirectory, "profile.json");
            File.WriteAllText(profilePath, profile.Content);
            entries.Add(new
            {
                id = profile.Id,
                relativePath = profile.RelativePath.Replace('\\', '/'),
                files = new[]
                {
                    new
                    {
                        relativePath = "profile.json",
                        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profilePath)))
                    }
                }
            });
        }

        File.WriteAllText(
            Path.Combine(source, "manifest.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, version, profiles = entries }));
    }

    private string WorkspacePath(string name) => Path.Combine(rootDirectory, name);

    private static string ProfilePath(string destination, string name) =>
        Path.Combine(destination, "Profiles", name, "profile.json");

    private static string ReadProfile(string destination, string name) =>
        File.ReadAllText(ProfilePath(destination, name));
}

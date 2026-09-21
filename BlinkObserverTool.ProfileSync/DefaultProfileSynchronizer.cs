using System.Security.Cryptography;
using System.Text.Json;

namespace BlinkObserverTool.ProfileSync;

public sealed class DefaultProfileSynchronizer
{
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public void Synchronize(string sourceDirectory, string destinationDirectory, string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);

        var manifest = ReadJson<DefaultProfileManifest>(Path.Combine(sourceDirectory, ManifestFileName));
        ValidateManifest(sourceDirectory, manifest);

        Directory.CreateDirectory(destinationDirectory);
        var state = ReadState(stateFilePath);

        foreach (var profile in manifest.Profiles)
        {
            SynchronizeProfile(sourceDirectory, destinationDirectory, manifest.Version, profile, state);
        }

        SaveState(stateFilePath, state);
    }

    private static void SynchronizeProfile(
        string sourceDirectory,
        string destinationDirectory,
        string manifestVersion,
        DefaultProfileManifestEntry profile,
        DefaultProfileSyncState state)
    {
        var sourceProfileDirectory = ResolveContainedPath(sourceDirectory, profile.RelativePath);
        var destinationProfileDirectory = ResolveContainedPath(destinationDirectory, profile.RelativePath);
        state.Profiles.TryGetValue(profile.Id, out var installedProfile);

        if (installedProfile is null)
        {
            if (!Path.Exists(destinationProfileDirectory))
            {
                ReplaceDirectory(sourceProfileDirectory, destinationProfileDirectory);
                state.Profiles[profile.Id] = CreateInstalledProfile(manifestVersion, profile);
                return;
            }

            if (DirectoryMatches(destinationProfileDirectory, profile.Files))
            {
                state.Profiles[profile.Id] = CreateInstalledProfile(manifestVersion, profile);
            }

            return;
        }

        if (!string.Equals(installedProfile.RelativePath, profile.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            MigrateProfilePath(
                sourceProfileDirectory,
                destinationDirectory,
                destinationProfileDirectory,
                manifestVersion,
                profile,
                installedProfile,
                state);
            return;
        }

        if (!Directory.Exists(destinationProfileDirectory)
            || !DirectoryMatches(destinationProfileDirectory, installedProfile.Files))
        {
            return;
        }

        ReplaceDirectory(sourceProfileDirectory, destinationProfileDirectory);
        state.Profiles[profile.Id] = CreateInstalledProfile(manifestVersion, profile);
    }

    private static void MigrateProfilePath(
        string sourceProfileDirectory,
        string destinationDirectory,
        string newProfileDirectory,
        string manifestVersion,
        DefaultProfileManifestEntry profile,
        InstalledDefaultProfile installedProfile,
        DefaultProfileSyncState state)
    {
        var oldProfileDirectory = ResolveContainedPath(destinationDirectory, installedProfile.RelativePath);
        if (!Directory.Exists(oldProfileDirectory)
            || Path.Exists(newProfileDirectory)
            || PathsOverlap(oldProfileDirectory, newProfileDirectory))
        {
            return;
        }

        var oldProfileIsUnmodified = DirectoryMatches(oldProfileDirectory, installedProfile.Files);
        ReplaceDirectory(sourceProfileDirectory, newProfileDirectory);

        if (oldProfileIsUnmodified)
        {
            Directory.Delete(oldProfileDirectory, recursive: true);
        }

        state.Profiles[profile.Id] = CreateInstalledProfile(manifestVersion, profile);
    }

    private static bool PathsOverlap(string firstPath, string secondPath)
    {
        var first = Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var second = Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceDirectory(string sourceDirectory, string destinationDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(destinationDirectory)
            ?? throw new InvalidOperationException($"Cannot determine parent directory for '{destinationDirectory}'.");
        Directory.CreateDirectory(parentDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(parentDirectory, $".default-profile-staging-{operationId}");
        var backupDirectory = Path.Combine(parentDirectory, $".default-profile-backup-{operationId}");
        CopyDirectory(sourceDirectory, stagingDirectory);

        var destinationMoved = false;
        var replacementInstalled = false;
        try
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Move(destinationDirectory, backupDirectory);
                destinationMoved = true;
            }

            Directory.Move(stagingDirectory, destinationDirectory);
            replacementInstalled = true;
        }
        catch
        {
            if (!Directory.Exists(destinationDirectory) && destinationMoved && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, destinationDirectory);
            }

            throw;
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
            if (replacementInstalled)
            {
                DeleteDirectoryIfPresent(backupDirectory);
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static bool DirectoryMatches(string directory, IReadOnlyCollection<DefaultProfileFile> expectedFiles)
    {
        var actualFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => NormalizeRelativePath(Path.GetRelativePath(directory, path)),
                path => ComputeSha256(path),
                StringComparer.OrdinalIgnoreCase);

        return actualFiles.Count == expectedFiles.Count
            && expectedFiles.All(file =>
                actualFiles.TryGetValue(NormalizeRelativePath(file.RelativePath), out var hash)
                && string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateManifest(string sourceDirectory, DefaultProfileManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported default-profile manifest schema version '{manifest.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException("The default-profile manifest version is required.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in manifest.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
            {
                throw new InvalidDataException($"Default-profile ID '{profile.Id}' is empty or duplicated.");
            }

            var sourceProfileDirectory = ResolveContainedPath(sourceDirectory, profile.RelativePath);
            if (!paths.Add(sourceProfileDirectory))
            {
                throw new InvalidDataException($"Default-profile path '{profile.RelativePath}' is duplicated.");
            }

            if (!Directory.Exists(sourceProfileDirectory) || profile.Files.Count == 0)
            {
                throw new InvalidDataException($"Default-profile source '{profile.RelativePath}' is missing or empty.");
            }

            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in profile.Files)
            {
                var normalizedPath = NormalizeRelativePath(file.RelativePath);
                if (!filePaths.Add(normalizedPath))
                {
                    throw new InvalidDataException($"File '{file.RelativePath}' is duplicated in profile '{profile.Id}'.");
                }

                var sourceFilePath = ResolveContainedPath(sourceProfileDirectory, file.RelativePath);
                if (!File.Exists(sourceFilePath)
                    || !string.Equals(ComputeSha256(sourceFilePath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"File '{file.RelativePath}' in profile '{profile.Id}' is missing or has an invalid hash.");
                }
            }

            var sourceFileCount = Directory.EnumerateFiles(sourceProfileDirectory, "*", SearchOption.AllDirectories).Count();
            if (sourceFileCount != profile.Files.Count)
            {
                throw new InvalidDataException($"Profile '{profile.Id}' contains files not declared in the manifest.");
            }
        }
    }

    private static string ResolveContainedPath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Path '{relativePath}' must be a non-empty relative path.");
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes its root directory.");
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static DefaultProfileSyncState ReadState(string stateFilePath)
    {
        if (!File.Exists(stateFilePath))
        {
            return new DefaultProfileSyncState();
        }

        try
        {
            var state = ReadJson<DefaultProfileSyncState>(stateFilePath);
            if (state.SchemaVersion != 1)
            {
                return new DefaultProfileSyncState();
            }

            state.Profiles = new Dictionary<string, InstalledDefaultProfile>(
                state.Profiles,
                StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException)
        {
            return new DefaultProfileSyncState();
        }
    }

    private static T ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidDataException($"JSON file '{path}' is empty.");
    }

    private static void SaveState(string stateFilePath, DefaultProfileSyncState state)
    {
        var directory = Path.GetDirectoryName(stateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var newStatePath = stateFilePath + ".new";
        using (var stream = File.Create(newStatePath))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
        }

        File.Move(newStatePath, stateFilePath, overwrite: true);
    }

    private static InstalledDefaultProfile CreateInstalledProfile(string manifestVersion, DefaultProfileManifestEntry profile) =>
        new()
        {
            ManifestVersion = manifestVersion,
            RelativePath = profile.RelativePath,
            Files = profile.Files.Select(file => new DefaultProfileFile
            {
                RelativePath = file.RelativePath,
                Sha256 = file.Sha256
            }).ToList()
        };

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class DefaultProfileManifest
    {
        public int SchemaVersion { get; set; }
        public string Version { get; set; } = "";
        public List<DefaultProfileManifestEntry> Profiles { get; set; } = [];
    }

    private sealed class DefaultProfileManifestEntry
    {
        public string Id { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public List<DefaultProfileFile> Files { get; set; } = [];
    }

    private sealed class DefaultProfileSyncState
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, InstalledDefaultProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InstalledDefaultProfile
    {
        public string ManifestVersion { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public List<DefaultProfileFile> Files { get; set; } = [];
    }

    private sealed class DefaultProfileFile
    {
        public string RelativePath { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}

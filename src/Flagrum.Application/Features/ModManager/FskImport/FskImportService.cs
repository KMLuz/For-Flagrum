using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Flagrum.Abstractions;
using Flagrum.Abstractions.AssetExplorer;
using Flagrum.Abstractions.ModManager.Project;
using Flagrum.Abstractions.ModManager.Instructions;
using Flagrum.Application.Features.ModManager.Data;
using Flagrum.Application.Features.ModManager.Instructions;
using Flagrum.Application.Features.ModManager.Project;
using Flagrum.Application.Features.ModManager.Services;
using Flagrum.Core.Utilities;
using Injectio.Attributes;
using Microsoft.Extensions.Logging;

namespace Flagrum.Application.Features.ModManager.FskImport;

public enum FskBuildStatus
{
    /// <summary>No mod has been made from this build yet.</summary>
    New,

    /// <summary>The build has been re-exported since its mod was last synced.</summary>
    Changed,

    /// <summary>The mod matches the build.</summary>
    UpToDate,

    /// <summary>The build can't be imported, see <see cref="FskBuild.Problems" />.</summary>
    Problem
}

/// <summary>
/// A build found in the builds folder, along with the mod made from it (if any).
/// </summary>
public record FskBuildInfo(FskBuild Build, IFlagrumProject Mod, FskBuildStatus Status);

/// <summary>
/// Turns FSK add-on build folders (and zips of them) into Flagrum mods, and keeps those mods in sync with the builds.
/// </summary>
[RegisterScoped]
public class FskImportService(
    IProfileService profile,
    IFileIndex fileIndex,
    IModBuildInstructionFactory instructionFactory,
    ModManagerServiceBase modManager,
    ModMetadataService metadata,
    ILogger<FskImportService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() {WriteIndented = true};

    private FskImportState _state;

    private string StatePath => Path.Combine(profile.FlagrumDirectory, "Profiles", profile.Current.Id.ToString(),
        "fsk_builds.json");

    /// <summary>
    /// Where builds from zips are unpacked to, since the mods use their files in place.
    /// </summary>
    private string ZipBuildsDirectory => Path.Combine(profile.FlagrumDirectory, "fsk_builds",
        profile.Current.Id.ToString());

    /// <summary>
    /// The folder the add-on exports builds into (e.g. <c>H:\...\Frey\Builds</c>), or null if not chosen yet.
    /// </summary>
    public string BuildsFolder
    {
        get => State.BuildsFolder;
        set
        {
            State.BuildsFolder = value;
            SaveState();
        }
    }

    private FskImportState State => _state ??= LoadState();

    public bool IsFskMod(Guid modId) => State.Links.ContainsKey(modId);

    public string GetBuildFolder(Guid modId) => State.Links.TryGetValue(modId, out var link) ? link.FolderPath : null;

    public DateTime? GetSyncedAt(Guid modId) => State.Links.TryGetValue(modId, out var link) ? link.SyncedAt : null;

    /// <summary>
    /// Reads the build a mod was made from, or returns null if the mod isn't from a build or the folder is gone.
    /// </summary>
    public FskBuild TryLoadBuild(Guid modId)
    {
        var folder = GetBuildFolder(modId);
        if (folder == null || !FskBuild.IsBuildFolder(folder))
        {
            return null;
        }

        try
        {
            return FskBuild.Load(folder);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read FSK build at {Folder}", folder);
            return null;
        }
    }

    public FskBuildStatus GetStatus(FskBuild build, IFlagrumProject mod)
    {
        if (build.Problems.Count > 0)
        {
            return FskBuildStatus.Problem;
        }

        if (mod == null)
        {
            return FskBuildStatus.New;
        }

        return State.Links.TryGetValue(mod.Identifier, out var link) && link.Fingerprint == build.Fingerprint
            ? FskBuildStatus.UpToDate
            : FskBuildStatus.Changed;
    }

    /// <summary>
    /// Finds every build in the builds folder.
    /// </summary>
    public List<FskBuildInfo> ScanBuildsFolder()
    {
        if (string.IsNullOrWhiteSpace(BuildsFolder) || !Directory.Exists(BuildsFolder))
        {
            return [];
        }

        // Allow picking a single build folder as well as the folder containing them
        var folders = FskBuild.IsBuildFolder(BuildsFolder)
            ? [BuildsFolder]
            : Directory.EnumerateDirectories(BuildsFolder).Where(FskBuild.IsBuildFolder);

        var result = new List<FskBuildInfo>();
        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var build = FskBuild.Load(folder);
                var mod = FindModForBuild(build);
                result.Add(new FskBuildInfo(build, mod, GetStatus(build, mod)));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to read FSK build at {Folder}", folder);
            }
        }

        return result;
    }

    /// <summary>
    /// The mod previously made from this build folder, or else a mod with the same name as the build.
    /// </summary>
    public IFlagrumProject FindModForBuild(FskBuild build)
    {
        var linked = State.Links
            .Where(kvp => IOHelper.ComparePaths(kvp.Value.FolderPath, build.FolderPath))
            .Select(kvp => modManager.Projects.GetValueOrDefault(kvp.Key))
            .FirstOrDefault(mod => mod != null);

        return linked ?? modManager.Projects.Values
            .FirstOrDefault(mod => mod.Name.Equals(build.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the build a mod was made from has been re-exported since the mod was last synced.
    /// </summary>
    public bool HasBuildChanged(Guid modId)
    {
        var build = TryLoadBuild(modId);
        return build != null && build.Problems.Count == 0
                             && State.Links.TryGetValue(modId, out var link)
                             && link.Fingerprint != build.Fingerprint;
    }

    /// <summary>
    /// Whether a mod was made from a zipped build (as opposed to a folder the add-on exported to).
    /// </summary>
    public bool IsFromZip(Guid modId)
    {
        var folder = GetBuildFolder(modId);
        return folder != null && IOHelper.DoesPathStartWith(folder, ZipBuildsDirectory);
    }

    /// <summary>
    /// Creates a mod from a build, or updates the existing one. If the existing mod is enabled,
    /// it is turned off and back on so the game uses the new files straight away.
    /// </summary>
    /// <param name="build">The build to import.</param>
    /// <param name="newModName">
    /// If set, always creates a new mod with this name instead of updating an existing one.
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown if the build has problems or a game file can't be found.</exception>
    public async Task<IFlagrumProject> ImportAsync(FskBuild build, string newModName = null)
    {
        if (build.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The build \"{build.Name}\" can't be imported:\n" + string.Join("\n", build.Problems));
        }

        // Work out which game archive holds each replaced file before touching anything
        var archivePaths = new Dictionary<string, string>();
        var unknownFiles = new List<string>();
        foreach (var entry in build.Entries)
        {
            var relativePath = fileIndex.GetArchiveRelativePathByUri(entry.Uri);
            if (relativePath == null)
            {
                unknownFiles.Add(entry.Uri);
            }
            else
            {
                archivePaths[entry.Uri] = relativePath;
            }
        }

        if (unknownFiles.Count > 0)
        {
            throw new InvalidOperationException(
                $"Flagrum couldn't find these game files for \"{build.Name}\". Check the paths in entries.txt, " +
                "or rebuild the file index in Settings:\n" + string.Join("\n", unknownFiles));
        }

        var mod = newModName == null ? FindModForBuild(build) : null;
        var isNew = mod == null;
        var wasEnabled = !isNew && modManager.ModsState.GetActive(mod.Identifier);

        if (wasEnabled)
        {
            // Revert using the old file list before replacing it
            await modManager.DisableMod(mod);
        }

        if (isNew)
        {
            mod = new FlagrumProject
            {
                Identifier = Guid.NewGuid(),
                Name = newModName ?? build.Name,
                Author = "FSK",
                Description = build.Outfit == null
                    ? "Imported from an FSK build."
                    : $"Imported from an FSK build of outfit {build.Outfit}."
            };
        }

        mod.Archives.Clear();
        foreach (var group in build.Entries.GroupBy(e => archivePaths[e.Uri], StringComparer.OrdinalIgnoreCase))
        {
            var archive = new FlagrumProjectArchive {RelativePath = group.Key};
            foreach (var entry in group)
            {
                var instruction = instructionFactory.Create<ReplacePackedFileBuildInstruction>();
                instruction.Uri = entry.Uri;
                instruction.FilePath = entry.FilePath;
                instruction.FileLastModified = File.GetLastWriteTime(entry.FilePath).Ticks;
                archive.Instructions.Add(instruction);
            }

            mod.Archives.Add(archive);
        }

        var modDirectory = Path.Combine(profile.ModFilesDirectory, mod.Identifier.ToString());
        IOHelper.EnsureDirectoryExists(modDirectory);

        if (isNew)
        {
            SetDefaultThumbnail(mod.Identifier, modDirectory);
            modManager.ModsState.Add(mod.Identifier, new ModState());
            metadata.SetCategory(mod.Identifier, ModListCategory.Outfit);
        }

        // Make sure the new files get built rather than reusing the previous build's output
        modManager.ClearCachedFilesForMod(mod.Identifier);
        await modManager.SaveBuildList(mod);

        State.Links[mod.Identifier] = new FskBuildLink
        {
            FolderPath = build.FolderPath,
            Fingerprint = build.Fingerprint,
            SyncedAt = DateTime.Now
        };
        SaveState();

        if (wasEnabled)
        {
            await modManager.EnableMod(mod);
        }

        return mod;
    }

    /// <summary>
    /// Whether a zip contains an FSK build rather than a Flagrum mod.
    /// </summary>
    public static bool IsFskZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return FindEntriesFile(zip) != null;
    }

    /// <summary>
    /// Unpacks a zipped build into Flagrum's own folder, replacing any earlier copy of the same build.
    /// </summary>
    /// <returns>The unpacked build, ready to import.</returns>
    public FskBuild ExtractZip(string zipPath)
    {
        string buildFolder;

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entriesFile = FindEntriesFile(zip)
                              ?? throw new InvalidOperationException("This zip doesn't contain an FSK build.");

            // The build is either the zip's root or a single folder inside it
            var prefix = entriesFile.FullName[..^FskBuild.EntriesFileName.Length];
            var buildName = prefix.Length > 0
                ? prefix.TrimEnd('/', '\\').Split('/', '\\')[^1]
                : Path.GetFileNameWithoutExtension(zipPath);

            buildFolder = Path.Combine(ZipBuildsDirectory, buildName);
            if (Directory.Exists(buildFolder))
            {
                Directory.Delete(buildFolder, true);
            }

            Directory.CreateDirectory(buildFolder);
            var root = Path.GetFullPath(buildFolder) + Path.DirectorySeparatorChar;

            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Name.Length > 0))
            {
                var destination = Path.GetFullPath(Path.Combine(buildFolder, entry.FullName[prefix.Length..]));

                // Never write outside the build folder, whatever paths the zip contains
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }

        return FskBuild.Load(buildFolder);
    }

    /// <summary>
    /// Forgets which build a mod came from, such as after the mod is deleted.
    /// </summary>
    public void Unlink(Guid modId)
    {
        if (State.Links.Remove(modId))
        {
            SaveState();
        }
    }

    private static ZipArchiveEntry FindEntriesFile(ZipArchive zip) => zip.Entries
        .Where(e => e.Name.Equals(FskBuild.EntriesFileName, StringComparison.OrdinalIgnoreCase))
        .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
        .FirstOrDefault(e => e.FullName.Count(c => c is '/' or '\\') <= 1);

    private void SetDefaultThumbnail(Guid modId, string modDirectory)
    {
        try
        {
            var defaultImage = Path.Combine(IOHelper.GetExecutingDirectory(), "Resources", "earc.png");
            File.Copy(defaultImage, Path.Combine(modDirectory, "thumbnail.jpg"), true);
            File.Copy(defaultImage, Path.Combine(profile.ImagesDirectory, $"{modId}.jpg"), true);
        }
        catch (Exception exception)
        {
            // A missing picture shouldn't stop the import
            logger.LogWarning(exception, "Failed to set the default picture for mod {ModId}", modId);
        }
    }

    private FskImportState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                return JsonSerializer.Deserialize<FskImportState>(File.ReadAllText(StatePath), JsonOptions)
                       ?? new FskImportState();
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read FSK import state from {Path}", StatePath);
        }

        return new FskImportState();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(State, JsonOptions));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save FSK import state to {Path}", StatePath);
        }
    }

    private class FskImportState
    {
        public string BuildsFolder { get; set; }
        public Dictionary<Guid, FskBuildLink> Links { get; set; } = [];
    }

    private class FskBuildLink
    {
        public string FolderPath { get; set; }
        public string Fingerprint { get; set; }
        public DateTime SyncedAt { get; set; }
    }
}

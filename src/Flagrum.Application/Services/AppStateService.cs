using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flagrum.Abstractions;
using Flagrum.Abstractions.AssetExplorer;
using Flagrum.Core.Archive;
using Flagrum.Core.Utilities;
using Flagrum.Application.Features.AssetExplorer.Indexing;
using Flagrum.Application.Features.WorkshopMods.Data;
using FileIndexNode = Flagrum.Application.Features.AssetExplorer.Indexing.FileIndexNode;

namespace Flagrum.Application.Services;

public class AppStateService
{
    private readonly IConfiguration _configuration;
    private readonly IFileIndex _fileIndex;
    private readonly IProfileService _profile;

    public AppStateService(IProfileService profile, IConfiguration configuration, IFileIndex fileIndex)
    {
        _profile = profile;
        _configuration = configuration;
        _fileIndex = fileIndex;
        FileIndexNode.AppState = this;

        // Re-check the file index whenever it finishes regenerating
        _fileIndex.OnIsRegeneratingChanged += isRegenerating =>
        {
            if (!isRegenerating)
            {
                CheckFileIndex();
            }
        };
    }

    /// <summary>
    /// Archives listed in the file index that are missing from the game files or were built by Flagrum.
    /// Paths are relative to the game data directory.
    /// </summary>
    public IReadOnlyList<string> FileIndexProblems { get; private set; } = [];

    /// <summary>
    /// Raised when <see cref="FileIndexProblems" /> changes.
    /// </summary>
    public event Action OnFileIndexProblemsChanged;

    public Binmod ActiveMod { get; set; }
    public IList<Binmod> Mods { get; set; } = new List<Binmod>();
    public IList<ModlistEntry> UnmanagedEntries { get; set; } = new List<ModlistEntry>();
    public bool IsModListInitialized { get; set; }

    public IAssetExplorerNode CurrentGameViewNode { get; set; }

    public int ActiveCategoryFilter { get; set; }
    public int ActiveModTypeFilter { get; set; } = -1;

    public bool Is3DViewerOpen { get; set; }
    public bool IsModalOpen { get; set; }

    public string GetCurrentAssetExplorerPath()
    {
        var path = _configuration.Get<string>(StateKey.CurrentAssetExplorerPath);
        if (path == null || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        return path;
    }

    public void UpdateBinmodList()
    {
        var fakeMods = UnmanagedEntries.Select(e => new Binmod
        {
            // These are the only properties written to the list
            IsWorkshopMod = e.IsWorkshopMod,
            Path = e.Path,
            IsApplyToGame = e.IsEnabled,
            Index = e.Index
        });

        var entries = Mods.Union(fakeMods);
        var modList = ModlistEntry.FromFile(_profile.Current.BinmodListPath);

        var fixIdMap = modList
            .DistinctBy(m => m.Path)
            .ToDictionary(m => m.Path, m => m.Index);

        ModlistEntry.ToFile(_profile.Current.BinmodListPath, entries, fixIdMap);
    }

    public void LoadNodes()
    {
        if (_profile.IsReady)
        {
            // Run this separately as it shouldn't delay the startup
            ThreadHelper.RunOnNewThread(async () =>
            {
                if (_fileIndex.IsEmpty)
                {
                    _fileIndex.Regenerate();
                }
                else
                {
                    // Archives used to be preloaded here, but nothing used the preloaded archives, and keeping
                    // them open stopped the mod manager from writing to c000.earc and deleting mod archives
                    await Task.Run(CheckFileIndex);
                }
            });
        }
    }

    /// <summary>
    /// Finds archives in the file index that no longer match the game files, such as after a game update,
    /// a Steam file verification, or the game path being changed to a different install.
    /// </summary>
    public void CheckFileIndex()
    {
        var problems = ((FileIndex)_fileIndex).Archives.Values
            .Select(archive => archive.RelativePath)
            .Where(relativePath => IsFlagrumModArchive(relativePath)
                                   || !File.Exists(Path.Combine(_profile.GameDataDirectory, relativePath)))
            .ToList();

        if (problems.Count > 0 || FileIndexProblems.Count > 0)
        {
            FileIndexProblems = problems;
            OnFileIndexProblemsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Whether the given archive is one Flagrum built for a Forspoken mod, rather than part of the game.
    /// </summary>
    /// <param name="relativePath">Path of the archive relative to the game data directory.</param>
    public bool IsFlagrumModArchive(string relativePath) =>
        _profile.Current.Type == LuminousGame.Forspoken
        && relativePath.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase);

    public byte[] GetFileByUri(string uri)
    {
        uri = uri.ToLower();

        var file = ((FileIndex)_fileIndex)[uri];
        if (file == null)
        {
            return [];
        }

        if (file.Archive == null)
        {
            var absolutePath = Path.Combine(_profile.GameDataDirectory, uri.Replace("data://", ""));
            return File.Exists(absolutePath) ? File.ReadAllBytes(absolutePath) : [];
        }
        else
        {
            var relativePath = _fileIndex.GetArchiveRelativePathByUri(uri);
            if (relativePath == null)
            {
                return [];
            }

            // The file index can be out of date, in which case the user is prompted to regenerate it
            var absolutePath = Path.Combine(_profile.GameDataDirectory, relativePath);
            if (!File.Exists(absolutePath))
            {
                return [];
            }

            using var archive = new EbonyArchive(absolutePath);
            return archive[uri].GetReadableData();
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Flagrum.Abstractions;
using Flagrum.Abstractions.Archive;
using Flagrum.Abstractions.AssetExplorer;
using Flagrum.Abstractions.ModManager.Instructions;
using Flagrum.Abstractions.ModManager.Project;
using Flagrum.Core.Archive;
using Flagrum.Core.Utilities;
using Cryptography = Flagrum.Core.Utilities.Cryptography;
using Flagrum.Application.Features.AssetExplorer.Indexing;
using Flagrum.Application.Features.ModManager.Instructions;
using Flagrum.Application.Features.ModManager.Instructions.Abstractions;
using Flagrum.Application.Features.ModManager.Mod;
using Microsoft.Extensions.Logging;

namespace Flagrum.Application.Features.ModManager.Services;

/// <summary>
/// Forspoken loads every file through the redirect table in c000.earc (original file -> file to load), and the
/// game itself uses it for most of its files. Mods add redirects to their own archives in datas\mods.
/// </summary>
/// <remarks>
/// The table is never patched bit by bit. Every time a mod is turned on or off, it is rebuilt from the game's own
/// redirects plus every enabled mod, so nothing is left pointing at a mod that is off or gone, and turning one
/// mod off can't break another.
/// </remarks>
public class ForspokenModManager : ModManagerServiceBase
{
    private const string ErepUri = "data://c000.erep";
    private const string ModReferencePrefix = "data://mods/";
    private const string ModReferenceSuffix = ".ebex@";

    private readonly ILogger<ForspokenModManager> _logger;

    public ForspokenModManager(
        IProfileService profile,
        IFileIndex fileIndex,
        AssetConverter assetConverter,
        IServiceProvider provider,
        ILogger<ForspokenModManager> logger)
        : base(profile, fileIndex, assetConverter, provider)
    {
        _logger = logger;
        Directory.CreateDirectory(ModsDirectory);
    }

    private string C000Path => Path.Combine(_profile.GameDataDirectory, "c000.earc");
    private string BackupPath => Path.Combine(_profile.GameDataDirectory, "c000.backup");
    private string ModsDirectory => Path.Combine(_profile.GameDataDirectory, "mods");
    private string GetModArchivePath(Guid modId) => Path.Combine(ModsDirectory, $"{modId}.earc");

    protected override Task ApplyMod(IFlagrumProject mod, EbonyArchiveManager archiveManager)
    {
        // Fail before changing anything if the game has these files open
        GameFileGuard.EnsureNotInUse(C000Path, GetModArchivePath(mod.Identifier));

        WriteModArchive(mod);

        // The mod being turned on goes last, so it wins if another enabled mod replaces the same file
        WriteRedirects(GetEnabledMods(mod.Identifier).Append(mod).ToList());
        return Task.CompletedTask;
    }

    protected override Task RevertMod(IFlagrumProject mod)
    {
        var modPath = GetModArchivePath(mod.Identifier);

        // Fail before changing anything if the game has these files open
        GameFileGuard.EnsureNotInUse(C000Path, modPath);

        WriteRedirects(GetEnabledMods(mod.Identifier).ToList());

        // c000.earc no longer points at the mod archive, so the mod is already off at this point
        // A leftover archive is harmless and gets overwritten the next time the mod is enabled
        try
        {
            if (File.Exists(modPath))
            {
                File.Delete(modPath);
            }
        }
        catch (IOException)
        {
            // Ignore, see above
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore, see above
        }

        return Task.CompletedTask;
    }

    public override Guid[] Reset()
    {
        var enabledMods = Projects.Values
            .Where(p => ModsState.GetActive(p.Identifier))
            .Select(p => p.Identifier)
            .ToArray();

        // Fail before changing anything if the game has any of these files open
        var modFiles = Directory.Exists(ModsDirectory)
            ? Directory.GetFiles(ModsDirectory, "*", SearchOption.AllDirectories)
            : [];
        GameFileGuard.EnsureNotInUse([C000Path, ..modFiles]);

        // Put back the game's own redirects only. This deliberately doesn't copy c000.backup over c000.earc,
        // as a backup taken while other mods were applied would bring their redirects back.
        WriteRedirects([]);

        // Delete all mod files
        if (Directory.Exists(ModsDirectory))
        {
            Directory.Delete(ModsDirectory, true);
        }

        Directory.CreateDirectory(ModsDirectory);

        // Mark all mods as disabled
        ModsState.SetAllInactive();

        return enabledMods;
    }

    /// <inheritdoc />
    public override bool EnsureEnabledModsApplied()
    {
        if (!File.Exists(C000Path))
        {
            return false;
        }

        var enabled = GetEnabledMods(null).ToList();
        HashSet<Guid> applied;
        using (var archive = new EbonyArchive(C000Path))
        {
            applied = GetAppliedModIds(archive);
        }

        var expected = enabled.Select(m => m.Identifier).ToHashSet();
        if (applied.SetEquals(expected) && enabled.All(m => File.Exists(GetModArchivePath(m.Identifier))))
        {
            return false;
        }

        _logger.LogWarning("c000.earc has mods [{Applied}] applied but [{Enabled}] are enabled, re-applying them",
            string.Join(", ", applied), string.Join(", ", expected));

        GameFileGuard.EnsureNotInUse(C000Path);
        WriteRedirects(enabled);
        return true;
    }

    /// <summary>
    /// The enabled mods in the order they are applied (later ones win where two replace the same file).
    /// </summary>
    private IEnumerable<IFlagrumProject> GetEnabledMods(Guid? except) => Projects.Values
        .Where(p => p.Identifier != except && ModsState.GetActive(p.Identifier))
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => p.Identifier);

    /// <summary>
    /// Name of the file in the mod's archive that replaces the given game file. It has the same shape as the names
    /// older versions gave these files (data://mods/&lt;guid&gt;.&lt;extension&gt;), but the GUID is derived from the mod
    /// and the file it replaces, so the redirect table can be rebuilt without rewriting the mod's archive.
    /// </summary>
    private static string GetModFileUri(IFlagrumProject mod, IPackedBuildInstruction file)
    {
        var guid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{mod.Identifier:N}|{file.Uri}")));
        return $"{ModReferencePrefix}{guid}.{file.Uri.Split('.').Last()}";
    }

    /// <summary>
    /// Writes the mod's archive to datas\mods from its cached, processed files.
    /// </summary>
    private void WriteModArchive(IFlagrumProject mod)
    {
        // The folder is gone after a reset, and only gets created again at startup otherwise
        Directory.CreateDirectory(ModsDirectory);

        using var modArchive = new EbonyArchive(false);
        foreach (var file in mod.Archives.SelectMany(e => e.Instructions))
        {
            switch (file)
            {
                case AddPackedFileBuildInstruction:
                case ReplacePackedFileBuildInstruction:
                    var packedAssetInstruction = (PackedAssetBuildInstruction)file;
                    var hash = Cryptography.HashFileUri64(file.Uri);
                    var cachePath = $@"{_profile.CacheDirectory}\{mod.Identifier}{hash}.ffg";
                    var fragment = new FmodFragment();
                    fragment.Read(packedAssetInstruction.FilePath.EndsWith(".ffg")
                        ? packedAssetInstruction.FilePath
                        : cachePath);
                    modArchive.AddProcessedFile(GetModFileUri(mod, file), fragment.Flags, fragment.Data,
                        fragment.OriginalSize, fragment.Key, fragment.RelativePath);
                    break;
                case AddReferenceBuildInstruction:
                    throw new Exception(
                        "Forspoken doesn't need references since the whole earc tree is loaded on startup");
                case RemovePackedFileBuildInstruction:
                    // Removals are handled purely via EREP, so there's nothing to do here
                    break;
                default:
                    throw new Exception($"Unsupported build instruction {file.GetType().Name}");
            }
        }

        modArchive.WriteToFile(GetModArchivePath(mod.Identifier), LuminousGame.Forspoken);
    }

    /// <summary>
    /// The redirects a mod adds: original file -> file in the mod's archive, or 0 for a removed file.
    /// Rewrites the mod's archive first if it is missing or was written by an older version of Flagrum.
    /// </summary>
    private Dictionary<ulong, ulong> GetModRedirects(IFlagrumProject mod)
    {
        var files = mod.Archives.SelectMany(e => e.Instructions).ToList();
        if (!IsModArchiveCurrent(mod, files))
        {
            _logger.LogInformation("Rewriting the archive of {Mod} ({Id})", mod.Name, mod.Identifier);
            using (var archiveManager = new EbonyArchiveManager())
            {
                if (!CheckModIsCached(mod))
                {
                    BuildAssetCache(mod, archiveManager);
                }
            }

            WriteModArchive(mod);
        }

        var redirects = new Dictionary<ulong, ulong>();
        foreach (var file in files)
        {
            var original = Cryptography.HashFileUri64(file.Uri);
            switch (file)
            {
                case ReplacePackedFileBuildInstruction:
                    redirects[original] = new AssetId(GetModFileUri(mod, file)).Value;
                    break;
                case RemovePackedFileBuildInstruction:
                    redirects[original] = 0;
                    break;
            }
        }

        return redirects;
    }

    private bool IsModArchiveCurrent(IFlagrumProject mod, List<IPackedBuildInstruction> files)
    {
        var path = GetModArchivePath(mod.Identifier);
        if (!File.Exists(path))
        {
            return false;
        }

        using var archive = new EbonyArchive(path);
        return files
            .Where(f => f is AddPackedFileBuildInstruction or ReplacePackedFileBuildInstruction)
            .All(f => archive.Files.ContainsKey(new AssetId(GetModFileUri(mod, f))));
    }

    /// <summary>
    /// Rebuilds the redirect table in c000.earc from the game's own redirects plus the given mods, and makes
    /// c000.earc load exactly those mods' archives.
    /// </summary>
    private void WriteRedirects(List<IFlagrumProject> mods)
    {
        var modRedirects = mods.Select(GetModRedirects).ToList();
        var gameRedirects = LoadGameRedirects();

        // Same order as the game's table, with any new entries at the end
        var redirects = new Dictionary<ulong, ulong>(gameRedirects);
        var gameKeysByTarget = gameRedirects.ToLookup(p => p.Value, p => p.Key);
        foreach (var (original, replacement) in modRedirects.SelectMany(r => r))
        {
            redirects[original] = replacement;

            // Files the game itself redirects to the replaced file follow it too
            foreach (var key in gameKeysByTarget[original])
            {
                redirects[key] = replacement;
            }
        }

        using var archive = new EbonyArchive(C000Path);
        archive[ErepUri].SetRawData(new EbonyReplace([]) {Replacements = redirects}.ToArray());

        foreach (var uri in archive.Files.Values.Select(f => f.Uri).Where(IsModReference).ToList())
        {
            archive.RemoveFile(uri);
        }

        foreach (var mod in mods)
        {
            archive.AddFile($"{ModReferencePrefix}{mod.Identifier}{ModReferenceSuffix}",
                EbonyArchiveFileFlags.Autoload | EbonyArchiveFileFlags.Reference,
                []);
        }

        archive.WriteToSource(LuminousGame.Forspoken);
        _logger.LogInformation("Rebuilt the redirect table with {Count} mod(s): {Mods}", mods.Count,
            string.Join(", ", mods.Select(m => m.Name)));
    }

    /// <summary>
    /// The game's own redirects, without any mods.
    /// </summary>
    private Dictionary<ulong, ulong> LoadGameRedirects()
    {
        Dictionary<ulong, ulong> current;
        bool hasMods;
        using (var archive = new EbonyArchive(C000Path))
        {
            current = ReadRedirects(archive);
            hasMods = GetAppliedModIds(archive).Count > 0;
        }

        if (!hasMods)
        {
            // No mods are applied, so c000.earc is the game's own (for example after a game update, or after
            // Steam checked the game files). Keep it as the copy to rebuild from.
            File.Copy(C000Path, BackupPath, true);
            return WithoutModRedirects(current);
        }

        if (File.Exists(BackupPath))
        {
            using var backup = new EbonyArchive(BackupPath);
            return WithoutModRedirects(ReadRedirects(backup));
        }

        _logger.LogWarning("c000.backup is missing and mods are applied to c000.earc, using its redirects without "
                           + "the ones mods added");
        return WithoutModRedirects(current);
    }

    private static Dictionary<ulong, ulong> ReadRedirects(EbonyArchive archive) =>
        new EbonyReplace(archive[ErepUri].GetReadableData()).Replacements;

    /// <summary>
    /// Leaves out redirects mods added. The game only redirects files to files of the same type, while mods
    /// redirect a source file (e.g. .tif) to a built one (e.g. .btex) or remove it (0). This also cleans up a
    /// backup that was taken while mods were applied.
    /// </summary>
    private Dictionary<ulong, ulong> WithoutModRedirects(Dictionary<ulong, ulong> redirects)
    {
        var result = new Dictionary<ulong, ulong>(redirects.Count);
        var removed = 0;
        foreach (var (original, replacement) in redirects)
        {
            if (original >> 44 == replacement >> 44)
            {
                result.Add(original, replacement);
            }
            else
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Left out {Count} redirect(s) that mods added", removed);
        }

        return result;
    }

    private static bool IsModReference(string uri) =>
        uri.StartsWith(ModReferencePrefix, StringComparison.OrdinalIgnoreCase)
        && uri.EndsWith(ModReferenceSuffix, StringComparison.OrdinalIgnoreCase);

    private static HashSet<Guid> GetAppliedModIds(EbonyArchive archive) => archive.Files.Values
        .Select(f => f.Uri)
        .Where(IsModReference)
        .Select(u => Guid.TryParse(u[ModReferencePrefix.Length..^ModReferenceSuffix.Length], out var id)
            ? id
            : Guid.Empty)
        .ToHashSet();
}

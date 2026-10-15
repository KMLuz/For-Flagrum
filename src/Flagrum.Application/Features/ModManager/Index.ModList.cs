using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flagrum.Abstractions;
using Flagrum.Abstractions.ModManager.Project;
using Flagrum.Application.Features.ModManager.Data;
using Flagrum.Application.Features.ModManager.FskImport;
using Flagrum.Application.Features.ModManager.Instructions;
using Flagrum.Application.Features.ModManager.Instructions.Abstractions;
using Flagrum.Application.Features.ModManager.Services;
using Flagrum.Core.Utilities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Flagrum.Application.Features.ModManager;

/// <summary>
/// One file that a mod changes, as shown in the mod details panel.
/// </summary>
/// <param name="Action">What the mod does with the file, e.g. "Replace".</param>
/// <param name="GameFile">The game file (URI or path) being changed.</param>
/// <param name="SourceFile">The file on disk that the mod uses, if any.</param>
public record ModFileEntry(string Action, string GameFile, string SourceFile);

public enum ModStatusFilter
{
    All,
    Enabled,
    Disabled
}

public enum ModSortColumn
{
    Status,
    Name,
    Category,
    Files,
    Installed
}

/// <summary>
/// The mod list: filtering, sorting, selection, and enabling, disabling and deleting mods.
/// </summary>
public sealed partial class Index
{
    private const string ListColumns = "116px 52px minmax(0, 1fr) 110px 60px 110px 40px";

    private readonly Dictionary<Guid, long> _imageVersions = new();
    private readonly long _defaultImageVersion = DateTime.UtcNow.Ticks;

    [Inject] private ModMetadataService Metadata { get; set; }
    [Inject] private IAuthenticationService Authentication { get; set; }
    [Inject] private NavigationManager Navigation { get; set; }
    [Inject] private FskImportService FskImport { get; set; }

    private FskImportModal FskImportModal { get; set; }
    private HashSet<Guid> _changedBuilds = [];

    private string SearchText { get; set; } = "";
    private ModStatusFilter StatusFilter { get; set; }
    private ModListCategory? CategoryFilter { get; set; }
    private ModSortColumn SortColumn { get; set; } = ModSortColumn.Name;
    private bool SortDescending { get; set; }
    private Guid? SelectedModId { get; set; }

    /// <summary>
    /// The mod currently being enabled or disabled, if any. Only one mod can change at a time,
    /// as every change rewrites the same game file.
    /// </summary>
    public Guid? BusyModId { get; private set; }

    public ModMetadataService ModMetadata => Metadata;
    public FskImportService FskBuilds => FskImport;

    private IFlagrumProject SelectedMod =>
        SelectedModId is { } id && ModManager.Projects.TryGetValue(id, out var mod) ? mod : null;

    public bool IsEnabled(IFlagrumProject mod) => ModManager.ModsState.GetActive(mod.Identifier);

    public string GetImageUrl(Guid modId) =>
        $"Images/{Profile.Current.Id}/{modId}.jpg?{_imageVersions.GetValueOrDefault(modId, _defaultImageVersion)}";

    /// <summary>
    /// Lists the files a mod changes, in the order they appear in its build list.
    /// </summary>
    public static List<ModFileEntry> GetModFiles(IFlagrumProject mod)
    {
        var packed = mod.Archives
            .SelectMany(archive => archive.Instructions)
            .Select(instruction => instruction switch
            {
                ReplacePackedFileBuildInstruction replace => new ModFileEntry("Replace", replace.Uri, replace.FilePath),
                AddToPackedTextureArrayBuildInstruction array => new ModFileEntry("Add to array", array.Uri,
                    array.FilePath),
                AddPackedFileBuildInstruction add => new ModFileEntry("Add", add.Uri, add.FilePath),
                RemovePackedFileBuildInstruction remove => new ModFileEntry("Remove", remove.Uri, null),
                AddReferenceBuildInstruction reference => new ModFileEntry("Reference", reference.Uri, null),
                PackedAssetBuildInstruction asset => new ModFileEntry("Change", asset.Uri, asset.FilePath),
                _ => new ModFileEntry("Change", instruction.Uri, null)
            });

        var loose = mod.Instructions
            .Select(instruction => instruction switch
            {
                LooseAssetBuildInstruction file => new ModFileEntry("Loose file", file.RelativePath, file.FilePath),
                _ => new ModFileEntry("Setting", instruction.FilterableName, null)
            });

        return packed.Concat(loose).ToList();
    }

    private List<IFlagrumProject> GetVisibleMods()
    {
        IEnumerable<IFlagrumProject> mods = ModManager.Projects.Values;

        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            mods = mods.Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                   || (m.Author ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        mods = StatusFilter switch
        {
            ModStatusFilter.Enabled => mods.Where(IsEnabled),
            ModStatusFilter.Disabled => mods.Where(m => !IsEnabled(m)),
            _ => mods
        };

        if (CategoryFilter is { } category)
        {
            mods = mods.Where(m => Metadata.GetCategory(m.Identifier) == category);
        }

        // Pinned mods always stay at the top
        var pinnedFirst = mods.OrderByDescending(m => ModManager.ModsState.GetPinned(m.Identifier));
        var sorted = SortColumn switch
        {
            ModSortColumn.Status => ThenBy(pinnedFirst, m => !IsEnabled(m)),
            ModSortColumn.Category => ThenBy(pinnedFirst, m => Metadata.GetCategory(m.Identifier)),
            ModSortColumn.Files => ThenBy(pinnedFirst, m => GetModFiles(m).Count),
            ModSortColumn.Installed => ThenBy(pinnedFirst, m => Metadata.GetInstalledAt(m.Identifier)),
            _ => ThenBy(pinnedFirst, m => m.Name, StringComparer.OrdinalIgnoreCase)
        };

        return sorted.ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IOrderedEnumerable<IFlagrumProject> ThenBy<TKey>(
        IOrderedEnumerable<IFlagrumProject> source,
        Func<IFlagrumProject, TKey> key,
        IComparer<TKey> comparer = null) =>
        SortDescending ? source.ThenByDescending(key, comparer) : source.ThenBy(key, comparer);

    private void SortBy(ModSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;

            // Newest first is the more useful default for dates
            SortDescending = column == ModSortColumn.Installed;
        }
    }

    private void ChangeCategoryFilter(ChangeEventArgs e)
    {
        CategoryFilter = Enum.TryParse<ModListCategory>(e.Value?.ToString(), out var category) ? category : null;
    }

    private void RowMouseUp(MouseEventArgs e, IFlagrumProject mod)
    {
        // Right-click opens the mod's context menu
        if (e.Button == 2)
        {
            SetContextMod(mod);
        }
    }

    private void SelectMod(IFlagrumProject mod)
    {
        SelectedModId = SelectedModId == mod.Identifier ? null : mod.Identifier;
    }

    public void CloseDetailsPanel()
    {
        SelectedModId = null;
        StateHasChanged();
    }

    public void ToggleMod(IFlagrumProject mod)
    {
        if (IsEnabled(mod))
        {
            DisableMod(mod);
        }
        else
        {
            EnableMod(mod);
        }
    }

    public void EnableMod(IFlagrumProject mod) => RunModOperation(mod, () => EnableModAsync(mod));

    public void DisableMod(IFlagrumProject mod) => RunModOperation(mod, () => ModManagerService.DisableMod(mod));

    private void RunModOperation(IFlagrumProject mod, Func<Task> operation)
    {
        if (BusyModId != null)
        {
            return;
        }

        if (Profile.IsGameRunning())
        {
            Alert.Open("Error", "The Game is Running",
                "Flagrum cannot enable or disable mods while the game is running. " +
                "Please save and close down the game, then try again.", null);
            return;
        }

        BusyModId = mod.Identifier;
        StateHasChanged();

        ThreadHelper.RunOnNewThread(async () =>
        {
            try
            {
                await Task.Run(operation);
            }
            catch (Exception exception)
            {
                ShowModOperationError(exception);
            }

            await InvokeAsync(() =>
            {
                BusyModId = null;
                StateHasChanged();
            });
        });
    }

    private async Task EnableModAsync(IFlagrumProject mod)
    {
        var deadFiles = ModManagerService.Projects[mod.Identifier].GetDeadFiles();
        if (deadFiles.Any())
        {
            var message = deadFiles.Aggregate(
                "The mod could not be built as the following files were missing:<ul class='mt-4'>",
                (current, file) => current + $"<li>{file}</li>");
            await InvokeAsync(() => Alert.Open("Error", "Missing Files", message + "</ul>", null, 400, 300, true));
            return;
        }

        if (EarlyAccessMods.GuidMap.ContainsKey(mod.Identifier) && !Authentication.IsAuthenticated)
        {
            await InvokeAsync(() => Alert.Open("Error", "Flagrum+ Required",
                "This mod requires Flagrum+ to run.", null));
            return;
        }

        if (!CheckReferencesValid(mod))
        {
            return;
        }

        // Asks the user to turn off any enabled mods that change the same files
        var taskId = await CheckConflicts(mod, async () => await ModManagerService.EnableMod(mod));
        await TaskCompletionSources[taskId].Task;
        TaskCompletionSources.Remove(taskId);
    }

    public void DeleteMod(IFlagrumProject mod)
    {
        if (IsEnabled(mod) && Profile.IsGameRunning())
        {
            Alert.Open("Error", "The Game is Running",
                "Flagrum cannot delete enabled mods while the game is running. " +
                "Please save and close down the game, then try again.", null);
            return;
        }

        Prompt.Title = "Delete Mod";
        Prompt.Heading = "Are you sure?";
        Prompt.Subtext = $"<strong>{System.Net.WebUtility.HtmlEncode(mod.Name)}</strong> will be deleted. " +
                         "This action cannot be undone!";
        Prompt.YesText = "Delete";
        Prompt.NoText = "Cancel";
        Prompt.OnNo = null;
        Prompt.OnYes = async () => await InvokeAsync(async () =>
        {
            LoadingText = "Deleting Mod";
            IsLoading = true;
            StateHasChanged();

            try
            {
                await Task.Run(async () => await ModManagerService.DeleteMod(mod));
                Metadata.Remove(mod.Identifier);
                FskImport.Unlink(mod.Identifier);
                if (SelectedModId == mod.Identifier)
                {
                    SelectedModId = null;
                }
            }
            catch (Exception exception)
            {
                ShowModOperationError(exception);
            }

            IsLoading = false;
            StateHasChanged();
        });

        Prompt.Open();
    }

    /// <summary>
    /// Whether the FSK build this mod was made from has been re-exported since it was last synced.
    /// </summary>
    public bool HasBuildChanged(Guid modId) => _changedBuilds.Contains(modId);

    private void RefreshBuildStatuses()
    {
        _changedBuilds = ModManager.Projects.Keys
            .Where(FskImport.IsFskMod)
            .Where(FskImport.HasBuildChanged)
            .ToHashSet();
    }

    public void OnFskBuildsImported()
    {
        RefreshBuildStatuses();
        StateHasChanged();
    }

    private Task OpenFskImport() => FskImportModal.Open();

    /// <summary>
    /// Updates a mod from its FSK build folder, re-applying it straight away if it's enabled.
    /// </summary>
    public void ResyncMod(IFlagrumProject mod)
    {
        if (BusyModId != null)
        {
            return;
        }

        var build = FskImport.TryLoadBuild(mod.Identifier);
        if (build == null)
        {
            Alert.Open("Error", "Build Not Found",
                "The build folder for this mod can't be found:<br/><br/>" +
                System.Net.WebUtility.HtmlEncode(FskImport.GetBuildFolder(mod.Identifier) ?? "(unknown)"),
                null, 500, 300);
            return;
        }

        if (build.Problems.Count > 0)
        {
            Alert.Open("Error", "Build Has Problems",
                string.Join("<br/>", build.Problems.Select(System.Net.WebUtility.HtmlEncode)), null, 600, 400, true);
            return;
        }

        if (IsEnabled(mod) && Profile.IsGameRunning())
        {
            Alert.Open("Error", "The Game is Running",
                "This mod is enabled, so Flagrum needs to re-apply it. " +
                "Please save and close down the game, then try again.", null);
            return;
        }

        BusyModId = mod.Identifier;
        StateHasChanged();

        ThreadHelper.RunOnNewThread(async () =>
        {
            try
            {
                await Task.Run(() => FskImport.ImportAsync(build));
            }
            catch (InvalidOperationException exception)
            {
                await InvokeAsync(() => Alert.Open("Error", "Couldn't Re-sync",
                    System.Net.WebUtility.HtmlEncode(exception.Message).Replace("\n", "<br/>"), null, 600, 400, true));
            }
            catch (Exception exception)
            {
                ShowModOperationError(exception);
            }

            await InvokeAsync(() =>
            {
                BusyModId = null;
                RefreshBuildStatuses();
                StateHasChanged();
            });
        });
    }

    /// <summary>
    /// Imports a zipped FSK build chosen through Install Mod.
    /// </summary>
    private async Task InstallFskZip(string path)
    {
        await InvokeAsync(() => SetLoading(true, "Importing Build"));

        try
        {
            var mod = await Task.Run(() => FskImport.ImportZipAsync(path));
            await InvokeAsync(() => Alert.Open("Success", "Build Imported",
                $"Imported <strong>{System.Net.WebUtility.HtmlEncode(mod.Name)}</strong>. " +
                "Use its Enabled/Disabled button to turn it on.", null, 450, 260));
        }
        catch (InvalidOperationException exception)
        {
            await InvokeAsync(() => Alert.Open("Error", "Couldn't Import Build",
                System.Net.WebUtility.HtmlEncode(exception.Message).Replace("\n", "<br/>"), null, 600, 400, true));
        }
        catch (Exception exception)
        {
            ShowModOperationError(exception);
        }

        await InvokeAsync(() =>
        {
            SetLoading(false);
            RefreshBuildStatuses();
        });
    }

    public void EditMod(IFlagrumProject mod)
    {
        Navigation.NavigateTo($"/earc/editor/{mod.Identifier}");
    }

    /// <summary>
    /// Opens the dialog for the mod's name, author, description and picture.
    /// </summary>
    public Task EditModInfo(IFlagrumProject mod) => ModCardModal.Open(mod.Identifier);

    /// <summary>
    /// Opens the folder containing the mod's files in Explorer, e.g. the add-on build folder.
    /// </summary>
    public void OpenModFolder(IFlagrumProject mod)
    {
        var directories = GetModFiles(mod)
            .Select(file => file.SourceFile)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !path.EndsWith(".ffg"))
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrEmpty(directory))
            .ToList();

        var folder = GetCommonDirectory(directories);
        if (folder == null || !Directory.Exists(folder))
        {
            folder = Path.Combine(Profile.ModFilesDirectory, mod.Identifier.ToString());
        }

        if (Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) {UseShellExecute = true})?.Dispose();
        }
        else
        {
            Alert.Open("Warning", "Folder Not Found", "None of this mod's files could be found on disk.", null);
        }
    }

    private static string GetCommonDirectory(List<string> directories)
    {
        if (directories.Count == 0)
        {
            return null;
        }

        var common = directories[0].TrimEnd('\\', '/').Split('\\', '/');
        var length = common.Length;
        foreach (var segments in directories.Skip(1).Select(d => d.TrimEnd('\\', '/').Split('\\', '/')))
        {
            length = Math.Min(length, segments.Length);
            for (var i = 0; i < length; i++)
            {
                if (!string.Equals(common[i], segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    length = i;
                    break;
                }
            }
        }

        return length == 0 ? null : string.Join(Path.DirectorySeparatorChar, common.Take(length));
    }
}

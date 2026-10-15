using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flagrum.Abstractions;
using Injectio.Attributes;
using Microsoft.Extensions.Logging;

namespace Flagrum.Application.Features.ModManager.Services;

/// <summary>
/// Categories that can be assigned to a mod in the mod list.
/// </summary>
public enum ModListCategory
{
    Outfit,
    Hair,
    Body,
    Textures,
    Other
}

/// <summary>
/// Stores extra information shown in the mod list, such as each mod's category and install date.
/// </summary>
/// <remarks>
/// This is kept in its own file next to the mod state rather than in the mod projects,
/// so the project format stays compatible with upstream Flagrum.
/// </remarks>
[RegisterSingleton]
public class ModMetadataService(IProfileService profile, ILogger<ModMetadataService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = {new JsonStringEnumConverter()}
    };

    private readonly object _lock = new();
    private Dictionary<Guid, ModMetadata> _metadata;

    private string FilePath => Path.Combine(profile.FlagrumDirectory, "Profiles", profile.Current.Id.ToString(),
        "mod_metadata.json");

    public ModListCategory GetCategory(Guid modId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(modId, out var metadata) ? metadata.Category : ModListCategory.Other;
        }
    }

    public void SetCategory(Guid modId, ModListCategory category)
    {
        lock (_lock)
        {
            GetOrAdd(modId).Category = category;
            Save();
        }
    }

    public DateTime GetInstalledAt(Guid modId)
    {
        lock (_lock)
        {
            var metadata = GetOrAdd(modId);
            if (metadata.InstalledAt == null)
            {
                metadata.InstalledAt = EstimateInstalledAt(modId);
                Save();
            }

            return metadata.InstalledAt.Value;
        }
    }

    /// <summary>
    /// Records an install date for any of the given mods that don't have one yet.
    /// </summary>
    /// <remarks>
    /// Mods added before this file existed get the date their project was created, which is when they were
    /// installed or created in Flagrum.
    /// </remarks>
    public void EnsureInstalledDates(IEnumerable<Guid> modIds)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var modId in modIds)
            {
                var metadata = GetOrAdd(modId);
                if (metadata.InstalledAt == null)
                {
                    metadata.InstalledAt = EstimateInstalledAt(modId);
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
            }
        }
    }

    public void Remove(Guid modId)
    {
        lock (_lock)
        {
            if (Load().Remove(modId))
            {
                Save();
            }
        }
    }

    private DateTime EstimateInstalledAt(Guid modId)
    {
        var projectPath = Path.Combine(profile.ModFilesDirectory, modId.ToString(), "project.fproj");
        return File.Exists(projectPath) ? File.GetCreationTime(projectPath) : DateTime.Now;
    }

    private ModMetadata GetOrAdd(Guid modId)
    {
        var metadata = Load();
        if (!metadata.TryGetValue(modId, out var result))
        {
            result = new ModMetadata();
            metadata[modId] = result;
        }

        return result;
    }

    private Dictionary<Guid, ModMetadata> Load()
    {
        if (_metadata != null)
        {
            return _metadata;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                _metadata = JsonSerializer.Deserialize<Dictionary<Guid, ModMetadata>>(
                    File.ReadAllText(FilePath), JsonOptions);
            }
        }
        catch (Exception exception)
        {
            // Losing categories isn't worth stopping the mod list from loading over
            logger.LogError(exception, "Failed to read mod metadata from {Path}", FilePath);
        }

        return _metadata ??= [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_metadata, JsonOptions));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save mod metadata to {Path}", FilePath);
        }
    }

    private class ModMetadata
    {
        public ModListCategory Category { get; set; } = ModListCategory.Other;
        public DateTime? InstalledAt { get; set; }
    }
}

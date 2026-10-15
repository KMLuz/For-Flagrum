using System;
using System.IO;
using System.Linq;

namespace Flagrum.Application.Features.ModManager.Services;

/// <summary>
/// Checks that game files can be safely written before the mod manager touches them.
/// </summary>
public static class GameFileGuard
{
    /// <summary>
    /// Ensures no other process (typically the game) currently has any of the given files open.
    /// </summary>
    /// <param name="paths">Absolute paths of the files that are about to be written or deleted.</param>
    /// <exception cref="GameFilesInUseException">Thrown if any of the files are in use.</exception>
    /// <remarks>
    /// Files that do not exist are skipped. This should be called before making any changes,
    /// so that nothing is left partially modified if a file turns out to be locked.
    /// </remarks>
    public static void EnsureNotInUse(params string[] paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new GameFilesInUseException(path, exception);
            }
        }
    }
}

/// <summary>
/// Thrown when a game file cannot be modified because another process is using it.
/// </summary>
public class GameFilesInUseException(string filePath, Exception innerException)
    : IOException($"'{filePath}' is being used by another process.", innerException)
{
    /// <summary>
    /// Absolute path of the file that is in use.
    /// </summary>
    public string FilePath { get; } = filePath;
}

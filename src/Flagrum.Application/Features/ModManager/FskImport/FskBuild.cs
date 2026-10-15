using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Flagrum.Application.Features.ModManager.FskImport;

/// <summary>
/// One "Replace" entry from a build's <c>entries.txt</c>.
/// </summary>
/// <param name="Uri">The game file being replaced.</param>
/// <param name="FilePath">Where the replacement file actually is on disk.</param>
public record FskEntry(string Uri, string FilePath);

public enum FskBuildResult
{
    Unknown,
    Pass,
    Fail
}

/// <summary>
/// A build folder written by the FSK Blender add-on: <c>entries.txt</c>, <c>build_log.txt</c> and the built files.
/// </summary>
public partial class FskBuild
{
    public const string EntriesFileName = "entries.txt";
    public const string LogFileName = "build_log.txt";

    private FskBuild(string folderPath)
    {
        FolderPath = folderPath;
        Name = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// Name of the build, which is the name of its folder (e.g. <c>Jacket_NoPants</c>).
    /// </summary>
    public string Name { get; }

    public string FolderPath { get; }

    /// <summary>
    /// The outfit the build replaces (e.g. <c>hu000_100</c>), if the add-on wrote it in the header.
    /// </summary>
    public string Outfit { get; private set; }

    public List<FskEntry> Entries { get; } = [];

    /// <summary>
    /// Anything that stops the build from being imported, such as missing files.
    /// </summary>
    public List<string> Problems { get; } = [];

    public string BuildLog { get; private set; }
    public FskBuildResult Result { get; private set; }

    /// <summary>
    /// Changes whenever <c>entries.txt</c> or any of the files it lists change, so a re-export can be detected.
    /// </summary>
    public string Fingerprint { get; private set; }

    public static bool IsBuildFolder(string folderPath) =>
        File.Exists(Path.Combine(folderPath, EntriesFileName));

    public static FskBuild Load(string folderPath)
    {
        var build = new FskBuild(folderPath);
        var entriesPath = Path.Combine(folderPath, EntriesFileName);
        var entriesText = File.ReadAllText(entriesPath);
        build.ParseEntries(entriesText);

        var logPath = Path.Combine(folderPath, LogFileName);
        if (File.Exists(logPath))
        {
            build.BuildLog = File.ReadAllText(logPath);
            // Use the last result in case the log contains more than one
            var match = ResultRegex().Matches(build.BuildLog).LastOrDefault() ?? Match.Empty;
            build.Result = !match.Success ? FskBuildResult.Unknown
                : match.Groups[1].Value.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? FskBuildResult.Pass
                : FskBuildResult.Fail;
        }

        if (build.Entries.Count == 0 && build.Problems.Count == 0)
        {
            build.Problems.Add("entries.txt doesn't list any files");
        }

        build.Fingerprint = ComputeFingerprint(entriesText, build.Entries);
        return build;
    }

    private void ParseEntries(string text)
    {
        string pendingUri = null;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();

            if (line.StartsWith('#'))
            {
                // Header written by the add-on, e.g. "# Flagrum Replace entries for X (outfit hu000_100) - ..."
                var outfit = OutfitRegex().Match(line);
                if (outfit.Success)
                {
                    Outfit ??= outfit.Groups[1].Value.Trim();
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var key = separator > 0 ? line[..separator].Trim() : "";
            var value = separator > 0 ? line[(separator + 1)..].Trim() : "";

            if (key.Equals("Replace", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingUri != null)
                {
                    Problems.Add($"Line {lineNumber}: \"{pendingUri}\" has no \"With:\" line");
                }

                pendingUri = value;
            }
            else if (key.Equals("With", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingUri == null)
                {
                    Problems.Add($"Line {lineNumber}: \"With:\" without a \"Replace:\" line before it");
                    continue;
                }

                var filePath = ResolveFilePath(value);
                if (filePath == null)
                {
                    Problems.Add($"Missing file: {value}");
                }
                else
                {
                    Entries.Add(new FskEntry(pendingUri, filePath));
                }

                pendingUri = null;
            }
            else
            {
                Problems.Add($"Line {lineNumber}: not a Replace or With line: {line}");
            }
        }

        if (pendingUri != null)
        {
            Problems.Add($"\"{pendingUri}\" has no \"With:\" line");
        }
    }

    /// <summary>
    /// Finds a listed file, also when the build folder has been moved or unzipped somewhere else
    /// since the add-on wrote the absolute paths.
    /// </summary>
    private string ResolveFilePath(string listedPath)
    {
        if (File.Exists(listedPath))
        {
            return listedPath;
        }

        // Take the part of the path after the build folder's name, e.g. "materials\x.gmtl.gfxbin"
        var segments = listedPath.Split('\\', '/');
        var index = Array.FindLastIndex(segments, s => s.Equals(Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index < segments.Length - 1)
        {
            var candidate = Path.Combine([FolderPath, ..segments[(index + 1)..]]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var byName = Path.Combine(FolderPath, segments[^1]);
        return File.Exists(byName) ? byName : null;
    }

    private static string ComputeFingerprint(string entriesText, IEnumerable<FskEntry> entries)
    {
        var builder = new StringBuilder(entriesText);
        foreach (var entry in entries)
        {
            var info = new FileInfo(entry.FilePath);
            builder.Append('\n').Append(entry.FilePath).Append('|').Append(info.Length)
                .Append('|').Append(info.LastWriteTimeUtc.Ticks);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    [GeneratedRegex(@"RESULT:\s*(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResultRegex();

    [GeneratedRegex(@"\(outfit\s+([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex OutfitRegex();
}

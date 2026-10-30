using System.Text.RegularExpressions;
using ASQATools.Shared.Models;

namespace ASQATools.Services;

public class CodeReflectorService
{
    public event Action<string>? OnLog;
    private void Log(string msg) => OnLog?.Invoke(msg);

    public CodeReflectorParseResult ParseAiResponse(string baseDir, string rawAiText)
    {
        var result = new CodeReflectorParseResult();
        if (string.IsNullOrWhiteSpace(rawAiText)) return result;
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Directory.GetCurrentDirectory();

        string canonicalBaseDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var handledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Mode A: Standard Git Unified Diff (--- a/path and +++ b/path with @@ hunks)
        var gitDiffRegex = new Regex(
            @"(?:diff --git\s+a\/[^\r\n]+\s+b\/([^\r\n]+)|---\s+[a-z]?\/?([^\r\n\t]+)\r?\n\+\+\+\s+[b-z]?\/?([^\r\n\t]+))\r?\n(@@\s+-\d+.*?(?=(?:diff --git|---\s+[a-z]?\/|\Z)))",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var gitMatches = gitDiffRegex.Matches(rawAiText);
        foreach (Match m in gitMatches)
        {
            string path = (m.Groups[3].Success && !string.IsNullOrWhiteSpace(m.Groups[3].Value) ? m.Groups[3].Value :
                           m.Groups[2].Success && !string.IsNullOrWhiteSpace(m.Groups[2].Value) ? m.Groups[2].Value :
                           m.Groups[1].Value).Trim().Trim('`', '"', '*');

            if (path.StartsWith("b/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(2);
            if (path.StartsWith("a/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(2);

            string hunkText = m.Groups[4].Value;
            string cleanRelPath = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(canonicalBaseDir, cleanRelPath));
            if (!fullPath.StartsWith(canonicalBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

            result.TotalHunks++;
            result.Files.Add(new CodeReflectorFileEntry
            {
                RelativePath = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/'),
                FullPath = fullPath,
                ChangeType = "Git Unified Diff",
                Content = hunkText,
                FileExistsOnDisk = File.Exists(fullPath)
            });
            handledPaths.Add(fullPath);
        }

        // Mode B: Standard File Header Pattern (FILE: path or ### `path`)
        var fileBlockRegex = new Regex(
            @"(?:={3,}\s*(?:FILE:?|\[FILE\])\s*([^\r\n=]+)\s*={3,}|(?:###|\*\*)\s*(?:File:?|FILE:?)\s*[`""]?([^\r\n`""]+)[`""]?|(?:```[a-zA-Z0-9_\-\.]*\s+([a-zA-Z0-9_\-/\\]+\.[a-zA-Z0-9_]+)))\r?\n(.*?)(?=(?:={3,}\s*(?:FILE:?|\[FILE\])|(?:###|\*\*)\s*(?:File:?|FILE:?)|```[a-zA-Z0-9_\-\.]*\s+[a-zA-Z0-9_\-/\\]+\.[a-zA-Z0-9_]+|\Z))",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var matches = fileBlockRegex.Matches(rawAiText);
        foreach (Match m in matches)
        {
            string path = (m.Groups[1].Success ? m.Groups[1].Value :
                           m.Groups[2].Success ? m.Groups[2].Value :
                           m.Groups[3].Value).Trim().Trim('`', '"', '*');

            if (path.StartsWith("b/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(2);
            if (path.StartsWith("a/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(2);

            string content = m.Groups[4].Value;
            content = Regex.Replace(content, @"^\s*```[a-zA-Z0-9_\-]*\r?\n", "", RegexOptions.Multiline);
            content = Regex.Replace(content, @"\r?\n```\s*$", "", RegexOptions.Multiline).TrimEnd();
            content = Regex.Replace(content, @"^\s*\d+\s*\|\s?", "", RegexOptions.Multiline);

            string cleanRelPath = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(canonicalBaseDir, cleanRelPath));
            if (!fullPath.StartsWith(canonicalBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!handledPaths.Contains(fullPath))
            {
                bool exists = File.Exists(fullPath);
                result.Files.Add(new CodeReflectorFileEntry
                {
                    RelativePath = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/'),
                    FullPath = fullPath,
                    ChangeType = exists ? "Full Update" : "New File",
                    Content = content,
                    FileExistsOnDisk = exists
                });
                handledPaths.Add(fullPath);
            }
        }

        // Mode C: Search & Replace Blocks (Aider format)
        var srRegex = new Regex(@"(?:File:\s*([^\r\n]+)\r?\n)?<<<<<<< SEARCH\r?\n(.*?)\r?\n=======\r?\n(.*?)\r?\n>>>>>>> REPLACE", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match m in srRegex.Matches(rawAiText))
        {
            string pathHint = m.Groups[1].Value.Trim().Trim('`', '"');
            if (!string.IsNullOrEmpty(pathHint))
            {
                if (pathHint.StartsWith("b/", StringComparison.OrdinalIgnoreCase)) pathHint = pathHint.Substring(2);
                if (pathHint.StartsWith("a/", StringComparison.OrdinalIgnoreCase)) pathHint = pathHint.Substring(2);

                string cleanRelPath = pathHint.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                string fullPath = Path.GetFullPath(Path.Combine(canonicalBaseDir, cleanRelPath));
                if (!fullPath.StartsWith(canonicalBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!handledPaths.Contains(fullPath))
                {
                    result.TotalHunks++;
                    result.Files.Add(new CodeReflectorFileEntry
                    {
                        RelativePath = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/'),
                        FullPath = fullPath,
                        ChangeType = "Search/Replace Patch",
                        Content = m.Value,
                        FileExistsOnDisk = File.Exists(fullPath)
                    });
                    handledPaths.Add(fullPath);
                }
            }
        }

        return result;
    }

    public async Task<int> ApplyChangesAsync(string baseDir, string rawAiText, bool createBackups)
    {
        Log("=== Initiating AI Code Reflection Engine ===");
        var parsed = ParseAiResponse(baseDir, rawAiText);
        if (parsed.Files.Count == 0 && parsed.TotalHunks == 0)
        {
            throw new InvalidOperationException("No identifiable Git diffs, search/replace blocks, or full file updates found in the text.");
        }

        int appliedCount = 0;

        foreach (var file in parsed.Files)
        {
            string fullPath = file.FullPath;
            string canonicalBaseDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(canonicalBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Path traversal attempt blocked: {file.RelativePath}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            if (file.FileExistsOnDisk && createBackups)
            {
                string backupPath = fullPath + ".bak";
                File.Copy(fullPath, backupPath, overwrite: true);
                Log($"  🔒 Backup created: {Path.GetFileName(backupPath)}");
            }

            if (file.ChangeType == "Git Unified Diff")
            {
                if (!File.Exists(fullPath))
                {
                    Log($"  ⚠️ Cannot apply git diff: File not found {file.RelativePath}");
                    continue;
                }

                string existingContent = await File.ReadAllTextAsync(fullPath);
                string patchedContent = ApplyGitHunks(existingContent, file.Content, out int appliedHunks, out int failedHunks);

                // ATOMIC SAFETY GUARD: Do NOT write the file if any hunk failed!
                if (failedHunks > 0)
                {
                    Log($"  ❌ [ABORTED] {file.RelativePath}: {failedHunks} hunk(s) failed. File left untouched to prevent syntax corruption.");
                    continue;
                }

                await File.WriteAllTextAsync(fullPath, patchedContent);
                Log($"  ✔ Git diff applied to {file.RelativePath}: {appliedHunks} hunk(s) succeeded, 0 failed.");
                appliedCount++;
            }
            else if (file.ChangeType == "Search/Replace Patch")
            {
                if (!File.Exists(fullPath))
                {
                    Log($"  ⚠️ Cannot apply patch: File not found {file.RelativePath}");
                    continue;
                }

                string existingContent = await File.ReadAllTextAsync(fullPath);
                var srRegex = new Regex(@"<<<<<<< SEARCH\r?\n(.*?)\r?\n=======\r?\n(.*?)\r?\n>>>>>>> REPLACE", RegexOptions.Singleline);
                bool anyFailed = false;

                foreach (Match m in srRegex.Matches(file.Content))
                {
                    string search = m.Groups[1].Value;
                    string replace = m.Groups[2].Value;
                    if (existingContent.Contains(search))
                    {
                        existingContent = existingContent.Replace(search, replace);
                    }
                    else
                    {
                        anyFailed = true;
                        Log($"  ❌ Search block not matched in {file.RelativePath}");
                    }
                }

                if (anyFailed)
                {
                    Log($"  ❌ [ABORTED] {file.RelativePath}: search/replace mismatch. File left untouched.");
                    continue;
                }

                await File.WriteAllTextAsync(fullPath, existingContent);
                Log($"  ✔ Patch applied to {file.RelativePath}");
                appliedCount++;
            }
            else
            {
                await File.WriteAllTextAsync(fullPath, file.Content);
                Log($"  ✔ Written [{file.ChangeType}]: {file.RelativePath}");
                appliedCount++;
            }
        }

        Log($"✔ Successfully applied changes to {appliedCount} file(s) on disk.");
        return appliedCount;
    }

    public static string ApplyGitHunks(string originalText, string hunksText, out int succeeded, out int failed)
    {
        succeeded = 0;
        failed = 0;
        var lines = originalText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        var hunkPattern = new Regex(@"(@@\s+-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@[^\r\n]*\r?\n)(.*?)(?=(?:@@\s+-\d+|\Z))", RegexOptions.Singleline);

        var matches = hunkPattern.Matches(hunksText);
        var hunksToApply = new List<(int OldStart, List<string> SearchLines, List<string> ReplaceLines)>();

        foreach (Match m in matches)
        {
            int oldStart = int.TryParse(m.Groups[2].Value, out int sLine) ? sLine : 1;
            string body = m.Groups[4].Value.TrimEnd('\r', '\n');
            var hunkLines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var searchLines = new List<string>();
            var replaceLines = new List<string>();

            foreach (var h in hunkLines)
            {
                char prefix = h.Length > 0 ? h[0] : ' ';
                string content = h.Length > 1 ? h.Substring(1) : "";

                if (prefix == ' ')
                {
                    searchLines.Add(content);
                    replaceLines.Add(content);
                }
                else if (prefix == '-')
                {
                    searchLines.Add(content);
                }
                else if (prefix == '+')
                {
                    replaceLines.Add(content);
                }
                else
                {
                    searchLines.Add(h);
                    replaceLines.Add(h);
                }
            }

            if (searchLines.Count > 0)
            {
                hunksToApply.Add((oldStart, searchLines, replaceLines));
            }
        }

        foreach (var (oldStart, searchLines, replaceLines) in hunksToApply.OrderByDescending(h => h.OldStart))
        {
            int matchIndex = FindMatchingBlockIndex(lines, searchLines, oldStart - 1);

            if (matchIndex >= 0)
            {
                lines.RemoveRange(matchIndex, searchLines.Count);
                lines.InsertRange(matchIndex, replaceLines);
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int FindMatchingBlockIndex(List<string> lines, List<string> searchLines, int preferredIndex)
    {
        if (searchLines.Count == 0 || lines.Count < searchLines.Count) return -1;

        int maxOffset = Math.Max(preferredIndex, lines.Count - preferredIndex);
        for (int offset = 0; offset <= Math.Min(30, maxOffset); offset++)
        {
            int checkDown = preferredIndex + offset;
            if (checkDown >= 0 && checkDown <= lines.Count - searchLines.Count && IsBlockMatch(lines, searchLines, checkDown))
                return checkDown;

            if (offset > 0)
            {
                int checkUp = preferredIndex - offset;
                if (checkUp >= 0 && checkUp <= lines.Count - searchLines.Count && IsBlockMatch(lines, searchLines, checkUp))
                    return checkUp;
            }
        }

        for (int i = 0; i <= lines.Count - searchLines.Count; i++)
        {
            if (IsBlockMatch(lines, searchLines, i)) return i;
        }

        return -1;
    }

    private static bool IsBlockMatch(List<string> lines, List<string> searchLines, int startIndex)
    {
        for (int j = 0; j < searchLines.Count; j++)
        {
            if (!string.Equals(lines[startIndex + j].Trim(), searchLines[j].Trim(), StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}

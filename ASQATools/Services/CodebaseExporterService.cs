using System.Text;
using ASQATools.Shared.Models;

namespace ASQATools.Services;

public class CodebaseExporterService
{
    public event Action<string>? OnLog;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".vscode", "node_modules", "dist", "build", "publish", "pr_reviews_data", "App_Data"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".bin", ".zip", ".png", ".jpg", ".jpeg", ".ico", ".pdf",
        ".woff", ".woff2", ".ttf", ".eot", ".svg", ".map", ".suo", ".user"
    };

    private void Log(string msg) => OnLog?.Invoke(msg);

    public List<TreeNodeItemDto> GetDirectoryChildren(string rootPath, string? relativeSubPath = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) rootPath = Directory.GetCurrentDirectory();
        string targetPath = string.IsNullOrWhiteSpace(relativeSubPath) 
            ? rootPath 
            : Path.GetFullPath(Path.Combine(rootPath, relativeSubPath));

        if (!Directory.Exists(targetPath)) return new List<TreeNodeItemDto>();

        var result = new List<TreeNodeItemDto>();
        var dirInfo = new DirectoryInfo(targetPath);

        // 1. Subdirectories
        foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
        {
            string rel = Path.GetRelativePath(rootPath, dir.FullName).Replace('\\', '/');
            bool isDefaultExcluded = ExcludedDirectories.Contains(dir.Name) ||
                                     dir.Name.StartsWith(".") ||
                                     dir.Name.Equals("publish", StringComparison.OrdinalIgnoreCase);

            bool hasKids = false;
            try { hasKids = dir.EnumerateFileSystemInfos().Any(); } catch { }

            result.Add(new TreeNodeItemDto
            {
                RelativePath = rel,
                Name = dir.Name,
                IsDirectory = true,
                HasChildren = hasKids,
                IsExpanded = false,
                IsSelected = !isDefaultExcluded,
                IsDefaultExcluded = isDefaultExcluded
            });
        }

        // 2. Files
        foreach (var file in dirInfo.GetFiles().OrderBy(f => f.Name))
        {
            string rel = Path.GetRelativePath(rootPath, file.FullName).Replace('\\', '/');
            bool isDefaultExcluded = ExcludedExtensions.Contains(file.Extension) || file.Name.StartsWith("._");

            result.Add(new TreeNodeItemDto
            {
                RelativePath = rel,
                Name = file.Name,
                IsDirectory = false,
                HasChildren = false,
                IsExpanded = false,
                IsSelected = !isDefaultExcluded,
                IsDefaultExcluded = isDefaultExcluded
            });
        }

        return result;
    }

    public Task<CodebaseExportResult> ExportProjectAsync(CodebaseExportRequest req)
    {
        return Task.Run(() =>
        {
            string rootPath = req.RootPath;
            if (string.IsNullOrWhiteSpace(rootPath))
                rootPath = Directory.GetCurrentDirectory();

            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Directory not found: {rootPath}");

            Log($"Scanning directory: {rootPath}");

            var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !IsFileExcluded(f, rootPath, req.ExcludeStandardArtifacts, req.CustomExclusions, req.IncludedPaths, req.ExcludedPaths))
                .OrderBy(f => f)
                .ToList();

            Log($"Discovered {files.Count} packable source code files.");

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine($"GIT-INGEST CODEBASE BUNDLE: {Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar))}");
            sb.AppendLine($"Exported At: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Root Path:   {rootPath}");
            sb.AppendLine("================================================================================");
            sb.AppendLine("AI INSTRUCTION FOR CODE MODIFICATIONS:");
            sb.AppendLine("When proposing changes, modifications, or bug fixes for this codebase, please");
            sb.AppendLine("return your code modifications in standard Git Unified Diff format:");
            sb.AppendLine("--- a/path/to/file.ext");
            sb.AppendLine("+++ b/path/to/file.ext");
            sb.AppendLine("@@ -oldStart,oldCount +newStart,newCount @@");
            sb.AppendLine("- deleted line");
            sb.AppendLine("+ added line");
            sb.AppendLine("  context line");
            sb.AppendLine("This allows the ASQA AI Reflector to automatically apply your diffs directly to files.");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            sb.AppendLine("Directory structure:");
            BuildDirectoryTree(rootPath, files, sb);
            sb.AppendLine();

            int totalFiles = 0;
            long totalLines = 0;

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                Log($"  [{totalFiles + 1}/{files.Count}] 📄 Bundling: {relative}");
                string content;
                try { content = File.ReadAllText(file); } catch { continue; }

                sb.AppendLine("================================================");
                sb.AppendLine($"File: {relative}");
                sb.AppendLine("================================================");

                var lines = content.Split('\n');
                totalLines += lines.Length;
                totalFiles++;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (req.IncludeLineNumbers)
                        sb.AppendLine($"{(i + 1),5} | {lines[i].TrimEnd('\r')}");
                    else
                        sb.AppendLine(lines[i].TrimEnd('\r'));
                }
                sb.AppendLine();
            }

            string bundleText = sb.ToString();
            long totalChars = bundleText.Length;
            long estimatedTokens = totalChars / 4;

            Log($"✔ GitIngest bundle generated: {totalFiles} files bundled, {totalLines:N0} total LOC, ~{estimatedTokens:N0} tokens.");

            return new CodebaseExportResult
            {
                TotalFiles = totalFiles,
                TotalLines = totalLines,
                TotalCharacters = totalChars,
                EstimatedTokens = estimatedTokens,
                BundleText = bundleText
            };
        });
    }

    private static void BuildDirectoryTree(string rootPath, List<string> files, StringBuilder sb)
    {
        var relativePaths = files.Select(f => Path.GetRelativePath(rootPath, f).Replace('\\', '/')).ToList();
        var tree = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in relativePaths)
        {
            string dir = Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "";
            string file = Path.GetFileName(p);
            if (!tree.ContainsKey(dir)) tree[dir] = new List<string>();
            tree[dir].Add(file);
        }

        foreach (var kvp in tree)
        {
            string dirName = string.IsNullOrEmpty(kvp.Key) ? "." : kvp.Key;
            sb.AppendLine($"├── {dirName}/");
            for (int i = 0; i < kvp.Value.Count; i++)
            {
                bool isLast = i == kvp.Value.Count - 1;
                sb.AppendLine($"│   {(isLast ? "└──" : "├──")} {kvp.Value[i]}");
            }
        }
    }

    private static bool IsFileExcluded(
        string filePath, 
        string rootPath, 
        bool excludeStandardArtifacts, 
        string? customExclusions, 
        List<string>? includedPaths, 
        List<string>? excludedPaths)
    {
        string relative = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

        if (excludeStandardArtifacts)
        {
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in parts)
            {
                if (ExcludedDirectories.Contains(part)) return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(customExclusions))
        {
            var customs = customExclusions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var c in customs)
            {
                if (relative.Contains(c.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        string ext = Path.GetExtension(filePath);
        if (ExcludedExtensions.Contains(ext)) return true;
        if (Path.GetFileName(filePath).StartsWith("._")) return true;

        if (excludedPaths != null && excludedPaths.Count > 0)
        {
            foreach (var ex in excludedPaths)
            {
                if (relative.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(ex.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (includedPaths != null && includedPaths.Count > 0)
        {
            bool matches = includedPaths.Any(inc =>
                relative.Equals(inc, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(inc.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            if (!matches) return true;
        }

        return false;
    }
}

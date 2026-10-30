using ASQATools.Shared.Models;
using ASQATools.Shared.Services;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ASQATools.Services;

public class PrReviewEngineService
{
    private readonly TfsApiClient _tfs;
    public event Action<string>? OnLog;

    private static readonly object _cleanupLock = new();
    private static DateOnly _lastCleanupRunDate = DateOnly.MinValue;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".tif", ".tiff",
        ".zip", ".tar", ".gz", ".7z", ".rar", ".bz2",
        ".pdf", ".exe", ".dll", ".so", ".dylib", ".bin",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".mp3", ".mp4", ".mov", ".avi", ".wmv", ".wav",
        ".jar", ".war", ".ear", ".nupkg", ".snupkg"
    };

    private static readonly HashSet<string> ExcludedProjectAndSolutionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".sqlproj", ".dbproj",
        ".vcxproj", ".vcxproj.filters", ".vcxproj.user", ".props", ".targets"
    };

    private static readonly HashSet<string> ExcludedSecurityExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pfx", ".p12", ".pem", ".key", ".cer", ".crt", ".der", ".kdbx"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "web.config", "web.debug.config", "web.release.config",
        "app.config", "app.debug.config", "app.release.config",
        "appsettings.json", "launchsettings.json", "secrets.json",
        "connections.config", "connectionstrings.config", "parameters.xml", ".env"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public PrReviewEngineService(TfsApiClient tfs)
    {
        _tfs = tfs;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void TryRunDailyCleanup(int retentionDays = 3)
    {
        lock (_cleanupLock)
        {
            DateOnly today = DateOnly.FromDateTime(DateTime.Today);
            if (_lastCleanupRunDate == today) return;

            string rootDir = Path.Combine(AppContext.BaseDirectory, "pr_reviews_data");
            if (!Directory.Exists(rootDir))
            {
                _lastCleanupRunDate = today;
                return;
            }

            string markerPath = Path.Combine(rootDir, ".last_cleanup");
            if (File.Exists(markerPath))
            {
                try
                {
                    if (File.GetLastWriteTime(markerPath).Date == DateTime.Today)
                    {
                        _lastCleanupRunDate = today;
                        return;
                    }
                }
                catch { }
            }

            ExecuteCleanup(rootDir, retentionDays);

            try { File.WriteAllText(markerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
            _lastCleanupRunDate = today;
        }
    }

    private void ExecuteCleanup(string rootDir, int retentionDays)
    {
        try
        {
            DateTime cutoffDate = DateTime.Today.AddDays(-retentionDays);
            int deletedFolderCount = 0;

            foreach (var repoDir in Directory.GetDirectories(rootDir))
            {
                foreach (var dateDir in Directory.GetDirectories(repoDir))
                {
                    var dirInfo = new DirectoryInfo(dateDir);
                    bool shouldDelete = DateTime.TryParseExact(dirInfo.Name, "yyyy_MM_dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var folderDate)
                        ? folderDate < cutoffDate
                        : dirInfo.LastWriteTime.Date < cutoffDate;

                    if (shouldDelete)
                    {
                        Directory.Delete(dateDir, recursive: true);
                        deletedFolderCount++;
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(repoDir).Any())
                {
                    Directory.Delete(repoDir, recursive: false);
                }
            }

            if (deletedFolderCount > 0)
            {
                Log($"🧹 Daily cleanup: purged {deletedFolderCount} folder(s) older than {retentionDays} days.");
            }
            else
            {
                Log($"ℹ Daily retention check: verified (0 folders older than {retentionDays} days expired).");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Retention cleanup notice: {ex.Message}");
        }
    }

    private static bool IsExcludedFile(string filePath, out string reason)
    {
        if (string.IsNullOrWhiteSpace(filePath)) { reason = "Empty path"; return true; }
        string fileName = Path.GetFileName(filePath);
        string ext = Path.GetExtension(filePath);

        if (fileName.StartsWith("._", StringComparison.OrdinalIgnoreCase)) { reason = "Temporary / OS metadata"; return true; }
        if (BinaryExtensions.Contains(ext)) { reason = "Binary format"; return true; }
        if (ExcludedProjectAndSolutionExtensions.Contains(ext)) { reason = "Project / Solution file"; return true; }
        if (ExcludedSecurityExtensions.Contains(ext)) { reason = "Certificate / Private Key"; return true; }

        if (ExcludedFileNames.Contains(fileName) ||
            (fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) && ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Sensitive Configuration / Credentials";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool WasWontFixByDeveloper(TfsThread th, TfsPullRequestDetails pr, TfsIdentity? currentReviewer)
    {
        if (th.Status != 3) return false;

        string devId = pr.CreatedBy?.Id ?? "";
        string devName = pr.CreatedBy?.DisplayName ?? "";
        string reviewerId = currentReviewer?.Id ?? "";
        string reviewerName = currentReviewer?.DisplayName ?? "";

        if (th.Comments != null && th.Comments.Count > 0)
        {
            var nonSystemComments = th.Comments.Where(c => c != null && !c.IsDeleted && !IsSystemComment(c)).ToList();
            if (nonSystemComments.Count <= 1 && nonSystemComments.All(c => c.Author?.Id == reviewerId))
                return true;

            foreach (var comment in nonSystemComments.AsEnumerable().Reverse())
            {
                if (comment.Author == null) continue;
                string authorId = comment.Author.Id ?? "";
                string authorName = comment.Author.DisplayName ?? "";

                bool isDev = (!string.IsNullOrEmpty(devId) && authorId == devId) ||
                             (!string.IsNullOrEmpty(devName) && string.Equals(authorName, devName, StringComparison.OrdinalIgnoreCase));
                bool isReviewer = (!string.IsNullOrEmpty(reviewerId) && authorId == reviewerId) ||
                                  (!string.IsNullOrEmpty(reviewerName) && string.Equals(authorName, reviewerName, StringComparison.OrdinalIgnoreCase));

                if (isDev) return true;
                if (isReviewer) return false;
            }
        }
        return true;
    }

    private string LoadPromptTemplate()
    {
        string[] searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Templates", "Prompts", "PrReviewPrompt.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "Templates", "Prompts", "PrReviewPrompt.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "ASQATools", "Templates", "Prompts", "PrReviewPrompt.md")
        };

        foreach (var p in searchPaths)
        {
            if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
        }
        throw new FileNotFoundException("PrReviewPrompt.md template not found.");
    }

    private string LoadJsonSchema()
    {
        string[] searchPaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Templates", "Schemas", "ReviewPayload.schema.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Templates", "Schemas", "ReviewPayload.schema.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "ASQATools", "Templates", "Schemas", "ReviewPayload.schema.json")
        };

        foreach (var p in searchPaths)
        {
            if (File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8);
        }
        return "{}";
    }

    public async Task<PrExportBundleResult> ExportBundleAsync(
        string prInput, 
        NetworkCredential? creds,
        string? patToken, 
        bool diffOnly = false, 
        int contextLines = 6,
        bool forceExport = false)
    {
        TryRunDailyCleanup(retentionDays: 3);

        Log("Connecting to TFS API...");
        using var client = _tfs.CreateClient(creds, patToken);

        var (serverUrl, collection, project, repo, prId) = ParsePrInput(prInput);
        Log($"Fetching PR #{prId} from [{collection}/{project}/{repo}]...");

        var pr = await _tfs.GetPullRequestDetailsAsync(client, serverUrl, collection, project, repo, prId);
        var currentReviewer = await _tfs.GetCurrentAuthenticatedUserAsync(client, serverUrl);
        var iterations = await _tfs.GetPullRequestIterationsAsync(client, serverUrl, collection, project, repo, prId);
        int latestIteration = iterations.Count > 0 ? iterations.Max(i => i.Id) : 1;

        // Clean dynamic branch names
        string targetBranchRaw = pr.TargetRefName ?? "target";
        string sourceBranchRaw = pr.SourceRefName ?? "feature";
        string targetBranchClean = targetBranchRaw.Replace("refs/heads/", "");
        string sourceBranchClean = sourceBranchRaw.Replace("refs/heads/", "");

        var latestIterObj = iterations.FirstOrDefault(i => i.Id == latestIteration);
        string? commonCommit = latestIterObj?.CommonRefCommit?.CommitId;
        string? targetCommit = latestIterObj?.TargetRefCommit?.CommitId ?? pr.LastMergeTargetCommit?.CommitId;

        // 1. Detect Scenario A: Merge Conflicts
        bool hasConflicts = pr.HasConflicts ||
                            string.Equals(pr.MergeStatus, "conflicts", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(pr.MergeStatus, "failed", StringComparison.OrdinalIgnoreCase);

        // 2. Detect Scenario B: Branch Out of Date (Behind Target)
        bool isBehindTarget = !string.IsNullOrEmpty(commonCommit) &&
                              !string.IsNullOrEmpty(targetCommit) &&
                              !string.Equals(commonCommit, targetCommit, StringComparison.OrdinalIgnoreCase);

        // PRE-FLIGHT GUARD: Halt export if issues detected and forceExport is false
        if ((hasConflicts || isBehindTarget) && !forceExport)
        {
            Log($"⚠️ Pre-flight Guard: Export halted. Conflicts: {hasConflicts}, Branch Out of Date: {isBehindTarget}.");

            var alert = new PreflightAlertInfo
            {
                IsBlocked = true,
                HasConflicts = hasConflicts,
                IsBehindTarget = isBehindTarget,
                TargetBranch = targetBranchClean,
                SourceBranch = sourceBranchClean
            };

            if (hasConflicts && isBehindTarget)
            {
                alert.Title = $"🔴 Merge Conflicts & Branch Out of Date with `{targetBranchClean}`";
                alert.Description = $"This pull request has conflicting code changes with `{targetBranchClean}` and is also missing recent updates merged into `{targetBranchClean}`.";
                alert.Commands = new List<string>
                {
                    $"git checkout {sourceBranchClean}",
                    $"git pull origin {targetBranchClean}",
                    $"git commit -m \"Resolve merge conflicts\"",
                    $"git push origin {sourceBranchClean}"
                };
            }
            else if (hasConflicts)
            {
                alert.Title = $"🔴 Merge Conflicts Detected with `{targetBranchClean}`";
                alert.Description = $"This pull request cannot be merged automatically because changes on this branch conflict with changes in `{targetBranchClean}`.";
                alert.Commands = new List<string>
                {
                    $"git checkout {sourceBranchClean}",
                    $"git pull origin {targetBranchClean}",
                    $"git commit -m \"Resolve merge conflicts\"",
                    $"git push origin {sourceBranchClean}"
                };
            }
            else
            {
                alert.Title = $"🟠 Feature Branch is Behind `{targetBranchClean}`";
                alert.Description = $"The target branch (`{targetBranchClean}`) has received new updates since this branch was created. This branch is missing those recent changes, creating regression risks.";
                alert.Commands = new List<string>
                {
                    $"git checkout {sourceBranchClean}",
                    $"git pull origin {targetBranchClean}",
                    $"git push origin {sourceBranchClean}"
                };
            }

            return new PrExportBundleResult
            {
                PrId = prId,
                Title = pr.Title ?? "PR",
                Repository = repo,
                SourceBranch = sourceBranchClean,
                TargetBranch = targetBranchClean,
                PreflightAlert = alert
            };
        }

        // PROCEED WITH EXPORT
        Log($"Analyzing PR iterations (Latest: {latestIteration})...");
        var changes = await _tfs.GetIterationChangesAsync(client, serverUrl, collection, project, repo, prId, latestIteration);

        var summary = new PrChangesSummary();
        var filesToProcess = new List<(TfsChangeEntry Change, string? OldContent, string? NewContent, string? DiffText)>();

        string targetVersion = pr.LastMergeTargetCommit?.CommitId ?? pr.TargetRefName ?? "target";
        string sourceVersion = pr.LastMergeSourceCommit?.CommitId ?? pr.SourceRefName ?? "source";

        foreach (var change in changes)
        {
            if (change?.Item == null || string.IsNullOrWhiteSpace(change.Item.Path) || change.Item.IsFolder) continue;

            string filePath = change.Item.Path;
            string fileName = Path.GetFileName(filePath);

            if (IsExcludedFile(filePath, out string reason))
            {
                if (reason == "Binary format") summary.SkippedBinary++; else summary.SkippedExcluded++;
                summary.SkippedFileNames.Add($"{fileName} ({reason})");
                Log($"  🚫 Excluded {fileName} [{reason}]");
                continue;
            }

            string ctype = change.ChangeType?.ToLowerInvariant() ?? "";
            if (ctype.Contains("delete"))
            {
                summary.Deleted++;
                filesToProcess.Add((change, null, null, null));
                continue;
            }

            if (ctype.Contains("add"))
            {
                summary.Added++;
                try
                {
                    string newContent = await _tfs.GetFileContentAsync(client, serverUrl, collection, project, repo, filePath, sourceVersion);
                    filesToProcess.Add((change, null, newContent, null));
                }
                catch (Exception ex)
                {
                    Log($"  ⚠️ Could not fetch added file {fileName}: {ex.Message}");
                }
                continue;
            }

            try
            {
                string newContent = await _tfs.GetFileContentAsync(client, serverUrl, collection, project, repo, filePath, sourceVersion);
                string oldContent = await _tfs.GetFileContentAsync(client, serverUrl, collection, project, repo, filePath, targetVersion);

                if (FastDiffEngine.HasOnlyWhitespaceOrNewlineChanges(oldContent, newContent))
                {
                    summary.SkippedFormatting++;
                    summary.SkippedFileNames.Add($"{fileName} (Formatting/Whitespace Only)");
                    Log($"  ⚡ Skipped {fileName}: only formatting / whitespace changed.");
                    continue;
                }

                string diff = FastDiffEngine.GenerateUnifiedDiffWithLineNumbers(oldContent, newContent, contextLines: contextLines);
                if (ctype.Contains("rename")) summary.Renamed++; else summary.Edited++;
                filesToProcess.Add((change, oldContent, newContent, diff));
            }
            catch
            {
                try
                {
                    string newContent = await _tfs.GetFileContentAsync(client, serverUrl, collection, project, repo, filePath, sourceVersion);
                    summary.Edited++;
                    filesToProcess.Add((change, null, newContent, null));
                }
                catch (Exception ex)
                {
                    Log($"  ❌ Could not load content for {fileName}: {ex.Message}");
                }
            }
        }

        Log("✔ PULL REQUEST FILE CHANGES SUMMARY:");
        Log($"  - Total Changes:          {summary.TotalChanges}");
        Log($"  - 🆕 New Added Files:     {summary.Added}");
        Log($"  - ✏️ Edited / Modified:   {summary.Edited}");
        Log($"  - 🔄 Renamed Files:       {summary.Renamed}");
        Log($"  - 🗑️ Deleted Files:       {summary.Deleted}");
        Log($"  - ⚡ Skipped Formatting:  {summary.SkippedFormatting}");
        Log($"  - 🛡️ Excluded (Config):   {summary.SkippedExcluded}");
        Log($"  - 🚫 Skipped (Binary):    {summary.SkippedBinary}");

        var threads = await _tfs.GetPullRequestThreadsAsync(client, serverUrl, collection, project, repo, prId);
        var realActiveThreads = threads.Where(t =>
            t != null && !t.IsDeleted && !IsExecutiveSummaryOrFindingsThread(t) &&
            t.Comments != null && t.Comments.Any(c => c != null && !c.IsDeleted && !string.IsNullOrWhiteSpace(c.Content) && !IsSystemComment(c)) &&
            (t.Status == 1 || (t.Status == 3 && WasWontFixByDeveloper(t, pr, currentReviewer)))
        ).ToList();

        Log($"Discovered {threads.Count} total thread(s) ({realActiveThreads.Count} actionable review thread(s)).");
        Log("Assembling AI context bundle...");

        var activeThreadsSb = new StringBuilder();
        if (realActiveThreads.Count == 0)
        {
            activeThreadsSb.AppendLine("No active unresolved threads.");
        }
        else
        {
            foreach (var th in realActiveThreads)
            {
                var userComment = th.Comments?.FirstOrDefault(c => c != null && !c.IsDeleted && !string.IsNullOrWhiteSpace(c.Content) && !IsSystemComment(c))
                                  ?? th.Comments?.FirstOrDefault(c => c != null && !c.IsDeleted);
                string author = userComment?.Author?.DisplayName ?? "Reviewer";
                string content = userComment?.Content ?? "";
                string statusDesc = th.Status == 3 ? "WONT FIX (BY DEVELOPER - REJECTED)" : "ACTIVE";
                activeThreadsSb.AppendLine($"- Thread ID: {th.Id} | Status: {statusDesc} | File: {th.ThreadContext?.FilePath ?? "[General PR Feed]"} | Author: {author}");
                activeThreadsSb.AppendLine($"    * \"{content.Replace("\n", " ").Trim()}\"");
            }
        }

        var filesSb = new StringBuilder();
        foreach (var (change, _, newContent, diffText) in filesToProcess)
        {
            string path = change.Item.Path;
            filesSb.AppendLine("################################################################################");
            filesSb.AppendLine($"FILE: {path}");
            filesSb.AppendLine("################################################################################");

            string changeType = change.ChangeType?.ToLowerInvariant() ?? "";
            if (changeType.Contains("delete"))
            {
                filesSb.AppendLine("--- [STATUS: FILE WAS DELETED IN THIS PR] ---\n");
                continue;
            }

            if (!string.IsNullOrEmpty(diffText))
            {
                filesSb.AppendLine($"--- [DIFF WITH LINE NUMBERS (- OLD / + NEW) WITH {contextLines} CONTEXT LINES] ---");
                filesSb.AppendLine(diffText);
                filesSb.AppendLine();
            }
            else if (changeType.Contains("add"))
            {
                filesSb.AppendLine("--- [NEW FILE ADDED IN THIS PR - ALL LINES ARE NEW] ---\n");
            }

            bool isNewFile = changeType.Contains("add");
            if (!string.IsNullOrEmpty(newContent) && (!diffOnly || isNewFile))
            {
                filesSb.AppendLine(isNewFile
                    ? "--- [NEW FILE CONTENTS (WITH LINE NUMBERS)] ---"
                    : "--- [PROPOSED / NEW FULL FILE (WITH LINE NUMBERS)] ---");

                var lines = newContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < lines.Length; i++)
                {
                    filesSb.AppendLine($"{(i + 1),5} | {lines[i]}");
                }
            }
            filesSb.AppendLine();
        }

        string classificationRules = diffOnly
            ? $"Each modified file section below provides a 'DIFF WITH LINE NUMBERS' with ~{contextLines} lines of surrounding context.\n" +
              "1. Lines marked with '+' in the DIFF section represent NEW changes introduced by this PR.\n" +
              "   - Any defect or bug found on a '+' line MUST be tagged as 🆕 NEW.\n" +
              "2. Context lines (lines marked with space '  L') are PRE-EXISTING code shown for reference.\n" +
              "   - Any defect or bug found on context lines MUST be tagged as 🔄 PRE-EXISTING.\n" +
              "3. If an entire file is labeled '[NEW FILE ADDED IN THIS PR]', all lines in that file are 🆕 NEW."
            : "Each file section below provides a 'DIFF WITH LINE NUMBERS' and 'PROPOSED / NEW FULL FILE'.\n" +
              "1. Lines marked with '+' in the DIFF section represent NEW changes introduced by this PR.\n" +
              "   - Any defect or bug found on a '+' line MUST be tagged as 🆕 NEW.\n" +
              "2. Any defect or bug found on a line NOT marked with '+' in the DIFF is PRE-EXISTING code.\n" +
              "   - You MUST tag these as 🔄 PRE-EXISTING.\n" +
              "3. If an entire file is labeled '[NEW FILE ADDED IN THIS PR]', all lines in that file are 🆕 NEW.";

        string changesSummaryText = $"- Total Changes:            {summary.TotalChanges}\n" +
                                   $"- 🆕 New Added Files:       {summary.Added}\n" +
                                   $"- ✏️ Edited / Modified:     {summary.Edited}\n" +
                                   $"- 🔄 Renamed Files:         {summary.Renamed}\n" +
                                   $"- 🗑️ Deleted Files:         {summary.Deleted}\n" +
                                   $"- ⚡ Skipped (Formatting):  {summary.SkippedFormatting}\n" +
                                   $"- 🛡️ Excluded (Config):     {summary.SkippedExcluded}\n" +
                                   $"- 🚫 Skipped (Binary):      {summary.SkippedBinary}";

        // BUILD DIVERGENCE / CONFLICT EXECUTIVE SUMMARY PRE-AMBLE INSTRUCTION
        string preambleInstruction = "";
        if (hasConflicts && isBehindTarget)
        {
            preambleInstruction = $@"CRITICAL EXECUTIVE SUMMARY WARNING REQUIREMENT:
A branch divergence and merge conflict condition was detected. You MUST start your '### 📋 Executive Summary' with this EXACT visual alert block before any other text:

> ### ⚠️ DEPLOYMENT & CONFLICT ALERT
> **🔴 Merge Conflicts & Branch Out of Date with `{targetBranchClean}`**
> * **Status:** This pull request has unresolved merge conflicts with `{targetBranchClean}` and is also missing recent updates merged into `{targetBranchClean}`.
> * **Required Action:** The developer must sync with `{targetBranchClean}` (`git pull origin {targetBranchClean}`), resolve conflicting code locally, and re-test before this PR can be completed.
";
        }
        else if (hasConflicts)
        {
            preambleInstruction = $@"CRITICAL EXECUTIVE SUMMARY WARNING REQUIREMENT:
Merge conflicts exist. You MUST start your '### 📋 Executive Summary' with this EXACT visual alert block before any other text:

> ### ⚠️ DEPLOYMENT & CONFLICT ALERT
> **🔴 Unresolved Merge Conflicts with `{targetBranchClean}`**
> * **Status:** This pull request cannot be merged automatically due to conflicting code changes with `{targetBranchClean}`.
> * **Required Action:** The developer must pull `{targetBranchClean}` (`git pull origin {targetBranchClean}`), resolve conflicts locally, and re-test before this PR can be completed.
";
        }
        else if (isBehindTarget)
        {
            preambleInstruction = $@"CRITICAL EXECUTIVE SUMMARY WARNING REQUIREMENT:
Branch divergence exists. You MUST start your '### 📋 Executive Summary' with this EXACT visual alert block before any other text:

> ### ⚠️ DEPLOYMENT & SYNCHRONIZATION ALERT
> **🟠 Feature Branch is Out of Date with `{targetBranchClean}`**
> * **Status:** `{targetBranchClean}` has advanced with new commits that are not present in this branch (`{sourceBranchClean}`).
> * **Required Action:** The developer must sync their branch (`git pull origin {targetBranchClean}`) and verify compatibility with latest `{targetBranchClean}` updates prior to merging.
";
        }

        string rawTemplate = LoadPromptTemplate();
        string schemaText = LoadJsonSchema();
        string deepLinkExample = $"{serverUrl}/{collection}/{project}/_git/{repo}/pullrequest/{prId}?_a=files&path=<UrlEncodedFilePath>&line=45&lineEnd=45&lineStartColumn=1&lineEndColumn=1";

        string bundleText = rawTemplate
            .Replace("{{STRICT_JSON_SCHEMA}}", schemaText)
            .Replace("{{PR_ID}}", prId)
            .Replace("{{PR_TITLE}}", pr.Title ?? "PR")
            .Replace("{{AUTHOR}}", pr.CreatedBy?.DisplayName ?? "Unknown")
            .Replace("{{REPOSITORY}}", repo)
            .Replace("{{SERVER_URL}}", serverUrl)
            .Replace("{{COLLECTION}}", collection)
            .Replace("{{PROJECT}}", project)
            .Replace("{{SOURCE_BRANCH}}", sourceBranchRaw)
            .Replace("{{TARGET_BRANCH}}", targetBranchRaw)
            .Replace("{{PR_STATE}}", pr.Status?.ToUpper() ?? "ACTIVE")
            .Replace("{{CONFLICT_CHECK}}", pr.MergeStatus ?? "Unknown")
            .Replace("{{PR_DESCRIPTION}}", string.IsNullOrWhiteSpace(pr.Description) ? "No description provided." : pr.Description)
            .Replace("{{EXPORT_DATE}}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{{EXPORT_TIMESTAMP}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{{CHANGES_SUMMARY}}", changesSummaryText)
            .Replace("{{CLASSIFICATION_RULES}}", classificationRules)
            .Replace("{{DEEP_LINK_EXAMPLE}}", deepLinkExample)
            .Replace("{{ACTIVE_THREADS}}", activeThreadsSb.ToString())
            .Replace("{{FILES_AND_DIFFS}}", filesSb.ToString())
            .Replace("{{DIVERGENCE_PREAMBLE_INSTRUCTION}}", preambleInstruction);

        string dateFolder = DateTime.Now.ToString("yyyy_MM_dd");
        string timeStamp = DateTime.Now.ToString("HH_mm_ss");
        string sanitizedTitle = Regex.Replace(pr.Title ?? "PR", @"[^\w\-]", "_").Trim('_');
        string modeSuffix = diffOnly ? "DiffOnly" : "Full";
        string saveDir = Path.Combine(AppContext.BaseDirectory, "pr_reviews_data", repo, dateFolder);
        Directory.CreateDirectory(saveDir);

        string savePath = Path.Combine(saveDir, $"PR_Bundle_{prId}_{sanitizedTitle}_{modeSuffix}_{timeStamp}.txt");
        await File.WriteAllTextAsync(savePath, bundleText, Encoding.UTF8);

        Log($"✔ PR bundle file saved: {savePath}");
        CopyFileToWindowsClipboard(savePath);
        Log($"✔ Bundle file copied to clipboard: {Path.GetFileName(savePath)}");
        Log($"  * Metrics: {summary.TotalChanges} changes, ~{bundleText.Length / 4:N0} tokens.");

        return new PrExportBundleResult
        {
            BundleText = bundleText,
            SavedFilePath = savePath,
            PrId = prId,
            Title = pr.Title ?? "PR",
            Repository = repo,
            SourceBranch = sourceBranchClean,
            TargetBranch = targetBranchClean,
            Summary = summary,
            TotalCharacters = bundleText.Length,
            EstimatedTokens = bundleText.Length / 4,
            CreatedAt = DateTime.Now
        };
    }

    public async Task<string> ReflectReviewAsync(string rawJsonPayload, bool confirmApproval, NetworkCredential? creds, string? patToken, string fallbackPrInput)
    {
        Log("Parsing review payload JSON...");
        string cleanedJson = CleanJsonText(rawJsonPayload);
        var payload = JsonSerializer.Deserialize<AiReviewPayload>(cleanedJson, JsonOptions)
                      ?? throw new InvalidOperationException("Failed to deserialize review payload.");

        using var client = _tfs.CreateClient(creds, patToken);

        string serverUrl = payload.Metadata?.ServerUrl ?? "";
        string collection = payload.Metadata?.Collection ?? "";
        string project = payload.Metadata?.Project ?? "";
        string repo = payload.Metadata?.Repository ?? "";
        string prId = payload.Metadata?.PrId ?? "";

        if (string.IsNullOrWhiteSpace(prId) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(project))
        {
            if (!string.IsNullOrWhiteSpace(fallbackPrInput))
            {
                var fallback = ParsePrInput(fallbackPrInput);
                serverUrl = string.IsNullOrWhiteSpace(serverUrl) ? fallback.ServerUrl : serverUrl;
                collection = string.IsNullOrWhiteSpace(collection) ? fallback.Collection : collection;
                project = string.IsNullOrWhiteSpace(project) ? fallback.Project : project;
                repo = string.IsNullOrWhiteSpace(repo) ? fallback.Repo : repo;
                prId = string.IsNullOrWhiteSpace(prId) ? fallback.PrId : prId;
            }
        }

        Log($"Executing reflection on PR #{prId} [{repo}]...");
        var pr = await _tfs.GetPullRequestDetailsAsync(client, serverUrl, collection, project, repo, prId);
        var currentReviewer = await _tfs.GetCurrentAuthenticatedUserAsync(client, serverUrl);

        string sanitizedSummary = SanitizeWorkItemMentions(payload.SummaryMarkdown);

        Log("🧹 Removing old Executive Summary and Findings Table...");
        var existingThreads = await _tfs.GetPullRequestThreadsAsync(client, serverUrl, collection, project, repo, prId);
        int deletedSummaries = 0;
        foreach (var th in existingThreads)
        {
            if (IsExecutiveSummaryOrFindingsThread(th))
            {
                await DeleteEntireThreadAsync(client, serverUrl, collection, project, repo, prId, th);
                deletedSummaries++;
            }
        }
        if (deletedSummaries > 0)
        {
            Log($"🧹 Removed {deletedSummaries} old Executive Summary / Findings Table thread(s).");
        }

        if (payload.ResolvedThreads?.Count > 0)
        {
            Log($"Verifying and resolving {payload.ResolvedThreads.Count} thread(s)...");
            var liveThreads = await _tfs.GetPullRequestThreadsAsync(client, serverUrl, collection, project, repo, prId);

            foreach (var thread in payload.ResolvedThreads)
            {
                if (thread.ThreadId <= 0) continue;
                var liveThread = liveThreads.FirstOrDefault(t => t.Id == thread.ThreadId);
                if (liveThread == null || liveThread.IsDeleted) continue;

                string verdict = thread.EffectiveVerdict;
                string explanation = SanitizeWorkItemMentions(thread.EffectiveExplanation);

                if (verdict.Equals("RESOLVED", StringComparison.OrdinalIgnoreCase))
                {
                    await _tfs.UpdateThreadStatusAsync(client, serverUrl, collection, project, repo, prId, thread.ThreadId, 2, $"[RESOLVED]: {explanation}");
                    Log($"✔ Thread {thread.ThreadId} marked as RESOLVED.");
                }
                else
                {
                    if (liveThread.Status != 1)
                    {
                        await _tfs.UpdateThreadStatusAsync(client, serverUrl, collection, project, repo, prId, thread.ThreadId, 1, null);
                    }

                    bool alreadyReplied = liveThread.Comments != null && liveThread.Comments.Any(c => 
                        !c.IsDeleted && c.Content != null && c.Content.Contains("[STILL BROKEN]", StringComparison.OrdinalIgnoreCase));

                    if (!alreadyReplied)
                    {
                        await _tfs.AddCommentToThreadAsync(client, serverUrl, collection, project, repo, prId, thread.ThreadId, $"[REOPENED / STILL BROKEN]: {explanation}");
                        Log($"⚠ Thread {thread.ThreadId} reopened and marked as STILL BROKEN.");
                    }
                }
            }
        }

        if (payload.InlineComments?.Count > 0)
        {
            foreach (var c in payload.InlineComments) c.Tag = ResolveTag(c);

            var sortedComments = payload.InlineComments
                .OrderBy(c => GetTagWeight(c.Tag))
                .ThenByDescending(c => c.Line)
                .ToList();

            Log($"Syncing {sortedComments.Count} inline review comment(s)...");
            var currentThreads = await _tfs.GetPullRequestThreadsAsync(client, serverUrl, collection, project, repo, prId);

            int insertedCount = 0;
            int replacedCount = 0;
            int reopenedCount = 0;
            int skippedWontFixCount = 0;
            int unchangedCount = 0;

            foreach (var comment in sortedComments)
            {
                string sanitizedComment = SanitizeWorkItemMentions(comment.Comment);
                sanitizedComment = ColorizeCommentHeader(sanitizedComment, comment.Tag);
                string fileName = Path.GetFileName(comment.FilePath);

                try
                {
                    var matches = currentThreads
                        .Where(th => MatchesExistingThread(th, comment.FilePath, comment.Line, comment.Tag, sanitizedComment))
                        .ToList();

                    if (matches.Count > 0)
                    {
                        var primaryThread = matches[0];
                        var existingComment = primaryThread.Comments?.FirstOrDefault(c => !c.IsDeleted && !IsSystemComment(c));

                        for (int i = 1; i < matches.Count; i++)
                        {
                            await DeleteEntireThreadAsync(client, serverUrl, collection, project, repo, prId, matches[i]);
                        }

                        if (primaryThread.Status == 3)
                        {
                            bool isDevWontFix = WasWontFixByDeveloper(primaryThread, pr, currentReviewer);
                            if (!isDevWontFix)
                            {
                                skippedWontFixCount++;
                                Log($"🔒 [SKIPPED] Thread {primaryThread.Id} at Line {comment.Line} in {fileName}: Marked 'Won't Fix' by Reviewer.");
                                continue;
                            }
                            else
                            {
                                await _tfs.UpdateThreadStatusAsync(client, serverUrl, collection, project, repo, prId, primaryThread.Id, 1, null);
                                await _tfs.AddCommentToThreadAsync(client, serverUrl, collection, project, repo, prId, primaryThread.Id, 
                                    $"[REOPENED]: Defect cannot be marked 'Won't Fix' by author while code remains unfixed:\n\n{sanitizedComment}");
                                reopenedCount++;
                                Log($"🔄 [REOPENED] Thread {primaryThread.Id} at Line {comment.Line} in {fileName}: Author marked 'Won't Fix'; reopened by reviewer.");
                                continue;
                            }
                        }

                        if (primaryThread.Status == 2)
                        {
                            await _tfs.UpdateThreadStatusAsync(client, serverUrl, collection, project, repo, prId, primaryThread.Id, 1, null);
                            await _tfs.AddCommentToThreadAsync(client, serverUrl, collection, project, repo, prId, primaryThread.Id, 
                                $"[REOPENED]: Defect is still present:\n\n{sanitizedComment}");
                            reopenedCount++;
                            Log($"🔄 [REOPENED] Thread {primaryThread.Id} at Line {comment.Line} in {fileName}: Reopened resolved thread.");
                        }
                        else if (existingComment != null && AreCommentsEquivalent(existingComment.Content, sanitizedComment))
                        {
                            unchangedCount++;
                            Log($"🔒 [UNCHANGED] Thread {primaryThread.Id} at Line {comment.Line} in {fileName}: comment is already identical.");
                        }
                        else
                        {
                            try
                            {
                                if (existingComment != null)
                                    await _tfs.UpdateThreadCommentContentAsync(client, serverUrl, collection, project, repo, prId, primaryThread.Id, existingComment.Id, sanitizedComment);
                                else
                                    await _tfs.AddCommentToThreadAsync(client, serverUrl, collection, project, repo, prId, primaryThread.Id, sanitizedComment);
                            }
                            catch
                            {
                                await DeleteEntireThreadAsync(client, serverUrl, collection, project, repo, prId, primaryThread);
                                await _tfs.CreateInlineThreadAsync(client, serverUrl, collection, project, repo, prId, comment.FilePath, comment.Line, sanitizedComment);
                            }

                            replacedCount++;
                            Log($"✔ [REPLACED] Thread {primaryThread.Id} at Line {comment.Line} in {fileName}: updated existing thread.");
                        }
                    }
                    else
                    {
                        await _tfs.CreateInlineThreadAsync(client, serverUrl, collection, project, repo, prId, comment.FilePath, comment.Line, sanitizedComment);
                        insertedCount++;
                        Log($"✔ [NEW] Line {comment.Line} in {fileName}: published new inline comment.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"❌ Line {comment.Line} in {fileName} error: {ex.Message}");
                }
            }
            Log($"✔ Inline comments sync completed: {insertedCount} new, {replacedCount} updated, {reopenedCount} reopened, {skippedWontFixCount} skipped, {unchangedCount} unchanged.");
        }

        if (!sanitizedSummary.Contains("| File | Lines |", StringComparison.OrdinalIgnoreCase) && payload.InlineComments?.Count > 0)
        {
            var tableSb = new StringBuilder();
            tableSb.AppendLine();
            tableSb.AppendLine();
            tableSb.AppendLine("### 🔍 Findings Summary Table");
            tableSb.AppendLine("| File | Lines | Origin | Severity | Issue Summary |");
            tableSb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");

            var groupedComments = payload.InlineComments
                .OrderBy(c => GetTagWeight(c.Tag))
                .ThenBy(c => c.FilePath)
                .ThenBy(c => c.Line);

            foreach (var comment in groupedComments)
            {
                string fileName = Path.GetFileName(comment.FilePath);
                string encodedPath = Uri.EscapeDataString(comment.FilePath);
                string deepLink = $"{serverUrl}/{collection}/{project}/_git/{repo}/pullrequest/{prId}?_a=files&path={encodedPath}&line={comment.Line}&lineEnd={comment.Line}&lineStartColumn=1&lineEndColumn=1";
                string origin = comment.Tag.Contains("PRE", StringComparison.OrdinalIgnoreCase) ? "🔄 PRE-EXISTING" : "🆕 NEW";
                string severity = comment.Tag.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) ? "🔴 CRITICAL" :
                                 comment.Tag.Contains("MEDIUM", StringComparison.OrdinalIgnoreCase) ? "🟡 MEDIUM" : "🟢 LOW";

                string issueTitle = comment.Comment.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? comment.Tag;
                issueTitle = Regex.Replace(issueTitle, @"^[#\s\*>-]+", "").Trim();
                if (issueTitle.Length > 85) issueTitle = issueTitle.Substring(0, 82) + "...";

                tableSb.AppendLine($"| `{fileName}` | [Line {comment.Line}]({deepLink}) | {origin} | {severity} | {issueTitle} |");
            }

            sanitizedSummary += tableSb.ToString();
            Log("ℹ Attached Findings Summary Table directly under Executive Summary.");
        }

        Log("Posting Executive Summary & Findings Table to PR discussion feed...");
        await _tfs.CreateFeedCommentAsync(client, serverUrl, collection, project, repo, prId, sanitizedSummary);

        if (confirmApproval && payload.Metadata?.Verdict?.Equals("APPROVE", StringComparison.OrdinalIgnoreCase) == true)
        {
            Log("Applying +10 (Approved) vote...");
            await _tfs.VotePullRequestAsync(client, serverUrl, collection, project, repo, prId, 10);
            Log("✔ Approved vote cast successfully.");
        }

        string dateFolder = DateTime.Now.ToString("yyyy_MM_dd");
        string timeStamp = DateTime.Now.ToString("HH_mm_ss");
        string saveDir = Path.Combine(AppContext.BaseDirectory, "pr_reviews_data", repo, dateFolder);
        Directory.CreateDirectory(saveDir);
        string savePath = Path.Combine(saveDir, $"AI_Review_{prId}_Reflected_{timeStamp}.txt");
        await File.WriteAllTextAsync(savePath, cleanedJson, Encoding.UTF8);

        OpenPrInBrowser(fallbackPrInput, cleanedJson);
        return savePath;
    }

    public async Task ClearReviewAsync(string prInput, NetworkCredential? creds, string? patToken)
    {
        using var client = _tfs.CreateClient(creds, patToken);
        var (serverUrl, collection, project, repo, prId) = ParsePrInput(prInput);

        Log($"Purging all AI review threads for PR #{prId}...");
        var threads = await _tfs.GetPullRequestThreadsAsync(client, serverUrl, collection, project, repo, prId);

        int deletedCount = 0;
        foreach (var th in threads)
        {
            if (th == null || th.IsDeleted) continue;
            bool isAiThread = th.Comments?.Any(c => c != null && !c.IsDeleted && IsAiGeneratedComment(c.Content)) == true;

            if (isAiThread)
            {
                await DeleteEntireThreadAsync(client, serverUrl, collection, project, repo, prId, th);
                deletedCount++;
            }
        }

        Log($"[PURGED] Deleted {deletedCount} automated AI review thread(s).");
        Log("Resetting reviewer vote to 0...");
        await _tfs.VotePullRequestAsync(client, serverUrl, collection, project, repo, prId, 0);
        OpenPrInBrowser(prInput);
    }

    public async Task<(string ThreadsText, string SavedFilePath)> ExtractThreadsAsync(
        string prInput, 
        NetworkCredential? creds, 
        string? patToken, 
        bool includeSystemComments = false)
    {
        TryRunDailyCleanup(retentionDays: 3);

        Log("Connecting to TFS API to fetch all threads and discussions...");
        using var client = _tfs.CreateClient(creds, patToken);
        var (serverUrl, collection, project, repo, prId) = ParsePrInput(prInput);

        Log($"Retrieving PR #{prId} details and discussion feed...");
        var pr = await _tfs.GetPullRequestDetailsAsync(client, serverUrl, collection, project, repo, prId);
        var rawThreads = await _tfs.GetPullRequestThreadsAsync(client, serverUrl, collection, project, repo, prId);

        var validThreads = rawThreads.Where(t => t != null && !t.IsDeleted).ToList();
        int totalThreads = validThreads.Count;
        int activeThreads = validThreads.Count(t => t.Status == 1);
        int resolvedThreads = validThreads.Count(t => t.Status == 2);
        int closedThreads = validThreads.Count(t => t.Status == 4);
        int totalComments = 0;
        int generalFeedThreads = 0;
        int inlineThreads = 0;

        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("TFS PULL REQUEST DISCUSSIONS & COMMENTS EXPORT");
        sb.AppendLine($"Exported At:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Server / Org:    {serverUrl}/{collection}/{project}");
        sb.AppendLine($"Repository:      {repo}");
        sb.AppendLine($"Pull Request:    #{pr.PullRequestId} - {pr.Title}");
        sb.AppendLine($"Author:          {pr.CreatedBy?.DisplayName ?? "Unknown"}");
        sb.AppendLine($"Branches:        {pr.SourceRefName} -> {pr.TargetRefName}");
        sb.AppendLine($"PR Status:       {pr.Status.ToUpperInvariant()}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        var sortedThreads = validThreads
            .OrderBy(t => t.ThreadContext == null ? 0 : 1)
            .ThenBy(t => t.ThreadContext?.FilePath ?? "")
            .ThenBy(t => t.ThreadContext?.RightFileEnd?.Line ?? t.ThreadContext?.RightFileStart?.Line ?? 0)
            .ThenBy(t => t.Id)
            .ToList();

        var threadDetailsSb = new StringBuilder();

        foreach (var th in sortedThreads)
        {
            var rawComments = th.Comments?.Where(c => c != null && !c.IsDeleted).ToList() ?? new List<TfsComment>();
            var displayComments = includeSystemComments ? rawComments : rawComments.Where(c => !IsSystemComment(c)).ToList();
            if (displayComments.Count == 0) continue;

            totalComments += displayComments.Count;
            bool isInline = th.ThreadContext != null && !string.IsNullOrWhiteSpace(th.ThreadContext.FilePath);
            if (isInline) inlineThreads++; else generalFeedThreads++;

            string statusName = FormatThreadStatus(th.Status);
            string locationText = isInline
                ? $"{th.ThreadContext!.FilePath} (Line {th.ThreadContext.RightFileEnd?.Line ?? th.ThreadContext.RightFileStart?.Line ?? 0})"
                : "[General PR Discussion Feed]";

            threadDetailsSb.AppendLine("--------------------------------------------------------------------------------");
            threadDetailsSb.AppendLine($"THREAD ID: {th.Id} | Status: {statusName} | Type: {(isInline ? "INLINE CODE" : "GENERAL FEED")}");
            threadDetailsSb.AppendLine($"Location:  {locationText}");
            threadDetailsSb.AppendLine("--------------------------------------------------------------------------------");

            var rootComments = displayComments.Where(c => c.ParentCommentId == 0).OrderBy(c => c.Id).ToList();
            var replyComments = displayComments.Where(c => c.ParentCommentId != 0).OrderBy(c => c.Id).ToList();

            if (rootComments.Count == 0 && displayComments.Count > 0)
            {
                rootComments = displayComments;
                replyComments.Clear();
            }

            foreach (var root in rootComments)
            {
                AppendCommentBlock(threadDetailsSb, root, indentLevel: 0);
                var directReplies = replyComments.Where(r => r.ParentCommentId == root.Id).ToList();
                foreach (var reply in directReplies) AppendCommentBlock(threadDetailsSb, reply, indentLevel: 1);
            }

            var unparented = replyComments.Where(r => !rootComments.Any(root => root.Id == r.ParentCommentId)).ToList();
            foreach (var orphan in unparented) AppendCommentBlock(threadDetailsSb, orphan, indentLevel: 1);

            threadDetailsSb.AppendLine();
        }

        sb.AppendLine("DISCUSSIONS OVERVIEW METRICS:");
        sb.AppendLine($"- Total Valid Threads:      {totalThreads}");
        sb.AppendLine($"- General Feed Threads:     {generalFeedThreads}");
        sb.AppendLine($"- Inline Code Threads:      {inlineThreads}");
        sb.AppendLine($"- Active / Unresolved:      {activeThreads}");
        sb.AppendLine($"- Fixed / Resolved:         {resolvedThreads}");
        sb.AppendLine($"- Closed / Won't Fix:       {closedThreads}");
        sb.AppendLine($"- Exported Comments Count:  {totalComments}");
        sb.AppendLine($"  (System events included: {(includeSystemComments ? "YES" : "NO")})");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.Append(threadDetailsSb);

        string bundleText = sb.ToString();
        string dateFolder = DateTime.Now.ToString("yyyy_MM_dd");
        string timeStamp = DateTime.Now.ToString("HH_mm_ss");
        string sanitizedTitle = Regex.Replace(pr.Title, @"[^\w\-]", "_").Trim('_');
        string saveDir = Path.Combine(AppContext.BaseDirectory, "pr_reviews_data", repo, dateFolder);
        Directory.CreateDirectory(saveDir);
        string savePath = Path.Combine(saveDir, $"PR_Discussions_{prId}_{sanitizedTitle}_{timeStamp}.txt");
        await File.WriteAllTextAsync(savePath, bundleText, Encoding.UTF8);

        CopyFileToWindowsClipboard(savePath);
        Log($"✔ PR discussions export completed: {totalThreads} thread(s), {totalComments} comment(s).");
        Log($"  * Saved File: {savePath}");

        return (bundleText, savePath);
    }

    private static void AppendCommentBlock(StringBuilder sb, TfsComment comment, int indentLevel)
    {
        string indent = new string(' ', indentLevel * 4);
        string prefix = indentLevel > 0 ? "↳ " : "";
        string author = comment.Author?.DisplayName ?? "Unknown";
        string timeStr = comment.PublishedDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "No Timestamp";
        string sysBadge = IsSystemComment(comment) ? " [SYSTEM EVENT]" : "";

        sb.AppendLine($"{indent}{prefix}[Comment #{comment.Id}] {author} ({timeStr}){sysBadge}:");
        var lines = (comment.Content ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var line in lines) sb.AppendLine($"{indent}  {line}");
        sb.AppendLine();
    }

    private static string FormatThreadStatus(int status) => status switch
    {
        1 => "ACTIVE (UNRESOLVED)",
        2 => "FIXED / RESOLVED",
        3 => "WONT FIX",
        4 => "CLOSED",
        5 => "BY DESIGN",
        6 => "PENDING",
        _ => $"STATUS({status})"
    };

    private static bool MatchesExistingThread(TfsThread th, string filePath, int line, string tag, string commentText)
    {
        if (th == null || th.IsDeleted || th.ThreadContext == null) return false;
        if (th.Status != 1 && th.Status != 2 && th.Status != 3) return false;

        var firstComment = th.Comments?.FirstOrDefault(c => c != null && !c.IsDeleted && !string.IsNullOrWhiteSpace(c.Content));
        if (firstComment == null || !IsAiGeneratedComment(firstComment.Content)) return false;

        string thPath = (th.ThreadContext.FilePath ?? "").TrimStart('/').Replace('\\', '/');
        string targetPath = filePath.TrimStart('/').Replace('\\', '/');
        if (!string.Equals(thPath, targetPath, StringComparison.OrdinalIgnoreCase)) return false;

        int thLine = th.ThreadContext.RightFileEnd?.Line ?? th.ThreadContext.RightFileStart?.Line ?? 0;
        if (thLine == line) return true;

        if (Math.Abs(thLine - line) <= 5)
        {
            string existing = firstComment.Content;
            string newTitle = commentText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string existingTitle = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            if (!string.IsNullOrWhiteSpace(newTitle) && !string.IsNullOrWhiteSpace(existingTitle))
            {
                string cleanNew = Regex.Replace(newTitle, @"[^\w]", " ").Trim();
                string cleanExisting = Regex.Replace(existingTitle, @"[^\w]", " ").Trim();
                var words = cleanNew.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToList();
                int matches = words.Count(w => cleanExisting.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (matches >= 2) return true;
            }
        }
        return false;
    }

    private static string ResolveTag(InlineCommentDto comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.Tag)) return comment.Tag.Trim();
        if (!string.IsNullOrWhiteSpace(comment.Comment))
        {
            var match = Regex.Match(comment.Comment, @"\[(NEW|PRE-EXISTING|PREEXISTING)\s+(CRITICAL|MEDIUM|LOW)\]", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value;

            string firstLine = comment.Comment.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            bool isPre = firstLine.Contains("PRE", StringComparison.OrdinalIgnoreCase);
            if (firstLine.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)) return isPre ? "[PRE-EXISTING CRITICAL]" : "[NEW CRITICAL]";
            if (firstLine.Contains("MEDIUM", StringComparison.OrdinalIgnoreCase)) return isPre ? "[PRE-EXISTING MEDIUM]" : "[NEW MEDIUM]";
            if (firstLine.Contains("LOW", StringComparison.OrdinalIgnoreCase)) return isPre ? "[PRE-EXISTING LOW]" : "[NEW LOW]";
        }
        return "[NEW MEDIUM]";
    }

    private static string ColorizeCommentHeader(string commentText, string tag)
    {
        if (string.IsNullOrWhiteSpace(commentText)) return commentText;
        string originIcon = tag.Contains("PRE", StringComparison.OrdinalIgnoreCase) ? "🔄" : "🆕";
        string severityIcon = tag.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase) ? "🔴" :
                              tag.Contains("MEDIUM", StringComparison.OrdinalIgnoreCase) ? "🟡" : "🟢";
        string badge = $"{originIcon} {severityIcon}";

        if (commentText.Contains("🔴") || commentText.Contains("🟡") || commentText.Contains("🟢")) return commentText;

        var regex = new Regex(@"^(#{1,6}\s*)?(\[(NEW|PRE-EXISTING|PREEXISTING)\s+(CRITICAL|MEDIUM|LOW)\])", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (regex.IsMatch(commentText))
        {
            return regex.Replace(commentText, m =>
            {
                string hashes = m.Groups[1].Success ? m.Groups[1].Value : "### ";
                return $"{hashes}{badge} {m.Groups[2].Value}";
            }, 1);
        }
        return $"### {badge} {tag}\n\n{commentText}";
    }

    private static int GetTagWeight(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return 5;
        string t = tag.ToUpperInvariant();
        bool isPreExisting = t.Contains("PRE-EXISTING") || t.Contains("PREEXISTING") || t.Contains("PRE EXISTING");
        bool isCritical = t.Contains("CRITICAL");
        bool isMedium = t.Contains("MEDIUM");
        bool isLow = t.Contains("LOW");

        if (isPreExisting)
        {
            if (isLow) return 1;
            if (isMedium) return 2;
            if (isCritical) return 3;
            return 2;
        }
        else
        {
            if (isLow) return 4;
            if (isMedium) return 5;
            if (isCritical) return 6;
            return 5;
        }
    }

    private static bool AreCommentsEquivalent(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return true;
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        string normA = Regex.Replace(a.Replace("\r\n", "\n").Trim(), @"\s+", " ");
        string normB = Regex.Replace(b.Replace("\r\n", "\n").Trim(), @"\s+", " ");
        return string.Equals(normA, normB, StringComparison.Ordinal);
    }

    private static bool IsExecutiveSummaryOrFindingsThread(TfsThread th)
    {
        if (th == null || th.IsDeleted) return false;
        var first = th.Comments?.FirstOrDefault(c => c != null && !c.IsDeleted && !string.IsNullOrWhiteSpace(c.Content));
        if (first?.Content == null) return false;

        string content = first.Content;
        return content.Contains("Executive Summary", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Findings Summary Table", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Findings Table", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("| File | Lines |", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("📋 Executive Summary");
    }

    private static async Task DeleteEntireThreadAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, TfsThread th)
    {
        if (th.Comments != null)
        {
            foreach (var comment in th.Comments.Where(c => c != null && !c.IsDeleted).OrderByDescending(c => c.Id))
            {
                try { await client.DeleteAsync($"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads/{th.Id}/comments/{comment.Id}?api-version=5.0"); } catch { }
            }
        }
        try
        {
            var body = new { status = 4 };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads/{th.Id}?api-version=5.0") { Content = content };
            await client.SendAsync(req);
        }
        catch { }
    }

    private static bool IsAiGeneratedComment(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.Contains("Executive Summary", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Findings Summary Table", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Findings Table", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Review Summary", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("| File | Lines |") ||
               content.Contains("[NEW CRITICAL]") || content.Contains("[NEW MEDIUM]") || content.Contains("[NEW LOW]") ||
               content.Contains("[PRE-EXISTING CRITICAL]") || content.Contains("[PRE-EXISTING MEDIUM]") || content.Contains("[PRE-EXISTING LOW]") ||
               content.Contains("[RESOLVED]:") || content.Contains("[STILL BROKEN]:");
    }

    public static string SanitizeWorkItemMentions(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return Regex.Replace(input, @"(?<![#\w])#(\d+)", "No. $1");
    }

    public static bool IsSystemComment(TfsComment? comment)
    {
        if (comment == null) return false;
        if (comment.CommentType.ValueKind == JsonValueKind.Number && comment.CommentType.TryGetInt32(out int typeNum) && typeNum == 2) return true;
        if (comment.CommentType.ValueKind == JsonValueKind.String && string.Equals(comment.CommentType.GetString(), "system", StringComparison.OrdinalIgnoreCase)) return true;

        string author = comment.Author?.DisplayName ?? "";
        if (author.Contains("Microsoft.TeamFoundation.System", StringComparison.OrdinalIgnoreCase)) return true;

        string content = comment.Content?.Trim() ?? "";
        return content.EndsWith("joined as a reviewer", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("voted 10", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("voted -10", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("Vote of ", StringComparison.OrdinalIgnoreCase) ||
               content.StartsWith("The reference ", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("was reset: Changes pushed", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanJsonText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
        else if (text.StartsWith("```")) text = text.Substring(3);
        if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
        text = text.Trim();
        return Regex.Replace(text, @"(?<!\\)\$""([^""\r\n\\]+)(?<!\\)""", @"$\""$1\""");
    }

    public static void CopyFileToWindowsClipboard(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath) || !Environment.UserInteractive) return;
        try
        {
            string psCommand = $"Set-Clipboard -Path '{filePath.Replace("'", "''")}'";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{psCommand}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit(1000);
        }
        catch { }
    }

    public static void OpenFolderInExplorer(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath) || !Environment.UserInteractive) return;
        try { Process.Start("explorer.exe", $"/select,\"{filePath}\""); } catch { }
    }

    public void OpenPrInBrowser(string prInputOrUrl, string? jsonPayload = null)
    {
        if (!Environment.UserInteractive) return;
        try
        {
            string url = ResolvePrWebUrl(prInputOrUrl, jsonPayload);
            if (!string.IsNullOrEmpty(url))
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                Log($"🌐 Opened Pull Request in browser: {url}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠ Could not open browser automatically: {ex.Message}");
        }
    }

    private string ResolvePrWebUrl(string prInputOrUrl, string? jsonPayload)
    {
        if (prInputOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            prInputOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return prInputOrUrl.Split('?')[0];
        }

        if (!string.IsNullOrWhiteSpace(jsonPayload))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<AiReviewPayload>(CleanJsonText(jsonPayload), JsonOptions);
                if (payload != null && !string.IsNullOrEmpty(payload.Metadata.PrId))
                {
                    var m = payload.Metadata;
                    return $"{m.ServerUrl}/{m.Collection}/{m.Project}/_git/{m.Repository}/pullrequest/{m.PrId}";
                }
            }
            catch { }
        }

        if (int.TryParse(prInputOrUrl.Trim(), out int prId))
        {
            var parsed = ParsePrInput(prInputOrUrl);
            if (!string.IsNullOrEmpty(parsed.Repo))
            {
                return $"{parsed.ServerUrl}/{parsed.Collection}/{parsed.Project}/_git/{parsed.Repo}/pullrequest/{prId}";
            }
        }
        return prInputOrUrl;
    }

    private static (string ServerUrl, string Collection, string Project, string Repo, string PrId) ParsePrInput(string input)
    {
        input = (input ?? "").Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            int prIndex = Array.FindIndex(segments, s => s.Equals("pullrequest", StringComparison.OrdinalIgnoreCase) || s.Equals("pullrequests", StringComparison.OrdinalIgnoreCase));
            if (prIndex >= 0 && prIndex + 1 < segments.Length)
            {
                string prId = segments[prIndex + 1];
                string repo = segments[prIndex - 1].Equals("_git", StringComparison.OrdinalIgnoreCase) ? segments[prIndex - 2] : segments[prIndex - 1];
                string project = segments[prIndex - 3].Equals("_git", StringComparison.OrdinalIgnoreCase) ? segments[prIndex - 4] : segments[prIndex - 3];
                string collection = segments[0];
                return ($"{uri.Scheme}://{uri.Authority}", collection, project, repo, prId);
            }
        }
        throw new ArgumentException("A full TFS Pull Request URL is required (e.g. http://tfs/Collection/Project/_git/Repo/pullrequest/1234). Plain PR numbers are not supported.");
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASQATools.Shared.Models;

public class DomainLoginRequest
{
    public string Domain { get; set; } = "malath";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class AuthStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
}

public class RoleDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsFixed { get; set; } = false;
    public List<string> Permissions { get; set; } = new();
}

public class CreateRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}

public class UserRoleMapping
{
    public string Username { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}

public class RbacDatabaseDto
{
    public List<RoleDefinition> Roles { get; set; } = new();
    public List<UserRoleMapping> UserMappings { get; set; } = new();
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

public class UpdateRolePermissionsRequest
{
    public string RoleName { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}

public class AssignUserRoleRequest
{
    public string Username { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
}

public class TfsIdentitySearchResult
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string MailAddress { get; set; } = string.Empty;
}

public class PrExportRequest
{
    public string PrInput { get; set; } = string.Empty;
    public string PatToken { get; set; } = string.Empty;
    public bool DiffOnly { get; set; } = false;
    public int ContextLines { get; set; } = 6;
    public bool ForceExport { get; set; } = false;
}

public class ReflectReviewRequest
{
    public string ReviewPayloadText { get; set; } = string.Empty;
    public bool ConfirmApproval { get; set; } = false;
    public string PatToken { get; set; } = string.Empty;
    public string FallbackPrInput { get; set; } = string.Empty;
}

public class ReflectReviewResult
{
    public string Message { get; set; } = string.Empty;
    public string SavedFilePath { get; set; } = string.Empty;
}

public class ClearReviewRequest
{
    public string PrInput { get; set; } = string.Empty;
    public string PatToken { get; set; } = string.Empty;
}

public class ExtractThreadsRequest
{
    public string PrInput { get; set; } = string.Empty;
    public string PatToken { get; set; } = string.Empty;
    public bool IncludeSystemComments { get; set; } = false;
}

public class ExtractThreadsResult
{
    public string ThreadsText { get; set; } = string.Empty;
    public string SavedFilePath { get; set; } = string.Empty;
}

public class CodebaseExportRequest
{
    public string RootPath { get; set; } = string.Empty;
    public bool IncludeLineNumbers { get; set; } = true;
    public bool ExcludeStandardArtifacts { get; set; } = true;
    public string CustomExclusions { get; set; } = string.Empty;
    public List<string>? IncludedPaths { get; set; } = null;
    public List<string>? ExcludedPaths { get; set; } = null;
}

public class ScanTreeChildrenRequest
{
    public string RootPath { get; set; } = string.Empty;
    public string? SubPath { get; set; } = null;
}

public class TreeNodeItemDto
{
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    public bool IsDefaultExcluded { get; set; }
    public List<TreeNodeItemDto> Children { get; set; } = new();
}

public class OperationResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public List<string> Logs { get; set; } = new();
    public string? Error { get; set; }
}

public class CodebaseExportResult
{
    public string BundleText { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalLines { get; set; }
    public long TotalCharacters { get; set; }
    public long EstimatedTokens { get; set; }
}

public class PrChangesSummary
{
    public int TotalChanges => Added + Edited + Renamed + Deleted + SkippedBinary + SkippedFormatting + SkippedExcluded;
    public int Added { get; set; }
    public int Edited { get; set; }
    public int Renamed { get; set; }
    public int Deleted { get; set; }
    public int SkippedBinary { get; set; }
    public int SkippedFormatting { get; set; }
    public int SkippedExcluded { get; set; }
    public List<string> SkippedFileNames { get; set; } = new();
}

public class PreflightAlertInfo
{
    public bool IsBlocked { get; set; }
    public bool HasConflicts { get; set; }
    public bool IsBehindTarget { get; set; }
    public string TargetBranch { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Commands { get; set; } = new();
}

public class ReviewMetadata
{
    [JsonPropertyName("pr_id")] public string PrId { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("repository")] public string Repository { get; set; } = string.Empty;
    [JsonPropertyName("server_url")] public string ServerUrl { get; set; } = string.Empty;
    [JsonPropertyName("collection")] public string Collection { get; set; } = string.Empty;
    [JsonPropertyName("project")] public string Project { get; set; } = string.Empty;
    [JsonPropertyName("pr_api_url")] public string PrApiUrl { get; set; } = string.Empty;
    [JsonPropertyName("repo_api_url")] public string RepoApiUrl { get; set; } = string.Empty;
    [JsonPropertyName("feature_branch")] public string FeatureBranch { get; set; } = string.Empty;
    [JsonPropertyName("target_branch")] public string TargetBranch { get; set; } = string.Empty;
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "CHANGES_REQUESTED";
    [JsonPropertyName("export_date")] public string ExportDate { get; set; } = string.Empty;
    [JsonPropertyName("export_timestamp")] public string ExportTimestamp { get; set; } = string.Empty;
}

public class ResolvedThreadDto
{
    [JsonPropertyName("thread_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ThreadId { get; set; }

    [JsonPropertyName("file_path")] 
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("verdict")] 
    public string Verdict { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveVerdict => !string.IsNullOrWhiteSpace(Verdict) ? Verdict : Status;

    [JsonPropertyName("explanation")] 
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveExplanation => !string.IsNullOrWhiteSpace(Explanation) ? Explanation : Comment;
}

public class InlineCommentDto
{
    [JsonPropertyName("file_path")] 
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Line { get; set; }

    [JsonPropertyName("tag")] 
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("comment")] 
    public string Comment { get; set; } = string.Empty;
}

public class AiReviewPayload
{
    [JsonPropertyName("metadata")] public ReviewMetadata Metadata { get; set; } = new();
    [JsonPropertyName("summary_markdown")] public string SummaryMarkdown { get; set; } = string.Empty;
    [JsonPropertyName("resolved_threads")] public List<ResolvedThreadDto> ResolvedThreads { get; set; } = new();
    [JsonPropertyName("inline_comments")] public List<InlineCommentDto> InlineComments { get; set; } = new();
}

public class TfsPullRequestDetails
{
    public int PullRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceRefName { get; set; } = string.Empty;
    public string TargetRefName { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string MergeStatus { get; set; } = "unknown";
    public bool HasConflicts { get; set; }
    public string MergeFailureType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public TfsIdentity? CreatedBy { get; set; }
    public TfsRepository? Repository { get; set; }
    public TfsCommitRef? LastMergeSourceCommit { get; set; }
    public TfsCommitRef? LastMergeTargetCommit { get; set; }
}

public class TfsIdentity
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class TfsRepository
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public TfsProject? Project { get; set; }
}

public class TfsProject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class TfsCommitRef
{
    public string CommitId { get; set; } = string.Empty;
}

public class TfsThread
{
    public int Id { get; set; }
    [JsonPropertyName("isDeleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("status")] public JsonElement RawStatus { get; set; }

    [JsonIgnore]
    public int Status
    {
        get
        {
            if (RawStatus.ValueKind == JsonValueKind.Number && RawStatus.TryGetInt32(out int s)) return s;
            if (RawStatus.ValueKind == JsonValueKind.String)
            {
                return RawStatus.GetString()?.ToLowerInvariant() switch
                {
                    "active" => 1, "fixed" => 2, "wontfix" => 3, "closed" => 4, "bydesign" => 5, "pending" => 6, _ => 1
                };
            }
            return 1;
        }
    }

    public List<TfsComment> Comments { get; set; } = new();
    public TfsThreadContext? ThreadContext { get; set; }
}

public class TfsComment
{
    public int Id { get; set; }
    public int ParentCommentId { get; set; }
    public string Content { get; set; } = string.Empty;
    [JsonPropertyName("isDeleted")] public bool IsDeleted { get; set; }
    public JsonElement CommentType { get; set; }
    public TfsIdentity? Author { get; set; }
    [JsonPropertyName("publishedDate")] public DateTime? PublishedDate { get; set; }
}

public class TfsThreadContext
{
    public string FilePath { get; set; } = string.Empty;
    public TfsPosition? RightFileStart { get; set; }
    public TfsPosition? RightFileEnd { get; set; }
}

public class TfsPosition
{
    public int Line { get; set; }
    public int Offset { get; set; } = 1;
}

public class TfsListResponse<T>
{
    [JsonPropertyName("value")] public List<T> Value { get; set; } = new();
}

public class TfsIterationChanges
{
    [JsonPropertyName("changeEntries")] public List<TfsChangeEntry>? ChangeEntries { get; set; }
}

public class TfsChangeEntry
{
    public string ChangeType { get; set; } = string.Empty;
    public string? OriginalPath { get; set; }
    public string? SourceServerItem { get; set; }
    public TfsItem Item { get; set; } = new();
}

public class TfsItem
{
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public string? GitObjectType { get; set; }
}

public class TfsIteration
{
    public int Id { get; set; }
    public TfsCommitRef? SourceRefCommit { get; set; }
    public TfsCommitRef? TargetRefCommit { get; set; }
    public TfsCommitRef? CommonRefCommit { get; set; }
}

public class PrExportBundleResult
{
    public string BundleText { get; set; } = string.Empty;
    public string SavedFilePath { get; set; } = string.Empty;
    public string PrId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public PrChangesSummary Summary { get; set; } = new();
    public long TotalCharacters { get; set; }
    public long EstimatedTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public PreflightAlertInfo? PreflightAlert { get; set; }
}

public class CodeReflectorFileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = "Modified";
    public string Content { get; set; } = string.Empty;
    public bool FileExistsOnDisk { get; set; }
}

public class CodeReflectorParseResult
{
    public List<CodeReflectorFileEntry> Files { get; set; } = new();
    public int TotalHunks { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public class CodeReflectorApplyRequest
{
    public string TargetDirectory { get; set; } = string.Empty;
    public string RawAiResponse { get; set; } = string.Empty;
    public bool CreateBackups { get; set; } = true;
}

public class OpenFolderRequest
{
    public string FilePath { get; set; } = string.Empty;
}

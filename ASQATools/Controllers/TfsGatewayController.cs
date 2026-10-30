using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using ASQATools.Shared.Models;
using ASQATools.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json;

namespace ASQATools.Controllers;

[Authorize]
[ApiController]
[Route("api/tfsbridge")]
public class TfsGatewayController : ControllerBase
{
    private readonly TfsApiClient _tfs;
    private readonly PrReviewEngineService _engine;
    private readonly CodebaseExporterService _exporter;
    private readonly CodeReflectorService _reflector;
    private readonly RbacSecurityService _rbac;
    private readonly DomainCredentialProvider _credentials;
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;

    public TfsGatewayController(
        TfsApiClient tfs, 
        PrReviewEngineService engine,
        CodebaseExporterService exporter,
        CodeReflectorService reflector,
        RbacSecurityService rbac,
        DomainCredentialProvider credentials,
        IConfiguration config,
        IDataProtectionProvider provider)
    {
        _tfs = tfs;
        _engine = engine;
        _exporter = exporter;
        _reflector = reflector;
        _rbac = rbac;
        _credentials = credentials;
        _config = config;
        _protector = provider.CreateProtector("Tfs.Domain.Credentials.v1");
    }

    [AllowAnonymous]
    [HttpGet("default-directory")]
    public IActionResult GetDefaultDirectory()
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        if (!Directory.Exists(dir)) dir = Directory.GetCurrentDirectory();
        return Ok(new { directory = dir });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] DomainLoginRequest req)
    {
        string serverUrl = _config["Tfs:ServerUrl"] ?? "http://tfs";
        var creds = new NetworkCredential(req.Username, req.Password, req.Domain);
        var identity = await _tfs.TestCredentialsAsync(serverUrl, creds);
        if (identity == null) return Unauthorized("Invalid Domain credentials for " + serverUrl);

        string normalizedUser = RbacSecurityService.NormalizeUsername(req.Username);
        string encryptedSecret = _protector.Protect(JsonSerializer.Serialize(req));
        var roles = _rbac.GetUserRoles(normalizedUser);
        var permissions = _rbac.GetUserPermissions(normalizedUser);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, normalizedUser),
            new(ClaimTypes.GivenName, identity.DisplayName),
            new("tfs_creds", encryptedSecret)
        };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity));

        return Ok(new AuthStatusResponse
        {
            IsAuthenticated = true,
            DisplayName = identity.DisplayName,
            Username = normalizedUser,
            Roles = roles,
            Permissions = permissions
        });
    }

    [AllowAnonymous]
    [HttpGet("auth-status")]
    public IActionResult GetAuthStatus()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            string username = User.Identity.Name ?? "";
            return Ok(new AuthStatusResponse
            {
                IsAuthenticated = true,
                Username = username,
                DisplayName = User.FindFirst(ClaimTypes.GivenName)?.Value ?? username,
                Roles = _rbac.GetUserRoles(username),
                Permissions = _rbac.GetUserPermissions(username)
            });
        }
        return Ok(new AuthStatusResponse { IsAuthenticated = false });
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        return Ok();
    }

    [HttpGet("download-file")]
    public IActionResult DownloadFile([FromQuery] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest("File path must be provided.");

        string allowedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "pr_reviews_data")) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(filePath);

        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            return Forbid("Access to files outside the review repository is restricted.");

        if (System.IO.File.Exists(fullPath))
        {
            byte[] bytes = System.IO.File.ReadAllBytes(fullPath);
            string fileName = Path.GetFileName(fullPath);
            return File(bytes, "application/octet-stream", fileName);
        }
        return NotFound("Requested file does not exist on server.");
    }

    [HttpGet("identities/search")]
    public async Task<IActionResult> SearchTfsIdentities([FromQuery] string query)
    {
        string serverUrl = _config["Tfs:ServerUrl"] ?? "http://tfs";
        string collection = _config["Tfs:DefaultCollection"] ?? "ESolutionsHub";

        var creds = _credentials.GetCredentials();
        using var client = _tfs.CreateClient(creds);
        var matches = await _tfs.SearchIdentitiesSafeAsync(client, serverUrl, query, collection);
        return Ok(matches);
    }

    [HttpGet("rbac/database")]
    public IActionResult GetRbacDatabase()
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.IsAdmin(username)) return Forbid();
        return Ok(_rbac.GetDatabase());
    }

    [HttpPost("rbac/roles/create")]
    public IActionResult CreateRole([FromBody] CreateRoleRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.IsAdmin(username)) return Forbid();

        bool created = _rbac.CreateRole(req.Name, req.Description, req.Permissions);
        if (created)
            return Ok(new OperationResponse<string> { Success = true, Data = $"Role '{req.Name}' created successfully." });

        return BadRequest($"Could not create role '{req.Name}'. It may already exist or contain invalid characters.");
    }

    [HttpDelete("rbac/roles/{name}")]
    public IActionResult DeleteRole(string name)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.IsAdmin(username)) return Forbid();

        bool deleted = _rbac.DeleteRole(name);
        if (deleted)
            return Ok(new OperationResponse<string> { Success = true, Data = $"Role '{name}' deleted." });

        return BadRequest($"Role '{name}' cannot be deleted (system locked or not found).");
    }

    [HttpPost("rbac/roles/permissions")]
    public IActionResult UpdateRolePermissions([FromBody] UpdateRolePermissionsRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.IsAdmin(username)) return Forbid();

        var db = _rbac.GetDatabase();
        var role = db.Roles.FirstOrDefault(r => string.Equals(r.Name, req.RoleName, StringComparison.OrdinalIgnoreCase));
        if (role != null)
        {
            role.Permissions = req.Permissions;
            _rbac.SaveDatabase(db);
            return Ok(new OperationResponse<string> { Success = true, Data = $"Permissions updated for role '{req.RoleName}'." });
        }
        return BadRequest($"Role '{req.RoleName}' not found.");
    }

    [HttpPost("rbac/users/assign")]
    public IActionResult AssignUserRole([FromBody] AssignUserRoleRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.IsAdmin(username)) return Forbid();

        string targetUser = RbacSecurityService.NormalizeUsername(req.Username);
        if (string.IsNullOrWhiteSpace(targetUser)) return BadRequest("Invalid username.");

        var db = _rbac.GetDatabase();
        var userMap = db.UserMappings.FirstOrDefault(u => string.Equals(u.Username, targetUser, StringComparison.OrdinalIgnoreCase));
        if (userMap == null)
        {
            userMap = new UserRoleMapping { Username = targetUser, Roles = new List<string>() };
            db.UserMappings.Add(userMap);
        }

        if (!userMap.Roles.Contains(req.RoleName, StringComparer.OrdinalIgnoreCase))
        {
            userMap.Roles.Add(req.RoleName);
            _rbac.SaveDatabase(db);
        }

        return Ok(new OperationResponse<string> { Success = true, Data = $"Role '{req.RoleName}' assigned to user '{targetUser}'." });
    }

    [HttpPost("rbac/users/unassign")]
    public IActionResult UnassignUserRole([FromBody] AssignUserRoleRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.IsAdmin(username)) return Forbid();

        _rbac.RemoveUserRole(req.Username, req.RoleName);
        return Ok(new OperationResponse<string> { Success = true, Data = $"Role '{req.RoleName}' removed from user '{req.Username}'." });
    }

    [HttpPost("export-bundle")]
    public async Task<IActionResult> ExportBundle([FromBody] PrExportRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermPrEngine) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }
        if (req.ForceExport && !_rbac.HasPermission(username, RbacSecurityService.PermPrBypassGuard) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var logs = new List<string>();
        Action<string> logger = msg => logs.Add(msg);
        _engine.OnLog += logger;
        try
        {
            var creds = _credentials.GetCredentials();
            var bundle = await _engine.ExportBundleAsync(req.PrInput, creds, req.PatToken, req.DiffOnly, req.ContextLines, req.ForceExport);
            return Ok(new OperationResponse<PrExportBundleResult> { Success = true, Data = bundle, Logs = logs });
        }
        catch (Exception ex)
        {
            logs.Add($"❌ {ex.Message}");
            return Ok(new OperationResponse<PrExportBundleResult> { Success = false, Error = ex.Message, Logs = logs });
        }
        finally { _engine.OnLog -= logger; }
    }

    [HttpPost("reflect-review")]
    public async Task<IActionResult> ReflectReview([FromBody] ReflectReviewRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermPrEngine) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }
        if (req.ConfirmApproval && !_rbac.HasPermission(username, RbacSecurityService.PermPrApprove) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var logs = new List<string>();
        Action<string> logger = msg => logs.Add(msg);
        _engine.OnLog += logger;
        try
        {
            var creds = _credentials.GetCredentials();
            var savedPath = await _engine.ReflectReviewAsync(req.ReviewPayloadText, req.ConfirmApproval, creds, req.PatToken, req.FallbackPrInput);
            return Ok(new OperationResponse<ReflectReviewResult>
            {
                Success = true,
                Data = new ReflectReviewResult { Message = "Review applied successfully", SavedFilePath = savedPath },
                Logs = logs
            });
        }
        catch (Exception ex)
        {
            logs.Add($"❌ {ex.Message}");
            return Ok(new OperationResponse<ReflectReviewResult> { Success = false, Error = ex.Message, Logs = logs });
        }
        finally { _engine.OnLog -= logger; }
    }

    [HttpPost("clear-review")]
    public async Task<IActionResult> ClearReview([FromBody] ClearReviewRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermPrEngine) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var logs = new List<string>();
        Action<string> logger = msg => logs.Add(msg);
        _engine.OnLog += logger;
        try
        {
            var creds = _credentials.GetCredentials();
            await _engine.ClearReviewAsync(req.PrInput, creds, req.PatToken);
            return Ok(new OperationResponse<string> { Success = true, Data = "Cleared successfully", Logs = logs });
        }
        catch (Exception ex)
        {
            logs.Add($"❌ {ex.Message}");
            return Ok(new OperationResponse<string> { Success = false, Error = ex.Message, Logs = logs });
        }
        finally { _engine.OnLog -= logger; }
    }

    [HttpPost("extract-threads")]
    public async Task<IActionResult> ExtractThreads([FromBody] ExtractThreadsRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermPrExtractDiscussions) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var logs = new List<string>();
        Action<string> logger = msg => logs.Add(msg);
        _engine.OnLog += logger;
        try
        {
            var creds = _credentials.GetCredentials();
            var (threadsText, savedPath) = await _engine.ExtractThreadsAsync(req.PrInput, creds, req.PatToken, req.IncludeSystemComments);
            return Ok(new OperationResponse<ExtractThreadsResult>
            {
                Success = true,
                Data = new ExtractThreadsResult { ThreadsText = threadsText, SavedFilePath = savedPath },
                Logs = logs
            });
        }
        catch (Exception ex)
        {
            logs.Add($"❌ {ex.Message}");
            return Ok(new OperationResponse<ExtractThreadsResult> { Success = false, Error = ex.Message, Logs = logs });
        }
        finally { _engine.OnLog -= logger; }
    }

    [HttpPost("scan-tree-children")]
    public IActionResult ScanTreeChildren([FromBody] ScanTreeChildrenRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermCodeExporter) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var items = _exporter.GetDirectoryChildren(req.RootPath, req.SubPath);
        return Ok(items);
    }

    [HttpPost("purge-backups")]
    public IActionResult PurgeBackups([FromBody] CodebaseExportRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermCodeExporter) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        string targetDir = string.IsNullOrWhiteSpace(req.RootPath) ? Directory.GetCurrentDirectory() : req.RootPath;
        if (!Directory.Exists(targetDir)) return BadRequest("Directory not found.");

        int deletedCount = 0;
        try
        {
            var bakFiles = Directory.EnumerateFiles(targetDir, "*.bak", SearchOption.AllDirectories).ToList();
            foreach (var file in bakFiles)
            {
                try
                {
                    System.IO.File.Delete(file);
                    deletedCount++;
                }
                catch { }
            }
            return Ok(new OperationResponse<int>
            {
                Success = true,
                Data = deletedCount,
                Logs = new List<string> { $"🧹 Successfully purged {deletedCount} .bak backup file(s) from {targetDir}." }
            });
        }
        catch (Exception ex)
        {
            return Ok(new OperationResponse<int> { Success = false, Error = ex.Message });
        }
    }

    [HttpPost("export-codebase")]
    public async Task<IActionResult> ExportCodebase([FromBody] CodebaseExportRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermCodeExporter) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var logs = new List<string>();
        Action<string> logger = msg => logs.Add(msg);
        _exporter.OnLog += logger;
        try
        {
            var result = await _exporter.ExportProjectAsync(req);
            return Ok(new OperationResponse<CodebaseExportResult> { Success = true, Data = result, Logs = logs });
        }
        catch (Exception ex)
        {
            logs.Add($"❌ {ex.Message}");
            return Ok(new OperationResponse<CodebaseExportResult> { Success = false, Error = ex.Message, Logs = logs });
        }
        finally { _exporter.OnLog -= logger; }
    }

    [HttpPost("reflector-parse")]
    public IActionResult ReflectorParse([FromBody] CodeReflectorApplyRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermCodeExporter) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var parsed = _reflector.ParseAiResponse(req.TargetDirectory, req.RawAiResponse);
        return Ok(parsed);
    }

    [HttpPost("reflector-apply")]
    public async Task<IActionResult> ReflectorApply([FromBody] CodeReflectorApplyRequest req)
    {
        string username = User.Identity?.Name ?? "";
        if (!_rbac.HasPermission(username, RbacSecurityService.PermCodeExporter) && !_rbac.IsAdmin(username))
        {
            return Forbid();
        }

        var logs = new List<string>();
        Action<string> logger = msg => logs.Add(msg);
        _reflector.OnLog += logger;
        try
        {
            int count = await _reflector.ApplyChangesAsync(req.TargetDirectory, req.RawAiResponse, req.CreateBackups);
            return Ok(new OperationResponse<int> { Success = true, Data = count, Logs = logs });
        }
        catch (Exception ex)
        {
            logs.Add($"❌ {ex.Message}");
            return Ok(new OperationResponse<int> { Success = false, Error = ex.Message, Logs = logs });
        }
        finally { _reflector.OnLog -= logger; }
    }
}

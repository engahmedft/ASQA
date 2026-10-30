using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ASQATools.Shared.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ASQATools.Services;

public class RbacSecurityService
{
    private readonly IDataProtector _protector;
    private readonly string _storagePath;
    private static readonly object _dbLock = new();

    public static readonly string PermRoleManagement = "admin-roles";
    public static readonly string PermPrEngine = "pr-engine";
    public static readonly string PermPrWontFixApprove = "pr-wontfix-approve";
    public static readonly string PermPrApprove = "pr-approve";
    public static readonly string PermPrBypassGuard = "pr-bypass-guard";
    public static readonly string PermPrExtractDiscussions = "pr-extract-discussions";
    public static readonly string PermCodeExporter = "code-exporter";
    public static readonly string PermDbExcel = "db-excel";
    public static readonly string PermReports = "reports";

    public RbacSecurityService(IDataProtectionProvider provider, IWebHostEnvironment env)
    {
        _protector = provider.CreateProtector("ASQA.RBAC.Security.v1");
        string dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _storagePath = Path.Combine(dir, "security_db.enc");
        InitializeDefaultDatabase();
    }

    public static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return string.Empty;
        username = username.Trim();

        int backslashIndex = username.LastIndexOf('\\');
        if (backslashIndex >= 0) username = username.Substring(backslashIndex + 1);

        int atIndex = username.IndexOf('@');
        if (atIndex >= 0) username = username.Substring(0, atIndex);

        return username.ToLowerInvariant();
    }

    public static int GetRoleSortOrder(string name) => name switch
    {
        "Admin" => 1,
        "Quality Review Manager" => 2,
        "Quality Reviewer" => 3,
        "Developer" => 4,
        "Code Exporter" => 5,
        "Excel Extractor" => 6,
        "Reports Extractor" => 7,
        _ => 50
    };

    public static List<RoleDefinition> GetDefaultRoles()
    {
        return new List<RoleDefinition>
        {
            new() { Name = "Admin", Description = "System Administrators", IsFixed = true, Permissions = new List<string> { PermRoleManagement } },
            new() { Name = "Quality Review Manager", Description = "QA Lead & Review Manager (Full PR Review Authority)", IsFixed = false, Permissions = new List<string> { PermPrEngine, PermPrWontFixApprove, PermPrApprove, PermPrBypassGuard, PermPrExtractDiscussions } },
            new() { Name = "Quality Reviewer", Description = "Quality Assurance Reviewer (PR Engine + Won't Fix Approval)", IsFixed = true, Permissions = new List<string> { PermPrEngine, PermPrWontFixApprove } },
            new() { Name = "Developer", Description = "Software Engineers (PR Engine only)", IsFixed = false, Permissions = new List<string> { PermPrEngine } },
            new() { Name = "Code Exporter", Description = "Dedicated Codebase Exporter & Reflector", IsFixed = false, Permissions = new List<string> { PermCodeExporter } },
            new() { Name = "Excel Extractor", Description = "Excel DB Extractor Specialist", IsFixed = false, Permissions = new List<string> { PermDbExcel } },
            new() { Name = "Reports Extractor", Description = "Reporting & Analytics Specialists", IsFixed = false, Permissions = new List<string> { PermReports } }
        };
    }

    public static RbacDatabaseDto CreateDefaultDatabase()
    {
        return new RbacDatabaseDto
        {
            Roles = GetDefaultRoles(),
            UserMappings = new List<UserRoleMapping>
            {
                new() { Username = "a.abdulhafez", Roles = new List<string> { "Admin" } }
            },
            LastModified = DateTime.UtcNow
        };
    }

    private void InitializeDefaultDatabase()
    {
        lock (_dbLock)
        {
            if (File.Exists(_storagePath)) return;
            var defaultDb = CreateDefaultDatabase();
            SaveDatabaseInternal(defaultDb);
        }
    }

    public RbacDatabaseDto GetDatabase()
    {
        lock (_dbLock)
        {
            if (!File.Exists(_storagePath)) InitializeDefaultDatabase();
            try
            {
                byte[] encryptedBytes = File.ReadAllBytes(_storagePath);
                byte[] decryptedBytes = _protector.Unprotect(encryptedBytes);
                string json = Encoding.UTF8.GetString(decryptedBytes);
                var db = JsonSerializer.Deserialize<RbacDatabaseDto>(json) ?? new RbacDatabaseDto();

                bool modified = false;

                if (db.Roles == null || db.Roles.Count == 0)
                {
                    db.Roles = GetDefaultRoles();
                    modified = true;
                }
                else
                {
                    if (!db.Roles.Any(r => string.Equals(r.Name, "Admin", StringComparison.OrdinalIgnoreCase)))
                    {
                        db.Roles.Insert(0, new() { Name = "Admin", Description = "System Administrators", IsFixed = true, Permissions = new List<string> { PermRoleManagement } });
                        modified = true;
                    }
                    if (!db.Roles.Any(r => string.Equals(r.Name, "Quality Reviewer", StringComparison.OrdinalIgnoreCase)))
                    {
                        db.Roles.Add(new() { Name = "Quality Reviewer", Description = "Quality Assurance Reviewer (PR Engine + Won't Fix Approval)", IsFixed = true, Permissions = new List<string> { PermPrEngine, PermPrWontFixApprove } });
                        modified = true;
                    }
                    if (!db.Roles.Any(r => string.Equals(r.Name, "Quality Review Manager", StringComparison.OrdinalIgnoreCase)))
                    {
                        db.Roles.Add(new() { Name = "Quality Review Manager", Description = "QA Lead & Review Manager (Full PR Review Authority)", IsFixed = false, Permissions = new List<string> { PermPrEngine, PermPrWontFixApprove, PermPrApprove, PermPrBypassGuard, PermPrExtractDiscussions } });
                        modified = true;
                    }
                }

                // Strictly apply sort order: Developer before Code Exporter
                db.Roles = db.Roles.OrderBy(r => GetRoleSortOrder(r.Name)).ToList();

                foreach (var r in db.Roles)
                {
                    if (string.Equals(r.Name, "Admin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Name, "Quality Reviewer", StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsFixed = true;
                    }
                }

                if (modified)
                {
                    SaveDatabaseInternal(db);
                }

                return db;
            }
            catch
            {
                var fallback = CreateDefaultDatabase();
                SaveDatabaseInternal(fallback);
                return fallback;
            }
        }
    }

    private void SaveDatabaseInternal(RbacDatabaseDto db)
    {
        db.LastModified = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = _protector.Protect(plainBytes);
        File.WriteAllBytes(_storagePath, encryptedBytes);
    }

    public void SaveDatabase(RbacDatabaseDto db)
    {
        lock (_dbLock) SaveDatabaseInternal(db);
    }

    public bool CreateRole(string name, string description, List<string> permissions)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length < 2 || name.Length > 40) return false;
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9\s._\-]+$")) return false;

        lock (_dbLock)
        {
            var db = GetDatabase();
            if (db.Roles.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                return false;

            db.Roles.Add(new RoleDefinition
            {
                Name = name,
                Description = description?.Trim() ?? "",
                IsFixed = false,
                Permissions = permissions ?? new()
            });

            db.Roles = db.Roles.OrderBy(r => GetRoleSortOrder(r.Name)).ToList();
            SaveDatabaseInternal(db);
            return true;
        }
    }

    public bool DeleteRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName)) return false;
        if (string.Equals(roleName, "Admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(roleName, "Quality Reviewer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_dbLock)
        {
            var db = GetDatabase();
            var role = db.Roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
            if (role == null || role.IsFixed) return false;

            db.Roles.Remove(role);
            foreach (var user in db.UserMappings)
            {
                user.Roles.RemoveAll(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase));
            }
            db.UserMappings.RemoveAll(u => u.Roles.Count == 0);

            SaveDatabaseInternal(db);
            return true;
        }
    }

    public void RemoveUserRole(string username, string roleName)
    {
        string norm = NormalizeUsername(username);
        if (norm == "a.abdulhafez" && string.Equals(roleName, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_dbLock)
        {
            var db = GetDatabase();
            var mapping = db.UserMappings.FirstOrDefault(u => u.Username == norm);

            if (mapping != null)
            {
                mapping.Roles.RemoveAll(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase));
                if (mapping.Roles.Count == 0 && norm != "a.abdulhafez")
                {
                    db.UserMappings.Remove(mapping);
                }
                SaveDatabaseInternal(db);
            }
        }
    }

    public List<string> GetUserRoles(string username)
    {
        string norm = NormalizeUsername(username);
        if (string.IsNullOrEmpty(norm)) return new();

        var db = GetDatabase();
        var mapping = db.UserMappings.FirstOrDefault(u => u.Username == norm);
        return mapping?.Roles ?? new List<string>();
    }

    public List<string> GetUserPermissions(string username)
    {
        var roles = GetUserRoles(username);
        var db = GetDatabase();
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in roles)
        {
            var def = db.Roles.FirstOrDefault(x => string.Equals(x.Name, r, StringComparison.OrdinalIgnoreCase));
            if (def != null)
            {
                foreach (var p in def.Permissions) permissions.Add(p);
            }
        }

        return permissions.ToList();
    }

    public bool HasPermission(string username, string permissionKey)
    {
        var perms = GetUserPermissions(username);
        return perms.Contains(permissionKey, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAdmin(string username)
    {
        var roles = GetUserRoles(username);
        return roles.Contains("Admin", StringComparer.OrdinalIgnoreCase);
    }
}

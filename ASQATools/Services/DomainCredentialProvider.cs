using System.Net;
using System.Text.Json;
using ASQATools.Shared.Models;
using Microsoft.AspNetCore.DataProtection;

namespace ASQATools.Services;

public class DomainCredentialProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDataProtector _protector;

    public DomainCredentialProvider(IHttpContextAccessor httpContextAccessor, IDataProtectionProvider provider)
    {
        _httpContextAccessor = httpContextAccessor;
        _protector = provider.CreateProtector("Tfs.Domain.Credentials.v1");
    }

    public NetworkCredential GetCredentials()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        var claim = user?.FindFirst("tfs_creds")?.Value;

        if (!string.IsNullOrEmpty(claim))
        {
            try
            {
                string decrypted = _protector.Unprotect(claim);
                try
                {
                    var req = JsonSerializer.Deserialize<DomainLoginRequest>(decrypted);
                    if (req != null && !string.IsNullOrEmpty(req.Username))
                        return new NetworkCredential(req.Username, req.Password, req.Domain);
                }
                catch { }

                var parts = decrypted.Split(new[] { "\\|" }, StringSplitOptions.None);
                if (parts.Length >= 3)
                {
                    return new NetworkCredential(parts[1], parts[2], parts[0]);
                }
            }
            catch (Exception)
            {
                // Fallback on error
            }
            return CredentialCache.DefaultNetworkCredentials;
        }

        // Fallback to local Windows account if running locally
        return CredentialCache.DefaultNetworkCredentials;
    }
}

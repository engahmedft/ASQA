using ASQATools.Shared.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ASQATools.Services;

public class TfsApiClient
{
    private readonly IHttpClientFactory _clientFactory;
    private static readonly ConcurrentDictionary<string, HttpMessageHandler> _handlerCache = new(StringComparer.OrdinalIgnoreCase);

    public TfsApiClient(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public HttpClient CreateClient(NetworkCredential? creds, string? pat = null)
    {
        if (!string.IsNullOrWhiteSpace(pat))
        {
            var patClient = _clientFactory.CreateClient();
            var authBytes = Encoding.ASCII.GetBytes($":{pat}");
            patClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            patClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return patClient;
        }

        string cacheKey = creds != null
            ? $"{creds.Domain}\\{creds.UserName}:{creds.Password}"
            : "DEFAULT_NTLM_CREDENTIALS";

        var handler = _handlerCache.GetOrAdd(cacheKey, _ => new SocketsHttpHandler
        {
            Credentials = creds ?? CredentialCache.DefaultNetworkCredentials,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 50,
            AutomaticDecompression = DecompressionMethods.All
        });

        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// Multi-tier, injection-safe identity search across on-premises TFS collections.
    /// </summary>
    public async Task<List<TfsIdentitySearchResult>> SearchIdentitiesSafeAsync(
        HttpClient client, 
        string serverUrl, 
        string query, 
        string primaryCollection = "ESolutionsHub")
    {
        var results = new List<TfsIdentitySearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        query = query.Trim();
        // Strict Security Validation: Minimum 2 chars, Maximum 40 chars, alphanumeric + safe symbols
        if (query.Length < 2 || query.Length > 40) return results;
        if (!Regex.IsMatch(query, @"^[a-zA-Z0-9\s._\-]+$")) return results;

        // Candidate paths on TFS Server
        var candidateCollections = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryCollection) && Regex.IsMatch(primaryCollection, @"^[a-zA-Z0-9_\-\.]+$"))
        {
            candidateCollections.Add(primaryCollection);
        }
        if (!candidateCollections.Contains("ESolutionsHub")) candidateCollections.Add("ESolutionsHub");
        if (!candidateCollections.Contains("DefaultCollection")) candidateCollections.Add("DefaultCollection");

        foreach (var col in candidateCollections)
        {
            try
            {
                // Tier 1: Collection-level modern REST API
                string url = $"{serverUrl.TrimEnd('/')}/{col}/_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(query)}&queryMembership=None&api-version=5.0";
                var resp = await client.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var items = await ParseIdentitiesResponseAsync(resp);
                    if (items.Count > 0) return items;
                }

                // Tier 2: Legacy TFS internal scoped identity endpoint fallback
                string legacyUrl = $"{serverUrl.TrimEnd('/')}/{col}/_api/_identity/ReadScopedIdentityInfo?__v=5&searchFilter=General&filterValue={Uri.EscapeDataString(query)}";
                var legResp = await client.GetAsync(legacyUrl);
                if (legResp.IsSuccessStatusCode)
                {
                    var items = await ParseIdentitiesResponseAsync(legResp);
                    if (items.Count > 0) return items;
                }
            }
            catch { }
        }

        // Tier 3: Server Root Endpoint fallback
        try
        {
            string rootUrl = $"{serverUrl.TrimEnd('/')}/_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(query)}&queryMembership=None&api-version=5.0";
            var rootResp = await client.GetAsync(rootUrl);
            if (rootResp.IsSuccessStatusCode)
            {
                return await ParseIdentitiesResponseAsync(rootResp);
            }
        }
        catch { }

        return results;
    }

    private static async Task<List<TfsIdentitySearchResult>> ParseIdentitiesResponseAsync(HttpResponseMessage resp)
    {
        var results = new List<TfsIdentitySearchResult>();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        
        JsonElement arrayElement;
        if (doc.RootElement.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array)
        {
            arrayElement = val;
        }
        else if (doc.RootElement.TryGetProperty("identities", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
        {
            arrayElement = idArr;
        }
        else
        {
            return results;
        }

        foreach (var item in arrayElement.EnumerateArray())
        {
            bool isContainer = item.TryGetProperty("isContainer", out var ic) && ic.GetBoolean();
            if (isContainer) continue; // Exclude groups/teams, only return real domain users

            string id = item.GetProperty("id").GetString() ?? "";
            string displayName = item.TryGetProperty("providerDisplayName", out var pdn)
                ? pdn.GetString() ?? ""
                : (item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "");

            string accountName = "";
            string mail = "";

            if (item.TryGetProperty("properties", out var props))
            {
                if (props.TryGetProperty("Account", out var acc))
                {
                    accountName = acc.ValueKind == JsonValueKind.String
                        ? acc.GetString() ?? ""
                        : (acc.TryGetProperty("$value", out var av) ? av.GetString() ?? "" : "");
                }
                if (props.TryGetProperty("Mail", out var m))
                {
                    mail = m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? ""
                        : (m.TryGetProperty("$value", out var mv) ? mv.GetString() ?? "" : "");
                }
            }

            if (string.IsNullOrEmpty(accountName)) accountName = displayName;

            results.Add(new TfsIdentitySearchResult
            {
                Id = id,
                DisplayName = displayName,
                AccountName = accountName,
                MailAddress = mail
            });
        }
        return results;
    }

    public async Task<TfsIdentity?> GetCurrentAuthenticatedUserAsync(HttpClient client, string serverUrl)
    {
        try
        {
            string userUrl = $"{serverUrl.TrimEnd('/')}/_apis/connectionData";
            var userResp = await client.GetAsync(userUrl);
            if (!userResp.IsSuccessStatusCode) return null;

            var userJson = await userResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(userJson);
            if (doc.RootElement.TryGetProperty("authenticatedUser", out var authUser))
            {
                string id = authUser.GetProperty("id").GetString() ?? "";
                string displayName = authUser.TryGetProperty("providerDisplayName", out var name)
                    ? name.GetString() ?? ""
                    : (authUser.TryGetProperty("customDisplayName", out var cname) ? cname.GetString() ?? "" : "");

                return new TfsIdentity { Id = id, DisplayName = displayName };
            }
        }
        catch { }
        return null;
    }

    public async Task<TfsIdentity?> TestCredentialsAsync(string serverUrl, NetworkCredential creds)
    {
        using var client = CreateClient(creds);
        return await GetCurrentAuthenticatedUserAsync(client, serverUrl);
    }

    public async Task<TfsPullRequestDetails> GetPullRequestDetailsAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}?api-version=5.0";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<TfsPullRequestDetails>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException($"Could not deserialize PR #{prId}.");
    }

    public async Task<List<TfsIteration>> GetPullRequestIterationsAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/iterations?api-version=5.0";
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new List<TfsIteration>();

        var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<TfsListResponse<TfsIteration>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result?.Value ?? new List<TfsIteration>();
    }

    public async Task<List<TfsChangeEntry>> GetIterationChangesAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, int iterationId)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/iterations/{iterationId}/changes?api-version=5.0";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<TfsIterationChanges>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result?.ChangeEntries ?? new List<TfsChangeEntry>();
    }

    public async Task<string> GetFileContentAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string filePath, string version)
    {
        string encodedPath = Uri.EscapeDataString(filePath);
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/items?path={encodedPath}&versionDescriptor.version={version}&versionDescriptor.versionType=commit&$format=text&api-version=5.0";

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/items?path={encodedPath}&versionDescriptor.version={version}&$format=text&api-version=5.0";
            response = await client.GetAsync(url);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<List<TfsThread>> GetPullRequestThreadsAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads?api-version=5.0";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<TfsListResponse<TfsThread>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result?.Value ?? new List<TfsThread>();
    }

    public async Task CreateFeedCommentAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, string markdownContent)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads?api-version=5.0";
        var body = new
        {
            comments = new[] { new { parentCommentId = 0, content = markdownContent, commentType = 1 } }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateInlineThreadAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, string filePath, int line, string commentMarkdown)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads?api-version=5.0";
        var body = new
        {
            comments = new[] { new { parentCommentId = 0, content = commentMarkdown, commentType = 1 } },
            status = 1,
            threadContext = new
            {
                filePath = filePath,
                rightFileStart = new { line = line, offset = 1 },
                rightFileEnd = new { line = line, offset = 1 }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateThreadStatusAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, int threadId, int status, string? replyComment)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads/{threadId}?api-version=5.0";
        var body = new { status = status };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        if (!string.IsNullOrWhiteSpace(replyComment))
        {
            await AddCommentToThreadAsync(client, serverUrl, collection, project, repo, prId, threadId, replyComment);
        }
    }

    public async Task AddCommentToThreadAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, int threadId, string commentText)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads/{threadId}/comments?api-version=5.0";
        var body = new { content = commentText, parentCommentId = 0, commentType = 1 };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteThreadCommentAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, int threadId, int commentId)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads/{threadId}/comments/{commentId}?api-version=5.0";
        var response = await client.DeleteAsync(url);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task VotePullRequestAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, int vote)
    {
        var authUser = await GetCurrentAuthenticatedUserAsync(client, serverUrl);
        if (authUser == null || string.IsNullOrEmpty(authUser.Id)) return;

        string voteUrl = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/reviewers/{authUser.Id}?api-version=5.0";
        var body = new { vote = vote };
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await client.PutAsync(voteUrl, content);

        if (vote == 0 && response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateThreadCommentContentAsync(HttpClient client, string serverUrl, string collection, string project, string repo, string prId, int threadId, int commentId, string newContent)
    {
        string url = $"{serverUrl.TrimEnd('/')}/{collection}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/threads/{threadId}/comments/{commentId}?api-version=5.0";
        var body = new { content = newContent };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url) { Content = content };
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}

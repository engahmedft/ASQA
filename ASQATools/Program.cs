using Microsoft.AspNetCore.Authentication.Cookies;
using ASQATools.Services;
using ASQATools.Components;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<DomainCredentialProvider>();

builder.Services.AddHttpClient();
builder.Services.AddScoped<TfsApiClient>();
builder.Services.AddScoped<PrReviewEngineService>();
builder.Services.AddScoped<CodebaseExporterService>();
builder.Services.AddScoped<CodeReflectorService>();
builder.Services.AddSingleton<RbacSecurityService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ASQA_TFS_SESSION";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddControllers();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Enable WebAssembly debugging in Development
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// MapStaticAssets is required in .NET 9/10 to serve fingerprinted WASM client bundles
app.MapStaticAssets();
app.UseStaticFiles();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ASQATools.Client._Imports).Assembly);

app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        if (app.Environment.IsDevelopment() && Environment.UserInteractive && !Debugger.IsAttached)
        {
            var address = app.Urls.FirstOrDefault() ?? "http://localhost:5085";
            Process.Start(new ProcessStartInfo { FileName = address, UseShellExecute = true });
        }
    }
    catch { }
});

app.Run();

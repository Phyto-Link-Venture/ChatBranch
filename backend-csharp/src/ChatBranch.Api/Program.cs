using ChatBranch.Api.Auth;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- data: SQLite via EF Core, pointed at the same file layout as Django ----
var conn = builder.Configuration.GetConnectionString("Default")
           ?? "Data Source=chatbranch.sqlite3";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));
builder.Services.AddScoped<ScopingService>();

// ---- auth: bearer token handler + RBAC policies -----------------------------
builder.Services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o => o.AddChatBranchPolicies());

// ---- CORS: let the existing Next.js dashboard (localhost:3000) call in ------
const string CorsPolicy = "frontend";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ---- seed the demo tenant/users on boot (idempotent) ------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Seed.Run(db);
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

// ---- Phase 1 endpoints ------------------------------------------------------
// A cheap liveness probe the frontend already hits (kept open, like Django).
app.MapGet("/api/whatsapp/status", () => Results.Ok(new
{
    ok = true,
    service = "chatbranch-csharp",
    stack = "asp.net-core",
})).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSettingsEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the host in tests.
public partial class Program { }

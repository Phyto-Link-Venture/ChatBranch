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
builder.Services.AddScoped<ChatBranch.Data.Stores.ThreadStore>();
builder.Services.AddScoped<ChatBranch.Data.Ai.PersonaService>();
builder.Services.AddScoped<ChatBranch.Data.Ai.KnowledgeService>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AssistantService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ChatBranch.Data.Ai.ILlmClient, ChatBranch.Data.Ai.OpenAiLlmClient>();
builder.Services.AddSingleton<ChatBranch.Data.Channels.IWhatsAppCloudClient, ChatBranch.Data.Channels.WhatsAppCloudClient>();
builder.Services.AddScoped<ChatBranch.Data.Channels.OutboundSender>();
builder.Services.AddScoped<ChatBranch.Data.Channels.InboundProcessor>();
builder.Services.AddScoped<ChatBranch.Data.Flows.QueueService>();
builder.Services.AddScoped<ChatBranch.Data.Flows.FlowEngine>();
builder.Services.AddHostedService<ChatBranch.Api.Workers.OutboundQueueWorker>();
builder.Services.AddHostedService<ChatBranch.Api.Workers.FlowEngineWorker>();
builder.Services.AddScoped<ChatBranch.Data.Analytics.AnalyticsService>();
builder.Services.AddSingleton<ChatBranch.Data.Sidecars.IBaileysGateway, ChatBranch.Data.Sidecars.SandboxBaileysGateway>();
var dataDir = builder.Configuration["DataDir"]
              ?? Path.Combine(AppContext.BaseDirectory, "data");
builder.Services.AddSingleton(new ChatBranch.Data.Sidecars.VoiceAgentBridge(dataDir));

// ---- auth: bearer token handler + RBAC policies -----------------------------
builder.Services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o => o.AddChatBranchPolicies());

// ---- CORS: allowed origins come from config (Cors:Origins in appsettings /
// env), NOT hardcoded — the dashboard localhost pair is the built-in default.
const string CorsPolicy = "frontend";
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>();
if (corsOrigins is null || corsOrigins.Length == 0)
    corsOrigins = new[] { "http://localhost:3000", "http://127.0.0.1:3000" };
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

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
app.MapInboxEndpoints();
app.MapAiEndpoints();
app.MapFlowsEndpoints();
app.MapContactsEndpoints();
app.MapAnalyticsEndpoints();
app.MapIntegrationsEndpoints();
app.MapNotificationEndpoints();
app.MapAdminEndpoints();
app.MapWhatsAppSettingsEndpoints();
app.MapVoiceMessagingEndpoints();
app.MapApiV1Endpoints();
app.MapWebhookEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the host in tests.
public partial class Program { }

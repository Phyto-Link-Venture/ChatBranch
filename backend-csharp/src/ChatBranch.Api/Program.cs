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
builder.Services.AddScoped<ChatBranch.Data.Ai.Skills.SkillService>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Skills.SkillDrafter>();
builder.Services.AddScoped<ChatBranch.Data.Ai.SummaryService>();
builder.Services.AddScoped<ChatBranch.Data.Integrations.GoogleOAuth>();
builder.Services.AddScoped<ChatBranch.Data.Integrations.IntegrationResolver>();
builder.Services.AddScoped<ChatBranch.Data.Integrations.IntegrationActions>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ChatBranch.Data.Ai.ILlmClient, ChatBranch.Data.Ai.OpenAiLlmClient>();
builder.Services.AddSingleton<ChatBranch.Data.Channels.IWhatsAppCloudClient, ChatBranch.Data.Channels.WhatsAppCloudClient>();
builder.Services.AddScoped<ChatBranch.Data.Channels.OutboundSender>();
builder.Services.AddScoped<ChatBranch.Data.Channels.InboundProcessor>();
builder.Services.AddScoped<ChatBranch.Data.Flows.QueueService>();
builder.Services.AddScoped<ChatBranch.Data.Flows.QueueCallPlacer>();
builder.Services.AddScoped<ChatBranch.Data.Flows.FlowEngine>();
builder.Services.AddScoped<ChatBranch.Data.Flows.AutomationService>();
// AI ops: the monitor gate, human-like replies, follow-ups (+ their clock)
builder.Services.AddSingleton(TimeProvider.System);
// "typing…": one dispatcher over the per-channel indicators (WhatsApp Cloud +
// Baileys in LiveTypingIndicator, Telegram's sendChatAction)
builder.Services.AddScoped<ChatBranch.Data.Ai.LiveTypingIndicator>();
builder.Services.AddScoped<ChatBranch.Data.Ai.ITypingIndicator>(sp => new ChatBranch.Data.Ai.ChannelTypingIndicator(
    sp.GetRequiredService<ChatBranch.Data.Ai.LiveTypingIndicator>(),
    new ChatBranch.Data.Ai.TelegramTypingIndicator(sp.GetRequiredService<ChatBranch.Data.Channels.ITelegramClient>())));
builder.Services.AddScoped<ChatBranch.Data.Ai.AiComposer>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiGate>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiResponder>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiFollowUpService>();
builder.Services.AddHostedService<ChatBranch.Api.Workers.AiOpsWorker>();
builder.Services.AddHostedService<ChatBranch.Api.Workers.OutboundQueueWorker>();
builder.Services.AddHostedService<ChatBranch.Api.Workers.FlowEngineWorker>();
builder.Services.AddScoped<ChatBranch.Data.Analytics.AnalyticsService>();
// Baileys (unofficial WhatsApp Web) is a supported channel: drive the Node
// sidecar over HTTP (Setting baileys_url/baileys_secret → env → default).
// Unreachable gateway = clean per-call error, never a crash.
builder.Services.AddSingleton<ChatBranch.Data.Sidecars.IBaileysGateway, ChatBranch.Data.Sidecars.HttpBaileysGateway>();
// ONE data directory, shared with the voice agent worker (CHATBRANCH_DATA_DIR,
// default backend/data in a checkout). Written back so every config["DataDir"]
// reader (media, voices.json) agrees with the agent's files.
var dataDir = builder.Configuration["DataDir"] =
    ChatBranch.Data.Sidecars.VoiceAgentBridge.ResolveDataDir(builder.Configuration["DataDir"]);
builder.Services.AddSingleton(new ChatBranch.Data.Sidecars.VoiceAgentBridge(dataDir));
// Meta transports: Messenger/Instagram Send API, the template registry, message
// attachments + call recordings on disk, and inbound downloads off the webhook ack.
builder.Services.AddSingleton<ChatBranch.Data.Channels.IMetaMessagingClient, ChatBranch.Data.Channels.MetaMessagingClient>();
builder.Services.AddScoped<ChatBranch.Data.Channels.WhatsAppTemplateCatalog>();
builder.Services.AddSingleton(new ChatBranch.Data.Channels.MediaStore(dataDir));
builder.Services.AddSingleton(new ChatBranch.Data.Channels.CallRecordings(dataDir));
builder.Services.AddSingleton<ChatBranch.Data.Channels.MediaFetcher>();
builder.Services.AddSingleton<ChatBranch.Data.Sidecars.LiveKitClient>();
// Email (SMTP out via MailKit, IMAP pull), Telegram Bot API, and the channel-health
// prober (periodic HealthProbeWorker + "Check now" + the post-save re-check).
// Each external client is an interface so tests swap in fakes.
builder.Services.AddSingleton<ChatBranch.Data.Channels.IEmailTransport>(_ => new ChatBranch.Data.Channels.MailKitEmailTransport());
builder.Services.AddSingleton<ChatBranch.Data.Channels.IMailboxReader>(_ => new ChatBranch.Data.Channels.MailKitMailboxReader());
builder.Services.AddSingleton<ChatBranch.Data.Channels.ITelegramClient>(sp =>
    new ChatBranch.Data.Channels.TelegramClient(sp.GetRequiredService<IHttpClientFactory>()));
// The Bot API puts the token IN THE URL (…/bot<token>/method) — the factory's
// default request logging would write it to the console, so this client logs nothing.
builder.Services.AddHttpClient("telegram").RemoveAllLoggers();
builder.Services.AddSingleton<ChatBranch.Data.Channels.IGraphHealthProbe, ChatBranch.Data.Channels.GraphHealthProbe>();
builder.Services.AddScoped(sp => new ChatBranch.Data.Channels.EmailChannel(
    sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<ScopingService>(),
    sp.GetRequiredService<ChatBranch.Data.Stores.ThreadStore>(),
    sp.GetRequiredService<ChatBranch.Data.Channels.IEmailTransport>(),
    sp.GetRequiredService<ChatBranch.Data.Sidecars.VoiceAgentBridge>()));
builder.Services.AddScoped<ChatBranch.Data.Channels.TelegramChannel>();
builder.Services.AddScoped<ChatBranch.Data.Channels.EmailSyncService>();
builder.Services.AddScoped<ChatBranch.Data.Channels.ChannelHealthProber>();
builder.Services.AddSingleton(sp => new ChatBranch.Api.Workers.HealthRecheck(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<ChatBranch.Api.Workers.HealthRecheck>>(),
    enabled: ChatBranch.Api.Workers.HealthProbeWorker.Enabled));
builder.Services.AddHostedService<ChatBranch.Api.Workers.HealthProbeWorker>();

// ---- auth: bearer token handler + RBAC policies -----------------------------
builder.Services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o => o.AddChatBranchPolicies());

// ---- CORS: allowed origins come from config (Cors:Origins in appsettings /
// env), NOT hardcoded — the dashboard localhost pair is the built-in default.
const string CorsPolicy = "frontend";
const string WidgetCorsPolicy = "widget";
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>();
if (corsOrigins is null || corsOrigins.Length == 0)
    corsOrigins = new[] { "http://localhost:3000", "http://127.0.0.1:3000" };
builder.Services.AddCors(o =>
{
    o.AddPolicy(CorsPolicy, p => p
        .WithOrigins(corsOrigins)
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
    // The website widget runs on OTHER PEOPLE'S sites, so its public endpoints
    // must answer any origin — an allow-list would mean re-configuring the API
    // for every customer domain. Safe because these endpoints carry no
    // credentials (no cookies, no bearer) and expose nothing but one visitor's
    // own conversation, keyed by a server-minted 128-bit session id.
    o.AddPolicy(WidgetCorsPolicy, p => p
        .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// ---- seed the demo tenant/users on boot (idempotent) ------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Seed.Run(db);
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseMiddleware<ChatBranch.Api.Audit.RequestAuditMiddleware>(); // activity log (sees authz refusals too)
app.UseAuthorization();

// ---- Phase 1 endpoints ------------------------------------------------------
// (The open liveness probe GET /api/whatsapp/status lives in InboxEndpoints —
// mapping it here as well made every request an AmbiguousMatchException.)

app.MapAuthEndpoints();
app.MapSettingsEndpoints();
app.MapAssignmentsEndpoints();
app.MapInboxEndpoints();
app.MapInboxThreadsEndpoints();
app.MapSavedViewsEndpoints();
app.MapAiEndpoints();
app.MapFlowsEndpoints();
app.MapContactsEndpoints();
app.MapAnalyticsEndpoints();
app.MapIntegrationsEndpoints();
app.MapNotificationEndpoints();
app.MapAdminEndpoints();
app.MapWhatsAppSettingsEndpoints();
app.MapVoiceMessagingEndpoints();
app.MapMcpEndpoints();
app.MapTagsEndpoints();
app.MapPronunciationEndpoints();
app.MapWhatsAppThreadsEndpoints();
app.MapMessagingEndpoints();
app.MapCallsVoiceEndpoints();
app.MapCustomFieldsEndpoints();
app.MapTeamsEndpoints();
app.MapAutomationsEndpoints();
app.MapWidgetEndpoints();
app.MapWidgetAdminEndpoints();
app.MapHelpCenterEndpoints();
app.MapReportsEndpoints();
app.MapBindingsEndpoints();
app.MapAuditEndpoints();
app.MapDevelopersEndpoints();
app.MapMcpServerEndpoints();
app.MapAssistantEndpoints();
app.MapSearchEndpoints();
app.MapLiveEndpoints();
app.MapAiMonitorEndpoints();

// Inbound conversations route themselves to a team. The hook lives here so the
// data layer never has to reference the API assembly.
ChatBranch.Data.Channels.InboundProcessor.TeamRouter = (db, thread) =>
    ChatBranch.Api.Endpoints.TeamsEndpoints.AutoAssignAsync(db, thread).GetAwaiter().GetResult();
app.MapApiV1Endpoints();
app.MapWebhookEndpoints();
app.MapTelegramWebhookEndpoints();
app.MapMessagingTransportEndpoints();
app.MapMetaWebhookEndpoints();
app.MapWhatsAppMediaEndpoints();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the host in tests.
public partial class Program { }

using ChatBranch.Api.Auth;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- thread pool floor ----------------------------------------------------
// The send seams (OutboundSender, the queue drain, the flow engine, the AI gate)
// still block on async provider calls (.GetAwaiter().GetResult()). Each blocked
// request holds a pool thread while the work it waits on needs another one, and
// the pool starts at one thread per core and grows by ~1 thread per 0.5 s — so on
// a 2-vCPU host a burst of webhooks (or the test suite's parallel hosts) stalled
// requests past 100 s. A floor of THREADPOOL_MIN_THREADS (default 64) lets the
// pool hand out threads at once; it only creates them when work is waiting.
var poolFloor = int.TryParse(Environment.GetEnvironmentVariable("THREADPOOL_MIN_THREADS"), out var floor) && floor > 0
    ? floor : 64;
System.Threading.ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
System.Threading.ThreadPool.SetMinThreads(Math.Max(minWorkers, poolFloor), Math.Max(minIo, poolFloor));

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
// Calendar booking for the AI ("Connected app" skills): the Google Calendar
// client (shared with the flow actions) and the reserve → make-at-delivery service
builder.Services.AddScoped<ChatBranch.Data.Integrations.GoogleCalendarClient>();
builder.Services.AddScoped<ChatBranch.Data.Bookings.BookingService>();
// Documents / QR / Payments / Sheets app skills: files made for a reply (sent at
// delivery) and the integration-backed tools
builder.Services.AddScoped<ChatBranch.Data.Documents.DocumentService>();
builder.Services.AddScoped<ChatBranch.Data.Documents.DocumentTemplateAi>();
// the Documents app's custom layout (HTML templates): headless Chromium (DOCUMENT_HTML_CHROMIUM
// → google-chrome / chromium on the PATH; "off" disables — HTML-mode documents are then refused)
builder.Services.AddSingleton<ChatBranch.Data.Documents.IHtmlPdfConverter>(_ =>
    ChatBranch.Data.Documents.ChromiumHtmlPdfConverter.FromEnvironment());
builder.Services.AddScoped<ChatBranch.Data.Documents.DocumentHtmlAi>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Skills.ConnectedAppTools>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ChatBranch.Data.Ai.ILlmClient, ChatBranch.Data.Ai.OpenAiLlmClient>();
// WhatsApp: Meta's Cloud API, YCloud and Twilio behind one router (WhatsAppClientRouter)
builder.Services.AddWhatsAppProviders();
builder.Services.AddScoped<ChatBranch.Data.Channels.OutboundSender>();
builder.Services.AddScoped<ChatBranch.Data.Channels.InboundProcessor>();
builder.Services.AddScoped<ChatBranch.Data.Flows.QueueService>();
builder.Services.AddScoped<ChatBranch.Data.Flows.QueueCallPlacer>();
builder.Services.AddScoped<ChatBranch.Data.Flows.FlowEngine>();
builder.Services.AddScoped<ChatBranch.Data.Flows.AutomationService>();
// AI ops: the monitor gate, human-like replies, follow-ups (+ their clock)
builder.Services.AddSingleton(TimeProvider.System);
// "typing…": one dispatcher over the per-channel indicators (WhatsApp Cloud +
// Baileys in LiveTypingIndicator, Telegram's sendChatAction, Messenger /
// Instagram sender actions inside Meta's 24h window)
builder.Services.AddScoped<ChatBranch.Data.Ai.LiveTypingIndicator>();
builder.Services.AddScoped<ChatBranch.Data.Ai.ITypingIndicator>(sp => new ChatBranch.Data.Ai.ChannelTypingIndicator(
    sp.GetRequiredService<ChatBranch.Data.Ai.LiveTypingIndicator>(),
    new ChatBranch.Data.Ai.TelegramTypingIndicator(sp.GetRequiredService<ChatBranch.Data.Channels.ITelegramClient>()),
    new ChatBranch.Data.Ai.MetaTypingIndicator(sp.GetRequiredService<ChatBranch.Data.Channels.IMetaMessagingClient>(),
        sp.GetRequiredService<ChatBranch.Data.AppDbContext>(), sp.GetRequiredService<TimeProvider>())));
// AI voice replies (ElevenLabs text-to-speech) + customer voice notes as words (speech-to-text)
builder.Services.AddScoped<ChatBranch.Data.Ai.Voice.SpeechClient>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Voice.VoiceReplyService>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Voice.VoiceTranscriber>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiComposer>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiGate>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiResponder>();
builder.Services.AddScoped<ChatBranch.Data.Ai.AiFollowUpService>();
// the AI playground's per-user rate limit (in memory, per process)
builder.Services.AddSingleton<ChatBranch.Api.Endpoints.PlaygroundRateLimiter>();
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
// LINE, Viber, SMS (Twilio), WeChat, TikTok, Douyin, Zalo: one transport each + the send seam
builder.Services.AddMoreChannels();
builder.Services.AddScoped<ChatBranch.Data.Channels.EmailSyncService>();
builder.Services.AddScoped<ChatBranch.Data.Channels.ChannelHealthProber>();
builder.Services.AddSingleton(sp => new ChatBranch.Api.Workers.HealthRecheck(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<ChatBranch.Api.Workers.HealthRecheck>>(),
    enabled: ChatBranch.Api.Workers.HealthProbeWorker.Enabled));
builder.Services.AddHostedService<ChatBranch.Api.Workers.HealthProbeWorker>();
// AI learning: retrieval (BM25 + embeddings when a provider is configured),
// feedback → lessons, and the embedding indexer (EMBEDDING_WORKER=0 disables it)
builder.Services.AddHttpClient("embeddings").RemoveAllLoggers();
builder.Services.AddScoped<ChatBranch.Data.Ai.Learning.IEmbeddingClient, ChatBranch.Data.Ai.Learning.OpenAiEmbeddingClient>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Learning.RetrievalService>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Learning.LearningService>();
// the on-demand AI reviewer ("AI to monitor AI") — runs only when an admin clicks Review now
builder.Services.AddScoped<ChatBranch.Data.Ai.Review.AiReviewService>();
builder.Services.AddScoped<ChatBranch.Data.Ai.Learning.EmbeddingIndexer>();
builder.Services.AddHostedService<ChatBranch.Api.Workers.EmbeddingWorker>();
// background AI tasks: silent AI workers queued by the inbound path, run here
builder.Services.AddScoped(sp => new ChatBranch.Data.Ai.Tasks.AiTaskService(
    sp.GetRequiredService<ChatBranch.Data.AppDbContext>(), sp.GetRequiredService<ChatBranch.Data.ScopingService>(),
    sp.GetRequiredService<ChatBranch.Data.Ai.ILlmClient>(),
    sp.GetRequiredService<ChatBranch.Data.Integrations.IntegrationActions>(),
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ChatBranch.Data.Channels.MediaStore>()));
builder.Services.AddHostedService<ChatBranch.Api.Workers.AiTaskWorker>();
// Contacts: identity registry + WhatsApp lookups (IdentityCheckWorker), invite
// links, unknown-customer "who are you?", contact-book import (+ Google Contacts)
builder.Services.AddContactIdentity();

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
app.UseKeyScopes(); // a scoped API key narrows every capability check below
app.UseCompanyAdminScope(); // a company admin never holds the organization-level capabilities
app.UseEntitySelection(); // ?entity=a,b — the dashboard's multi-select entity filter
app.UseMediaStreamLinks(); // signed media links in message payloads (the session user)
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
app.MapContactIdentityEndpoints();
app.MapContactAccessEndpoints();
app.MapAnalyticsEndpoints();
app.MapAiBillingEndpoints();
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
app.MapBookingsEndpoints();
app.MapDocumentsEndpoints();
app.MapLearningEndpoints();
app.MapAiControlsEndpoints();
app.MapAiPlaygroundEndpoints();
app.MapAiReviewEndpoints();
app.MapAiTaskEndpoints();
app.MapClientErrorEndpoints();

// Inbound conversations route themselves to a team. The hook lives here so the
// data layer never has to reference the API assembly.
ChatBranch.Data.Channels.InboundProcessor.TeamRouter = (db, thread) =>
    ChatBranch.Api.Endpoints.TeamsEndpoints.AutoAssignAsync(db, thread).GetAwaiter().GetResult();
app.MapApiV1Endpoints();
app.MapWebhookEndpoints();
app.MapTelegramWebhookEndpoints();
app.MapMoreChannelWebhookEndpoints();
app.MapWhatsAppBspWebhookEndpoints();
app.MapMessagingTransportEndpoints();
app.MapMessageReactionEndpoints();
app.MapMetaWebhookEndpoints();
app.MapWhatsAppMediaEndpoints();
// the Test lab: scripted end-to-end scenarios + a fenced demo dataset (superadmin; TEST_LAB=0 = not mapped)
ChatBranch.Api.TestLab.TestLabEndpoints.MapTestLabEndpoints(app);

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the host in tests.
public partial class Program { }

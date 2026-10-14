using System.Globalization;
using System.Text.Json.Serialization;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Template;
using WhyLine.Web.Components;
using WhyLine.Web.Live;
using WhyLine.Web.Speech;
#if WHYLINE_FOUNDRY
using WhyLine.Agents.Foundry;
using WhyLine.Agents.Workflows;
using WhyLine.Web.Telemetry;
#endif

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ── Settings ──
var matchSettings = builder.Configuration.GetSection(MatchSettings.Section).Get<MatchSettings>() ?? new MatchSettings();
var agentOptions = builder.Configuration.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();
builder.Services.AddSingleton(matchSettings);
builder.Services.AddSingleton(agentOptions);

// ── Observability: OpenTelemetry → Application Insights (Foundry tracing) and/or OTLP ──
#if WHYLINE_FOUNDRY
var telemetry = TelemetrySetup.Configure(builder);
#else
var telemetry = "off (offline build)";
#endif

// ── Newsroom agents: template (offline) by default ──
builder.Services.AddSingleton<ClaimVerifier>();
builder.Services.AddSingleton<DirectorAgent>();
builder.Services.AddSingleton<IAnalystAgent, TemplateAnalystAgent>();
builder.Services.AddSingleton<INarratorAgent, TemplateNarratorAgent>();
builder.Services.AddSingleton<ILocalizationAgent, TemplateLocalizationAgent>();
builder.Services.AddSingleton<IRecapAgent, TemplateRecapAgent>();

if (string.Equals(agentOptions.Mode, "Foundry", StringComparison.OrdinalIgnoreCase))
{
#if WHYLINE_FOUNDRY
    builder.Services.AddFoundryNewsroomAgents(agentOptions.Foundry);
#else
    throw new InvalidOperationException("Agents:Mode is Foundry but the app was built with WhyLineFoundry=false.");
#endif
}

// ── Voices: Azure AI Speech neural voices (falls back to browser voices) ──
builder.Services.AddSingleton(builder.Configuration.GetSection(SpeechOptions.Section).Get<SpeechOptions>() ?? new SpeechOptions());
builder.Services.AddHttpClient("speech", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<SpeechService>();

// ── Live match + newsroom ──
builder.Services.AddSingleton<ViewerRegistry>();
builder.Services.AddSingleton<IProfileSource>(sp => sp.GetRequiredService<ViewerRegistry>());
builder.Services.AddSingleton<LiveMatch>();
builder.Services.AddSingleton<INewsroomSink>(sp => sp.GetRequiredService<LiveMatch>());
builder.Services.AddSingleton<NewsroomSteps>();
#if WHYLINE_FOUNDRY
if (string.Equals(agentOptions.Orchestration, "Workflow", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<INewsroom, NewsroomWorkflow>();   // Microsoft Agent Framework workflow graph
else
    builder.Services.AddSingleton<INewsroom, NewsroomPipeline>();
#else
builder.Services.AddSingleton<INewsroom, NewsroomPipeline>();       // offline build: same steps, in-process runner
#endif
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveMatch>());
builder.Services.AddHostedService<NewsroomWorker>();

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<MatchHub>("/hubs/match");
app.MapWhyLineApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok", agents = agentOptions.Mode }));

_ = app.Services.GetRequiredService<SpeechService>().EnsureReadyAsync(); // resolves the Speech region in the background

var auth = "n/a";
#if WHYLINE_FOUNDRY
auth = app.Services.GetService<FoundryAgentFactory>()?.AuthMode ?? "n/a";
#endif
app.Logger.LogInformation("WhyLine started. Agents: {Mode} ({Auth}). Orchestration: {Orchestration}. Telemetry: {Telemetry}", agentOptions.Mode,
    auth, app.Services.GetRequiredService<INewsroom>().Orchestration, telemetry);
app.Run();

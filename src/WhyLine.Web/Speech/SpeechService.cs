using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WhyLine.Agents.Abstractions;
using WhyLine.Agents.Pipeline;
using WhyLine.Agents.Speech;
using WhyLine.Core.Domain;

namespace WhyLine.Web.Speech;

public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>Use Azure AI Speech neural voices when a key is available; otherwise viewers fall back to browser voices.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Speech key. Leave empty to reuse Agents:Foundry:ApiKey: a Microsoft Foundry resource is a multi-service Azure AI
    /// resource, so its key also works for Speech.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>The resource's region, e.g. eastus2. Leave empty to detect it from the resource.</summary>
    public string? Region { get; set; }

    /// <summary>The resource's endpoint, e.g. https://my-resource.cognitiveservices.azure.com. Derived from the Foundry project endpoint when empty.</summary>
    public string? Endpoint { get; set; }
}

/// <summary>
/// Azure AI Speech text to speech over REST. Runs on the server: the Blazor viewer streams the audio to the browser, so
/// there is no public endpoint that turns arbitrary text into speech. Clips are cached, so viewers who share a card
/// (or replays) don't pay twice.
/// </summary>
public sealed class SpeechService(SpeechOptions options, AgentOptions agents, IHttpClientFactory http, ILogger<SpeechService> logger)
{
    private const int MaxCachedClips = 400;
    private static readonly Meter Meter = new("WhyLine.Speech", "1.0");
    private static readonly Counter<long> Characters = Meter.CreateCounter<long>("whyline.speech.characters", "{char}",
        "Characters sent to Azure AI Speech (billing driver).");

    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly SemaphoreSlim _init = new(1, 1);
    private string? _region;
    private bool _initialised;

    public bool Available => _region is not null;

    /// <summary>Shown in the UI and the startup log.</summary>
    public string Status { get; private set; } = "browser voices";

    private string? Key => FirstNonEmpty(options.Key, agents.Foundry.ApiKey);

    /// <summary>Resolves the region once. Safe to call repeatedly.</summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_initialised) return Available;
        await _init.WaitAsync(ct);
        try
        {
            if (_initialised) return Available;
            _initialised = true;
            if (!options.Enabled) return Fail("disabled (Speech:Enabled=false)");
            if (Key is null) return Fail("no key (set Speech:Key, or Agents:Foundry:ApiKey)");

            _region = FirstNonEmpty(options.Region) ?? await DetectRegionAsync(ct);
            if (_region is null) return Fail("could not detect the region (set Speech:Region)");

            Status = $"Azure AI Speech ({_region})";
            logger.LogInformation("Speech: {Status}", Status);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex.Message);
        }
        finally
        {
            _init.Release();
        }
    }

    /// <summary>MP3 audio for a card, or null when Azure AI Speech is unavailable (the caller falls back to browser voices).</summary>
    public async Task<byte[]?> SynthesizeAsync(string text, string language, Persona persona, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !await EnsureReadyAsync(ct)) return null;

        var voice = SpeechVoices.For(language, persona);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{voice.Name}|{voice.Rate}|{text}")));
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        using var span = NewsroomTelemetry.Source.StartActivity("speak whyline-voice");
        span?.SetTag("whyline.speech.voice", voice.Name);
        span?.SetTag("whyline.speech.characters", text.Length);
        var sw = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_region}.tts.speech.microsoft.com/cognitiveservices/v1")
        {
            Content = new StringContent(SpeechVoices.Ssml(text, voice), Encoding.UTF8, "application/ssml+xml")
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", Key);
        request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WhyLine", "1.0"));

        try
        {
            using var response = await http.CreateClient("speech").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Speech synthesis failed for {Voice}: {Status}", voice.Name, (int)response.StatusCode);
                span?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            var audio = await response.Content.ReadAsByteArrayAsync(ct);
            Characters.Add(text.Length, new KeyValuePair<string, object?>("voice", voice.Name));
            span?.SetTag("whyline.speech.ms", Math.Round(sw.Elapsed.TotalMilliseconds));
            if (_cache.Count >= MaxCachedClips) _cache.Clear();
            _cache[cacheKey] = audio;
            return audio;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Speech synthesis failed for {Voice}", voice.Name);
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The region is a claim in the token the resource issues for its key, so a Foundry key is enough on its own:
    /// POST {endpoint}/sts/v1.0/issueToken, then read "region" from the JWT.
    /// </summary>
    private async Task<string?> DetectRegionAsync(CancellationToken ct)
    {
        var endpoint = FirstNonEmpty(options.Endpoint) ?? DeriveEndpoint(agents.Foundry.ProjectEndpoint);
        if (endpoint is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/sts/v1.0/issueToken")
        {
            Content = new StringContent("")
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", Key);
        using var response = await http.CreateClient("speech").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Speech: issueToken returned {Status}; set Speech:Region", (int)response.StatusCode);
            return null;
        }

        var jwt = await response.Content.ReadAsStringAsync(ct);
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("region", out var region) ? region.GetString() : null;
    }

    /// <summary>https://res.services.ai.azure.com/api/projects/p → https://res.cognitiveservices.azure.com</summary>
    public static string? DeriveEndpoint(string? projectEndpoint) =>
        Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var uri)
            ? $"https://{uri.Host.Split('.')[0]}.cognitiveservices.azure.com"
            : null;

    private bool Fail(string reason)
    {
        _region = null;
        Status = $"browser voices: {reason}";
        logger.LogInformation("Speech: {Status}", Status);
        return false;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

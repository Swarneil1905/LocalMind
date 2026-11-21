using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalMind.AgentHub.Core;

// Model adapters. Every request to a model goes through one of these, inside the worker. Two real adapters:
// Ollama (local, NDJSON) and an OpenAI-compatible gateway (OpenRouter, SSE). Tests point them at scripted local HTTP endpoints.

/// <summary>What a model is known to do. Null means not known: a listing that is silent about a capability does not establish it.</summary>
public sealed record ModelCapabilities(bool? Text, bool? Streaming, bool? Tools, bool? Vision)
{
    public static ModelCapabilities Unknown => new(null, null, null, null);
    public string Describe() => string.Join(", ", new[] { ("text", Text), ("streaming", Streaming), ("tools", Tools), ("images", Vision) }
        .Select(c => c.Item2 switch { true => c.Item1, false => "no " + c.Item1, null => c.Item1 + " unknown" }));
}

public sealed record DiscoveredModel(string Id, ModelCapabilities Capabilities);

public sealed record ChatMessage(string Role, string Content, string? ToolCallId = null, IReadOnlyList<ToolCall>? ToolCalls = null);
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>One request. <see cref="OnlyProviders"/> pins a gateway to named providers with no fallback.</summary>
public sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, int MaxTokens, bool OfferReadFile, IReadOnlyList<string>? OnlyProviders);

public sealed record ChatResult(string Text, IReadOnlyList<ToolCall> ToolCalls, string? ReportedModel, string? ReportedProvider, int? InputTokens, int? OutputTokens, decimal? ReportedCost);

/// <summary>
/// A failed request. <see cref="BeforeGeneration"/> is true when the provider refused before generating anything (nothing can have been
/// charged and a retry is safe); false when output may already have been produced or accepted. <see cref="Partial"/> keeps what arrived.
/// </summary>
public sealed class ModelException(string code, string message, bool beforeGeneration, TimeSpan? retryAfter = null, string partial = "") : Exception(message)
{
    public string Code { get; } = code;
    public bool BeforeGeneration { get; } = beforeGeneration;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string Partial { get; } = partial;
}

public interface IModelClient
{
    string Provider { get; }
    Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(CancellationToken ct);
    Task<ChatResult> ChatAsync(ChatRequest request, Action<string> onToken, CancellationToken ct);
}

public static class ModelHttp
{
    /// <summary>Test seam: the handler every adapter uses, so a check can count requests per host.</summary>
    public static Func<HttpMessageHandler> Handler { get; set; } = () => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) };

    internal static HttpClient Client(TimeSpan timeout) => new(Handler(), disposeHandler: true) { Timeout = timeout };

    internal static ModelException Status(HttpResponseMessage r, string body)
    {
        TimeSpan? retry = r.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null) : null;
        string detail = Shorten(body);
        return (int)r.StatusCode switch
        {
            401 or 403 => new("auth", "The provider rejected this key or its permissions.", true),
            402 => new("payment", "The provider account has no credit for this request.", true),
            404 => new("model_unavailable", "The provider says this model is not available." + (detail.Length > 0 ? " " + detail : ""), true),
            429 => new("throttled", "The provider is throttling requests" + (retry is { } t ? $" and asks to retry after {Math.Ceiling(t.TotalSeconds)} s." : "."), true, retry),
            400 or 413 or 422 => new("unsupported", "The provider rejected this request or a feature it uses." + (detail.Length > 0 ? " " + detail : ""), true),
            _ => new("provider_error", $"The provider failed with HTTP {(int)r.StatusCode}.", false),
        };
    }

    internal static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;

    internal static ModelException Unreachable(Exception e) =>
        new("endpoint_unavailable", "The endpoint could not be reached: " + (e.InnerException?.Message ?? e.Message), true);
}

/// <summary>Ollama on this computer or the network. Lists with /api/tags and /api/show, chats with streamed NDJSON from /api/chat.</summary>
public sealed class OllamaClient(string endpoint) : IModelClient
{
    private readonly string baseUrl = endpoint.TrimEnd('/');
    public string Provider => "ollama";

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(CancellationToken ct)
    {
        using var http = ModelHttp.Client(TimeSpan.FromSeconds(15));
        HttpResponseMessage r;
        try { r = await http.GetAsync(baseUrl + "/api/tags", ct); }
        catch (HttpRequestException e) { throw ModelHttp.Unreachable(e); }
        using (r)
        {
            string body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) throw ModelHttp.Status(r, body);
            var list = new List<DiscoveredModel>();
            foreach (var m in JsonNode.Parse(body)?["models"]?.AsArray() ?? [])
            {
                string? id = m?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new(id, await Show(http, id, ct)));
            }
            return list;
        }
    }

    // Newer Ollama reports capabilities; older builds do not, and then they stay unknown.
    private async Task<ModelCapabilities> Show(HttpClient http, string id, CancellationToken ct)
    {
        try
        {
            using var r = await http.PostAsync(baseUrl + "/api/show", Json(new JsonObject { ["model"] = id }), ct);
            if (!r.IsSuccessStatusCode) return ModelCapabilities.Unknown with { Streaming = true };
            var caps = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["capabilities"]?.AsArray()?.Select(c => c?.GetValue<string>()).ToHashSet();
            if (caps == null) return ModelCapabilities.Unknown with { Streaming = true };
            return new(caps.Contains("completion"), true, caps.Contains("tools"), caps.Contains("vision"));
        }
        catch (HttpRequestException) { return ModelCapabilities.Unknown; }
        catch (JsonException) { return ModelCapabilities.Unknown; }
    }

    public async Task<ChatResult> ChatAsync(ChatRequest request, Action<string> onToken, CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCalls is { Count: > 0 } calls)
                o["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject { ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = JsonNode.Parse(c.Arguments) } })]);
            messages.Add(o);
        }
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages, ["stream"] = true, ["options"] = new JsonObject { ["num_predict"] = request.MaxTokens } };
        if (request.OfferReadFile) body["tools"] = new JsonArray(ReadFileTool());
        using var http = ModelHttp.Client(Timeout.InfiniteTimeSpan);
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/chat") { Content = Json(body) };
        HttpResponseMessage r;
        try { r = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw ModelHttp.Unreachable(e); }
        using (r)
        {
            if (!r.IsSuccessStatusCode) throw ModelHttp.Status(r, await r.Content.ReadAsStringAsync(ct));
            var text = new StringBuilder();
            var calls = new List<ToolCall>();
            string? model = null;
            int? inTok = null, outTok = null;
            bool done = false;
            try
            {
                using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length == 0) continue;
                    var e = JsonNode.Parse(line);
                    if (e?["error"]?.GetValue<string>() is { } err)
                        throw new ModelException(text.Length == 0 ? "model_error" : "dropped", "The model stopped with an error: " + ModelHttp.Shorten(err), text.Length == 0, null, text.ToString());
                    model ??= e?["model"]?.GetValue<string>();
                    if (e?["message"]?["content"]?.GetValue<string>() is { Length: > 0 } piece) { text.Append(piece); onToken(piece); }
                    foreach (var c in e?["message"]?["tool_calls"]?.AsArray() ?? [])
                        calls.Add(new("call" + calls.Count, c?["function"]?["name"]?.GetValue<string>() ?? "", c?["function"]?["arguments"]?.ToJsonString() ?? "{}"));
                    if (e?["done"]?.GetValue<bool>() == true) { inTok = e["prompt_eval_count"]?.GetValue<int>(); outTok = e["eval_count"]?.GetValue<int>(); done = true; break; }
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); } // a stop or a time limit, not a dropped stream
            catch (Exception ex) when (ex is IOException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                throw new ModelException(text.Length == 0 ? "provider_error" : "dropped", "The stream dropped" + (text.Length == 0 ? " before any output." : " after output began."), false, null, text.ToString());
            }
            catch (JsonException) { throw new ModelException("protocol", "The model sent an unreadable stream.", false, null, text.ToString()); }
            if (!done) throw new ModelException(text.Length == 0 ? "provider_error" : "dropped", text.Length == 0 ? "The stream ended before any output." : "The stream dropped after output began.", false, null, text.ToString());
            return new(text.ToString(), calls, model, model == null ? null : "ollama", inTok, outTok, null);
        }
    }

    internal static JsonObject ReadFileTool() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "read_file",
            ["description"] = "Read one of the files supplied with this handoff, by name.",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("name") },
        },
    };

    internal static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");
}

/// <summary>
/// An OpenAI-compatible gateway (OpenRouter first). The key comes from the vault for each call and never leaves this object.
/// Routing is pinned: the exact model, no model fallbacks, and optionally only named providers with fallbacks off.
/// </summary>
public sealed class GatewayClient(string endpoint, Func<string?> key) : IModelClient
{
    private readonly string baseUrl = endpoint.TrimEnd('/');
    public string Provider => "openrouter";

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body = null)
    {
        var m = new HttpRequestMessage(method, baseUrl + path);
        if (key() is { Length: > 0 } k) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
        else throw new ModelException("auth", "No key is stored for this connection.", true);
        m.Headers.Add("X-Title", "LocalMind Agent Hub");
        if (body != null) m.Content = OllamaClient.Json(body);
        return m;
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(CancellationToken ct)
    {
        using var http = ModelHttp.Client(TimeSpan.FromSeconds(20));
        HttpResponseMessage r;
        try { r = await http.SendAsync(Request(HttpMethod.Get, "/models"), ct); }
        catch (HttpRequestException e) { throw ModelHttp.Unreachable(e); }
        using (r)
        {
            string body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) throw ModelHttp.Status(r, body);
            var list = new List<DiscoveredModel>();
            foreach (var m in JsonNode.Parse(body)?["data"]?.AsArray() ?? [])
            {
                string? id = m?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var input = m?["architecture"]?["input_modalities"]?.AsArray()?.Select(x => x?.GetValue<string>()).ToHashSet();
                var output = m?["architecture"]?["output_modalities"]?.AsArray()?.Select(x => x?.GetValue<string>()).ToHashSet();
                var parameters = m?["supported_parameters"]?.AsArray()?.Select(x => x?.GetValue<string>()).ToHashSet();
                list.Add(new(id, new(output?.Contains("text") ?? input?.Contains("text"), true, parameters?.Contains("tools"), input?.Contains("image"))));
            }
            return list;
        }
    }

    public async Task<ChatResult> ChatAsync(ChatRequest request, Action<string> onToken, CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCallId != null) o["tool_call_id"] = m.ToolCallId;
            if (m.ToolCalls is { Count: > 0 } calls)
                o["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments } })]);
            messages.Add(o);
        }
        var provider = new JsonObject { ["allow_fallbacks"] = false };
        if (request.OnlyProviders is { Count: > 0 } only) provider["only"] = new JsonArray([.. only.Select(p => (JsonNode)p)]);
        var body = new JsonObject
        {
            ["model"] = request.Model, ["messages"] = messages, ["stream"] = true, ["max_tokens"] = request.MaxTokens,
            ["usage"] = new JsonObject { ["include"] = true }, ["provider"] = provider,
        };
        if (request.OfferReadFile) body["tools"] = new JsonArray(OllamaClient.ReadFileTool());
        using var http = ModelHttp.Client(Timeout.InfiniteTimeSpan);
        using var msg = Request(HttpMethod.Post, "/chat/completions", body);
        HttpResponseMessage r;
        try { r = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw ModelHttp.Unreachable(e); }
        using (r)
        {
            if (!r.IsSuccessStatusCode) throw ModelHttp.Status(r, await r.Content.ReadAsStringAsync(ct));
            var text = new StringBuilder();
            var calls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
            string? model = null, routed = null;
            int? inTok = null, outTok = null;
            decimal? cost = null;
            bool finished = false;
            try
            {
                using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(ct));
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.StartsWith("data:")) { data.Append(line[5..].TrimStart()); continue; }
                    if (line.Length != 0 || data.Length == 0) continue;
                    string payload = data.ToString();
                    data.Clear();
                    if (payload == "[DONE]") { finished = true; break; }
                    var e = JsonNode.Parse(payload);
                    if (e?["error"] is { } err)
                        throw new ModelException(text.Length == 0 ? "provider_error" : "dropped", "The provider stopped this response: " + ModelHttp.Shorten(err["message"]?.GetValue<string>() ?? err.ToJsonString()), false, null, text.ToString());
                    model ??= e?["model"]?.GetValue<string>();
                    routed ??= e?["provider"]?.GetValue<string>();
                    foreach (var choice in e?["choices"]?.AsArray() ?? [])
                    {
                        if (choice?["delta"]?["content"]?.GetValue<string>() is { Length: > 0 } piece) { text.Append(piece); onToken(piece); }
                        foreach (var tc in choice?["delta"]?["tool_calls"]?.AsArray() ?? [])
                        {
                            int index = tc?["index"]?.GetValue<int>() ?? 0;
                            if (!calls.TryGetValue(index, out var call)) calls[index] = call = (tc?["id"]?.GetValue<string>() ?? "call" + index, "", new StringBuilder());
                            if (tc?["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name) calls[index] = call = (call.Id, name, call.Args);
                            call.Args.Append(tc?["function"]?["arguments"]?.GetValue<string>() ?? "");
                        }
                        if (choice?["finish_reason"]?.GetValue<string>() is { Length: > 0 }) finished = true;
                    }
                    if (e?["usage"] is { } u)
                    {
                        inTok = u["prompt_tokens"]?.GetValue<int>();
                        outTok = u["completion_tokens"]?.GetValue<int>();
                        if (u["cost"] is JsonValue c && c.TryGetValue<decimal>(out var dec)) cost = dec;
                    }
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); } // a stop or a time limit, not a dropped stream
            catch (Exception ex) when (ex is IOException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                throw new ModelException(text.Length == 0 ? "provider_error" : "dropped", "The stream dropped" + (text.Length == 0 ? " before any output." : " after output began."), false, null, text.ToString());
            }
            catch (JsonException) { throw new ModelException("protocol", "The provider sent an unreadable stream.", false, null, text.ToString()); }
            if (!finished) throw new ModelException(text.Length == 0 ? "provider_error" : "dropped", text.Length == 0 ? "The stream ended before any output." : "The stream dropped after output began.", false, null, text.ToString());
            return new(text.ToString(), [.. calls.OrderBy(c => c.Key).Select(c => new ToolCall(c.Value.Id, c.Value.Name, c.Value.Args.ToString()))], model, routed, inTok, outTok, cost);
        }
    }
}

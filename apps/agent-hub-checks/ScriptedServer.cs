using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>One request as the scripted endpoint saw it.</summary>
internal sealed record ScriptedRequest(string Method, string Path, string Body, Dictionary<string, string> Headers)
{
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
    public string Model => Json?["model"]?.GetValue<string>() ?? "";
    /// <summary>The last user message: the handoff packet the model received.</summary>
    public string Packet => Json?["messages"]?.AsArray().LastOrDefault(m => m?["role"]?.GetValue<string>() == "user")?["content"]?.GetValue<string>() ?? "";
}

/// <summary>What the endpoint sends back. Chunks are written one at a time with a delay; DropAfter closes the socket after that many chunks.</summary>
internal sealed record ScriptedReply(int Status, string Body = "", string ContentType = "application/json", IReadOnlyList<string>? Chunks = null, int DelayMs = 0,
                                     int? DropAfter = null, Dictionary<string, string>? Headers = null);

/// <summary>
/// A local HTTP endpoint that answers like Ollama or an OpenAI-compatible gateway, from a script. The real adapters talk to it over TCP,
/// so the replay exercises the same code as a real model. Each response closes its connection.
/// </summary>
internal sealed class ScriptedServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    public string Url { get; }
    public int Port { get; }
    private int requests;
    public int Requests => requests;
    public ConcurrentQueue<ScriptedRequest> Seen { get; } = new();
    public Func<ScriptedRequest, ScriptedReply> Handle { get; set; } = _ => new(404, "{\"error\":\"not scripted\"}");

    public ScriptedServer()
    {
        listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Url = $"http://localhost:{Port}";
        _ = Loop();
    }

    private async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n"))
                {
                    if (await stream.ReadAsync(buffer) == 0) return;
                    head.Append((char)buffer[0]);
                }
                var lines = head.ToString().Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
                int length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                var body = new byte[length];
                int read = 0;
                while (read < length) { int n = await stream.ReadAsync(body.AsMemory(read)); if (n == 0) break; read += n; }
                var request = new ScriptedRequest(first[0], first[1], Encoding.UTF8.GetString(body, 0, read), headers);
                Interlocked.Increment(ref requests);
                Seen.Enqueue(request);
                var reply = Handle(request);
                var top = new StringBuilder($"HTTP/1.1 {reply.Status} {(reply.Status == 200 ? "OK" : "Scripted")}\r\nContent-Type: {reply.ContentType}\r\nConnection: close\r\n");
                foreach (var (k, v) in reply.Headers ?? []) top.Append($"{k}: {v}\r\n");
                if (reply.Chunks == null) top.Append($"Content-Length: {Encoding.UTF8.GetByteCount(reply.Body)}\r\n");
                top.Append("\r\n");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(top.ToString()));
                if (reply.Chunks == null) { await stream.WriteAsync(Encoding.UTF8.GetBytes(reply.Body)); return; }
                for (int i = 0; i < reply.Chunks.Count; i++)
                {
                    if (reply.DropAfter == i) { client.Client.LingerState = new LingerOption(true, 0); return; } // an abrupt reset
                    if (reply.DelayMs > 0) await Task.Delay(reply.DelayMs, stop.Token);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply.Chunks[i]));
                    await stream.FlushAsync();
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }

    // --- Ollama shapes ----------------------------------------------------------------------------------------
    public static ScriptedReply Tags(params string[] models) =>
        new(200, new JsonObject { ["models"] = new JsonArray([.. models.Select(m => (JsonNode)new JsonObject { ["name"] = m, ["model"] = m })]) }.ToJsonString());

    public static IReadOnlyList<string> OllamaText(string model, string text, int? inTok = 40, int? outTok = 25)
    {
        var parts = Split(text).Select(p => new JsonObject { ["model"] = model, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = p }, ["done"] = false }.ToJsonString() + "\n").ToList();
        var done = new JsonObject { ["model"] = model, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" }, ["done"] = true };
        if (inTok != null) done["prompt_eval_count"] = inTok;
        if (outTok != null) done["eval_count"] = outTok;
        parts.Add(done.ToJsonString() + "\n");
        return parts;
    }

    public static IReadOnlyList<string> OllamaToolCall(string model, string file) =>
        [new JsonObject { ["model"] = model, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(new JsonObject { ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = new JsonObject { ["name"] = file } } }) }, ["done"] = true, ["prompt_eval_count"] = 10, ["eval_count"] = 5 }.ToJsonString() + "\n"];

    // --- OpenAI-compatible gateway shapes -----------------------------------------------------------------------
    public static ScriptedReply ModelList(params (string Id, bool Tools)[] models) =>
        new(200, new JsonObject
        {
            ["data"] = new JsonArray([.. models.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id, ["architecture"] = new JsonObject { ["input_modalities"] = new JsonArray("text"), ["output_modalities"] = new JsonArray("text") },
                ["supported_parameters"] = m.Tools ? new JsonArray("max_tokens", "tools") : new JsonArray("max_tokens"),
            })]),
        }.ToJsonString());

    public static IReadOnlyList<string> Sse(string? model, string? provider, string text, int? inTok = 40, int? outTok = 25, double? cost = null)
    {
        JsonObject Chunk(JsonObject delta, string? finish)
        {
            var o = new JsonObject { ["id"] = "gen-1", ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }) };
            if (model != null) o["model"] = model;
            if (provider != null) o["provider"] = provider;
            return o;
        }
        var chunks = Split(text).Select(p => "data: " + Chunk(new JsonObject { ["content"] = p }, null).ToJsonString() + "\n\n").ToList();
        var last = Chunk(new JsonObject(), "stop");
        if (inTok != null) { var u = new JsonObject { ["prompt_tokens"] = inTok, ["completion_tokens"] = outTok }; if (cost != null) u["cost"] = cost; last["usage"] = u; }
        chunks.Add("data: " + last.ToJsonString() + "\n\n");
        chunks.Add("data: [DONE]\n\n");
        return chunks;
    }

    private static IEnumerable<string> Split(string text)
    {
        int size = Math.Max(1, text.Length / 3);
        for (int i = 0; i < text.Length; i += size) yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}

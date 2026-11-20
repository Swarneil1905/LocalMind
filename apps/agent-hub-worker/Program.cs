using System.Text;
using System.Text.Json;
using LocalMind.AgentHub.Core;

// Protocol: the first stdin line is {"profile","token"}; every later line is a WorkerRequest.
// Every stdout line is a WorkerResponse. Nothing else is written to stdout, and credentials are never logged.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (Console.In.ReadLine() is not { } first) return 2;
using var hello = JsonDocument.Parse(first);
string profile = hello.RootElement.GetProperty("profile").GetString() ?? "", token = hello.RootElement.GetProperty("token").GetString() ?? "";
if (profile.Length == 0 || token.Length == 0) return 2;

var host = new WorkerHost(profile, token, () => Guid.NewGuid().ToString("N")[..12]);
Send(new(Protocol.Version, "handshake", true, JsonSerializer.SerializeToElement(host.Info, Protocol.Json), null));

while (Console.In.ReadLine() is { } line)
{
    WorkerRequest? request = null;
    try { request = JsonSerializer.Deserialize<WorkerRequest>(line, Protocol.Json); } catch (JsonException) { }
    Send(request is null
        ? new(Protocol.Version, "", false, null, new("bad_request", "The request could not be read.", "Update LocalMind Agent Hub so the app and worker match."))
        : host.Handle(request));
}
host.Dispose();
return 0;

static void Send(WorkerResponse r)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(r, Protocol.Json));
    Console.Out.Flush();
}

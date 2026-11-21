using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalMind.AgentHub.Core;

public sealed partial class RunEngine
{
    private const string DocsPrompt = "Answer the question using only the numbered passages. Cite the passages you rely on like [1] or [2]. "
        + "If the passages do not answer the question, say that they do not.";

    /// <summary>References like [1] in a model's answer, checked once against the passages that were actually offered. Anything out of range or too large to be a number is reported, never counted.</summary>
    internal static (HashSet<int> Valid, List<string> Invalid) ParseRefs(string answer, int offered)
    {
        HashSet<int> valid = []; List<string> invalid = [];
        foreach (Match m in Regex.Matches(answer, @"\[(\d+)\]"))
        {
            string digits = m.Groups[1].Value.TrimStart('0');
            if (digits.Length is > 0 and < 6 && int.Parse(digits) is var n && n >= 1 && n <= offered) valid.Add(n);
            else if (!invalid.Contains(m.Value)) invalid.Add(m.Value);
        }
        return (valid, invalid);
    }

    /// <summary>
    /// Finds passages in this project's eligible documents and asks a model on this computer about them. With nothing relevant it sends nothing and says what is missing.
    /// Each citation records the document, its revision and the exact span, so it opens the same words later whatever happens to the document.
    /// </summary>
    public CommandResult AskDocuments(string workspace, string commandId, string projectId, string question, string role)
    {
        question = question?.Trim() ?? "";
        if (question.Length is 0 or > 1000) throw Bad("invalid_text", "A question needs 1 to 1000 characters.", "Shorten it.");
        if (!Team.Roles.Contains(role)) throw Bad("unknown_role", $"“{role}” is not a role.", "Choose the builder, reviewer or planner.");
        if (TaskStore.CommandStatus(workspace, commandId) is { } done) return done;
        List<Documents.Hit> hits; int eligible;
        (string Provider, string Endpoint, bool IsCloud, string? CredentialRef, string Label)? conn = null;
        string model = "";
        ProjectSettings settings;
        using (var db = Reader.Open(workspace))
        {
            hits = Documents.Retrieve(db, projectId, question);
            eligible = (int)TaskStore.Scalar<long>(db, "SELECT count(*) FROM documents WHERE project_id=$p AND removed_at IS NULL AND forgotten_at IS NULL", ("$p", projectId));
            settings = Team.Settings(db, projectId);
            if (hits.Count > 0)
            {
                var a = Reader.Rows(db, "SELECT connection_id, model_id FROM assignments WHERE project_id=$p AND role=$r", ("$p", projectId), ("$r", role)).FirstOrDefault()
                    ?? throw Bad("no_model", $"No model is assigned to the {role} for this project.", "Choose one in Team.");
                conn = Team.Connection(db, Reader.S(a[0]));
                model = Reader.S(a[1]);
                if (conn.Value.IsCloud) throw Bad("docs_cloud", "Asking about documents runs only on a model on this computer for now.", "Assign a local model to that role.");
            }
        }
        string answer, status, packet = "", destination = "", usedModel = "";
        HashSet<int> cited = []; List<string> invalid = [];
        if (hits.Count == 0)
        {
            status = "no_evidence";
            answer = eligible == 0 ? "This project has no documents yet, so there is nothing to cite. Nothing was sent to a model."
                : $"None of this project's {eligible} document(s) mention {string.Join(", ", Documents.Terms(question).Take(6))}. Nothing was sent to a model. Add a document that covers it, or rephrase the question.";
        }
        else
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hits.Count; i++) sb.Append($"[{i + 1}] {hits[i].Name}, revision {hits[i].Revision}\n{hits[i].Text}\n\n");
            sb.Append("Question: ").Append(question);
            packet = sb.ToString();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.MaxSeconds));
            ChatResult res;
            try { res = Client(conn!.Value.Provider, conn.Value.Endpoint, conn.Value.CredentialRef).ChatAsync(new(model, [new("system", DocsPrompt), new("user", packet)], Math.Max(settings.MaxOutputTokens, 400), false, null), _ => { }, cts.Token).GetAwaiter().GetResult(); }
            catch (ModelException e) { throw Bad(e.Code, e.Message, "Check the model in Connections, then ask again."); }
            catch (OperationCanceledException) { throw Bad("timeout", $"The model did not answer within {settings.MaxSeconds} seconds.", "Try again."); }
            answer = res.Text.Trim();
            usedModel = model;
            destination = $"{conn!.Value.Label}, " + (conn.Value.IsCloud ? "cloud" : "this computer");
            (cited, invalid) = ParseRefs(answer, hits.Count);
            status = cited.Count > 0 && invalid.Count == 0 ? "answered" : cited.Count > 0 ? "partial" : invalid.Count > 0 ? "invalid_citations" : "uncited";
        }
        return TaskStore.Run(workspace, commandId, "documents_asked", db =>
        {
            string id = newId();
            TaskStore.Exec(db, "INSERT INTO doc_answers(id,project_id,question,answer,status,model,destination,packet_text,created_at,invalid_refs) VALUES($i,$p,$q,$a,$s,$m,$d,$x,$at,$inv)",
                ("$i", id), ("$p", projectId), ("$q", question), ("$a", answer), ("$s", status), ("$m", usedModel.Length == 0 ? null : usedModel), ("$d", destination.Length == 0 ? null : destination), ("$x", packet), ("$at", Now), ("$inv", invalid.Count == 0 ? null : JsonSerializer.Serialize(invalid)));
            for (int i = 0; i < hits.Count; i++)
                TaskStore.Exec(db, "INSERT INTO doc_citations VALUES($a,$n,$d,$r,$s,$l,$c)", ("$a", id), ("$n", i + 1), ("$d", hits[i].DocId), ("$r", hits[i].Revision), ("$s", hits[i].Start), ("$l", hits[i].Length), ("$c", cited.Contains(i + 1) ? 1 : 0));
            return new(id, hits.Count, id, null, null, status);
        });
    }
}

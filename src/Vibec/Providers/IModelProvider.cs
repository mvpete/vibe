using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vibec.Providers;

public enum Role { User, Assistant }
public enum Effort { Low, Medium, High }

public sealed record ChatTurn(Role Role, string Text);

public sealed record JsonSchemaSpec(string Name, JsonObject Schema);

public sealed record ModelRequest(
    string System,
    IReadOnlyList<ChatTurn> Turns,
    JsonSchemaSpec? Schema = null,
    Effort Effort = Effort.Medium,
    int MaxOutputTokens = 16000);

public sealed record ModelReply(string Text, bool Refused, string? RefusalReason, int InputTokens, int OutputTokens);

public sealed record ProbeResult(bool Ok, string Detail);

public class ModelException(string message, Exception? inner = null) : Exception(message, inner);

/// Every model call in vibec goes through this. Providers: OpenAI-compatible APIs today
/// (OpenAI, Ollama, anything speaking /v1/chat/completions); CLIs and others plug in here.
public interface IModelProvider
{
    string Name { get; }
    string Model { get; }

    /// False for providers that can't enforce a JSON schema (e.g. CLIs). Structured.AskJsonAsync
    /// then asks for JSON in the prompt and validates/retries instead.
    bool SupportsJsonSchema { get; }

    Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken ct);

    /// Cheap reachability check for `vibec doctor`; shouldn't spend tokens if avoidable.
    Task<ProbeResult> ProbeAsync(CancellationToken ct);
}

public static class Structured
{
    /// Gets a JSON object back from any provider: native schema enforcement when available,
    /// otherwise prompt-for-JSON with one repair round.
    public static async Task<(JsonObject Json, ModelReply Reply)> AskJsonAsync(IModelProvider provider, ModelRequest request, CancellationToken ct)
    {
        var req = request;
        if (!provider.SupportsJsonSchema && request.Schema != null)
        {
            req = request with
            {
                System = request.System + "\n\nRespond with ONLY a JSON object matching this JSON Schema. No prose, no code fences.\n"
                         + request.Schema.Schema.ToJsonString(),
            };
        }

        var turns = req.Turns.ToList();
        for (int attempt = 0; ; attempt++)
        {
            var reply = await provider.CompleteAsync(req with { Turns = turns }, ct);
            if (reply.Refused) throw new ModelException($"the model refused: {reply.RefusalReason ?? "no reason given"}");

            try
            {
                if (JsonNode.Parse(StripFences(reply.Text)) is JsonObject obj) return (obj, reply);
                throw new JsonException("expected a JSON object");
            }
            catch (JsonException ex) when (attempt == 0)
            {
                turns.Add(new ChatTurn(Role.Assistant, reply.Text));
                turns.Add(new ChatTurn(Role.User, $"That wasn't valid JSON ({ex.Message}). Reply with only the JSON object."));
            }
            catch (JsonException ex)
            {
                throw new ModelException($"the model didn't return valid JSON: {ex.Message}");
            }
        }
    }

    static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        int firstNewline = t.IndexOf('\n');
        int lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline ? t[(firstNewline + 1)..lastFence] : t;
    }
}

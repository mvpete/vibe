#pragma warning disable OPENAI001 // reasoning effort is marked experimental in the SDK

using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;

namespace Vibec.Providers;

/// OpenAI's Chat Completions API, or anything compatible with it (Ollama, LM Studio, vLLM, ...)
/// when given a base URL.
public sealed class OpenAIProvider : IModelProvider
{
    readonly ChatClient _chat;
    readonly OpenAIModelClient _models;
    readonly bool _hasKey;
    readonly string? _baseUrl;
    bool _effortSupported = true; // non-reasoning models reject reasoning_effort; learned on first call

    public string Name { get; }
    public string Model { get; }
    public bool SupportsJsonSchema => true;

    public OpenAIProvider(string name, string model, string? baseUrl, string? apiKey)
    {
        Name = name;
        Model = model;
        _hasKey = !string.IsNullOrEmpty(apiKey) || baseUrl != null;
        _baseUrl = baseUrl;
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey);
        // codegen can think for minutes before the first byte; the SDK's default is 100s
        var options = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(15) };
        if (baseUrl != null) options.Endpoint = new Uri(baseUrl);
        _chat = new ChatClient(model, credential, options);
        _models = new OpenAIModelClient(credential, options);
    }

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        if (!_hasKey) throw new ModelException("no OpenAI API key: set OpenAI.ApiKey in appsettings.json (or OPENAI_API_KEY)");

        var messages = new List<ChatMessage> { new SystemChatMessage(request.System) };
        foreach (var turn in request.Turns)
            messages.Add(turn.Role == Role.User ? new UserChatMessage(turn.Text) : new AssistantChatMessage(turn.Text));

        while (true)
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = request.MaxOutputTokens };
            if (request.Schema != null)
            {
                options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    request.Schema.Name, BinaryData.FromString(request.Schema.Schema.ToJsonString()), jsonSchemaIsStrict: true);
            }
            if (_effortSupported)
            {
                options.ReasoningEffortLevel = request.Effort switch
                {
                    Effort.Low => ChatReasoningEffortLevel.Low,
                    Effort.High => ChatReasoningEffortLevel.High,
                    _ => ChatReasoningEffortLevel.Medium,
                };
            }

            try
            {
                ChatCompletion completion = (await _chat.CompleteChatAsync(messages, options, ct)).Value;
                var text = string.Concat(completion.Content.Select(p => p.Text));
                if (completion.FinishReason == ChatFinishReason.Length)
                    throw new ModelException($"the model ran out of output tokens ({request.MaxOutputTokens}) mid-answer");
                return new ModelReply(
                    text,
                    Refused: completion.Refusal != null || completion.FinishReason == ChatFinishReason.ContentFilter,
                    RefusalReason: completion.Refusal,
                    InputTokens: completion.Usage?.InputTokenCount ?? 0,
                    OutputTokens: completion.Usage?.OutputTokenCount ?? 0,
                    CachedInputTokens: completion.Usage?.InputTokenDetails?.CachedTokenCount ?? 0);
            }
            catch (ClientResultException ex) when (_effortSupported && ex.Status == 400 && ex.Message.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
            {
                _effortSupported = false; // retry once without it
            }
            catch (ClientResultException ex)
            {
                throw new ModelException($"{Name} returned HTTP {ex.Status}: {FirstLine(ex.Message)}", ex);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is AggregateException or TaskCanceledException or HttpRequestException or IOException)
            {
                // timeouts and dropped connections, after the SDK's own retries
                var inner = ex is AggregateException agg ? agg.InnerExceptions.LastOrDefault() ?? ex : ex;
                throw new ModelException($"couldn't reach {Name}: {FirstLine(inner.Message)}", ex);
            }
        }
    }

    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        if (!_hasKey) return new ProbeResult(false, "no OpenAI API key: set OpenAI.ApiKey in appsettings.json (or OPENAI_API_KEY)");
        try
        {
            await _models.GetModelAsync(Model, ct);
            return new ProbeResult(true, $"{Model} reachable");
        }
        catch (ClientResultException ex)
        {
            return new ProbeResult(false, ex.Status switch
            {
                401 => "API key rejected (401)",
                404 => $"model '{Model}' not found (404). try --model",
                _ => $"HTTP {ex.Status}: {FirstLine(ex.Message)}",
            });
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(false, $"can't connect: {ex.Message}");
        }
    }

    /// OpenAI's own prices for OpenAI's API; a local endpoint (Ollama, LM Studio) is free;
    /// any other OpenAI-compatible endpoint is unknown.
    public async Task<PriceQuote?> GetPriceAsync(CancellationToken ct)
    {
        if (_baseUrl == null || new Uri(_baseUrl).Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase))
            return await OpenAIPricing.LookupAsync(Model, ct);
        if (new Uri(_baseUrl).IsLoopback) return new PriceQuote(new Price(0, 0), "local, free");
        return null;
    }

    static string FirstLine(string s) => s.Split('\n')[0].Trim();
}

namespace Vibec.Providers;

public static class ProviderFactory
{
    public const string DefaultProvider = "openai";

    static readonly Dictionary<string, string> DefaultModels = new()
    {
        ["openai"] = "gpt-6-luna",
        ["ollama"] = "qwen2.5-coder",
    };

    static readonly string[] Planned = ["anthropic", "claude-cli", "copilot-cli"];

    /// Precedence: command-line flag, then env var, then appsettings.json, then the default.
    public static IModelProvider Create(string? name, string? model, string? baseUrl, Settings settings)
    {
        name ??= Environment.GetEnvironmentVariable("VIBEC_PROVIDER") ?? settings.Provider ?? DefaultProvider;
        model ??= Environment.GetEnvironmentVariable("VIBEC_MODEL") ?? settings.Model ?? DefaultModels.GetValueOrDefault(name);
        baseUrl ??= settings.BaseUrl;

        switch (name)
        {
            case "openai":
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? settings.OpenAI.ApiKey;
                return new OpenAIProvider(name, model!, baseUrl, string.IsNullOrWhiteSpace(key) ? null : key);
            case "ollama":
                // Ollama speaks the OpenAI API on its own port; no key needed
                return new OpenAIProvider(name, model!, baseUrl ?? "http://localhost:11434/v1", apiKey: "ollama");
            default:
                var hint = Planned.Contains(name) ? "it's planned but not built yet" : "never heard of it";
                throw new ModelException($"provider '{name}': {hint}. available: openai, ollama");
        }
    }
}

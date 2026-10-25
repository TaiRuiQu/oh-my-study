using OhMyStudy.Infrastructure.Grading;

internal static class ModelProfileChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var (model, expected) in new[]
        {
            ("gpt-4.1-2025-04-14", "gpt-4.1"),
            ("OPENAI/GPT-4.1-MINI", "gpt-4.1"),
            ("gpt-5.4", "gpt-5.4"),
            ("openai/gpt-5.4-mini:free", "gpt-5.4-small"),
            ("azure/gpt-5.4-nano", "gpt-5.4-small"),
            ("mini/gpt-5.4", "gpt-5.4"),
            ("claude/gpt-5.4", "gpt-5.4"),
            ("gpt/gemini-3-pro", "gemini-3"),
            ("gpt-router/custom-model", "generic"),
            ("gpt-6-astra", "gpt-6"),
            ("openai/gpt-6.1-sol", "gpt-6"),
            ("gpt-5.4.1", "gpt"),
            ("gpt-5.40", "gpt"),
            ("gpt-7", "gpt"),
            ("openai/o3-mini", "openai-reasoning"),
            ("o4-mini", "openai-reasoning"),
            ("claude-haiku-5-5", "claude-haiku-5.5"),
            ("anthropic/claude-haiku-5.5", "claude-haiku-5.5"),
            ("ANTHROPIC/CLAUDE-OPUS-5.5:thinking", "claude-opus-5.5"),
            ("us.anthropic.claude-opus-5-5-v1:0", "claude-opus-5.5"),
            ("claude-opus-5-5-20261023", "claude-opus-5.5"),
            ("claude-opus-5", "claude-opus-5"),
            ("claude-opus-5-20260101", "claude-opus-5"),
            ("claude-sonnet-5.5", "claude-sonnet-5.5"),
            ("claude-opus-5-6", "claude"),
            ("claude-opus-5-50", "claude"),
            ("claude-haiku-4-5", "claude"),
            ("claude-future", "claude"),
            ("google/gemini-2.5-pro", "gemini-2.5"),
            ("GEMINI_3_1_FLASH", "gemini-3"),
            ("google/gemini-3-pro-preview", "gemini-3"),
            ("gemini-2.50-flash", "gemini"),
            ("gemini-4-pro", "gemini"),
            ("synthetic-model", "generic"),
            ("notgpt-5.4", "generic"),
            ("myclaude-model", "generic"),
            ("provider-oo3", "generic")
        })
        {
            var profile = GradingModelProfile.Select(model);
            check(profile.Id == expected, $"{model}: selects {expected} without confusing versions or provider routes");
        }
    }
}

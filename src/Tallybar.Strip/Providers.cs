namespace Tallybar;

/// <summary>The set of providers Tallybar knows how to read. One place to register new ones.</summary>
internal static class Providers
{
    public static IReadOnlyList<IProvider> All { get; } =
    [
        new ClaudeProvider(),
        new CodexProvider(),
        new CopilotProvider(),
        new GeminiProvider(),
        new CursorProvider(),
    ];
}

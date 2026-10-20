using System.Reflection;

using CheapLoc;

namespace Dalamud;

/// <summary>
/// Localizes Dalamud-owned UI strings without inspecting the call stack.
/// </summary>
internal static class CoreLocalization
{
    private static readonly Assembly Assembly = typeof(CoreLocalization).Assembly;

    /// <summary>Returns the current translation or fallback for a Dalamud-owned key.</summary>
    /// <param name="key">The translation key.</param>
    /// <param name="fallback">The original text.</param>
    /// <returns>The current localized text.</returns>
    // Keep two string arguments so CheapLoc's exporter can read literal keys and fallbacks.
    public static string Localize(string key, string fallback) => Loc.Localize(key, fallback, Assembly);
}

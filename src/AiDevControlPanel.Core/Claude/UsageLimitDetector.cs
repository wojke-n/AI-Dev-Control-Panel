using System.Text.RegularExpressions;

namespace AiDevControlPanel.Core.Claude;

public static partial class UsageLimitDetector
{
    [GeneratedRegex(@"(usage|session|weekly)\s+limit|hit your limit|limit reached|out of extra usage", RegexOptions.IgnoreCase)]
    private static partial Regex LimitText();

    /// <summary>Older CLI versions append the reset time as "|&lt;epoch seconds&gt;".</summary>
    [GeneratedRegex(@"\|(\d{10})\b")]
    private static partial Regex ResetEpoch();

    public static bool IsLimitMessage(string? text) => text is not null && LimitText().IsMatch(text);

    public static DateTimeOffset? ParseResetTime(string? text)
    {
        var match = text is null ? null : ResetEpoch().Match(text);
        return match is { Success: true } ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(match.Groups[1].Value)) : null;
    }
}

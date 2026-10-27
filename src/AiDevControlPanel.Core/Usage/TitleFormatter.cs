using System.Globalization;

namespace AiDevControlPanel.Core.Usage;

public static class TitleFormatter
{
    public const string AppName = "AI-Dev-Control-Panel";

    private static readonly string[] DayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    /// <summary>e.g. "AI-Dev-Control-Panel - Usage: 7% (14:56) | 23% (Mo, 07:38)".</summary>
    public static string Format(UsageSnapshot? usage, TimeZoneInfo zone)
    {
        if (usage is null)
        {
            return $"{AppName} - Usage: n/a";
        }

        var sessionReset = TimeZoneInfo.ConvertTime(usage.Session.ResetsAt, zone);
        var weeklyReset = TimeZoneInfo.ConvertTime(usage.Weekly.ResetsAt, zone);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{AppName} - Usage: {Percent(usage.Session.Percent)}% ({sessionReset:HH:mm}) | " +
            $"{Percent(usage.Weekly.Percent)}% ({DayNames[(int)weeklyReset.DayOfWeek]}, {weeklyReset:HH:mm})");
    }

    private static int Percent(double value) => (int)Math.Round(Math.Max(0, value), MidpointRounding.AwayFromZero);
}

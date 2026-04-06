namespace TsukiAI.Core.Services;

/// <summary>Time-of-day behavior tags. No LLM — just tags for prompt.</summary>
public static class TimeOfDayService
{
    /// <summary>morning | afternoon | evening | late_night</summary>
    public static string GetTimeOfDayTag()
    {
        var hour = DateTime.Now.Hour;
        if (hour >= 6 && hour < 12) return "morning";
        if (hour >= 12 && hour < 17) return "afternoon";
        if (hour >= 17 && hour < 21) return "evening";
        return "late_night";
    }

    /// <summary>Short instruction for prompt: gentle / neutral / caring / concerned.</summary>
    public static string GetToneHint()
    {
        return GetTimeOfDayTag() switch
        {
            "morning" => "gentle",
            "afternoon" => "neutral",
            "evening" => "caring",
            "late_night" => "concerned",
            _ => "neutral"
        };
    }
}

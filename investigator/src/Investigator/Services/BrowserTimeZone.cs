namespace Investigator.Services;

/// <summary>
/// Scoped per Blazor circuit. Holds the browser's detected timezone,
/// falling back to Newfoundland (America/St_Johns) if unrecognised.
/// </summary>
public class BrowserTimeZone
{
    private static readonly TimeZoneInfo s_fallback =
        TimeZoneInfo.FindSystemTimeZoneById("America/St_Johns");

    public TimeZoneInfo TimeZone { get; private set; } = s_fallback;

    public string IanaId { get; private set; } = "America/St_Johns";

    public void Initialize(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
            return;

        try
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            IanaId = ianaId;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
    }

    public DateTimeOffset ToLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, TimeZone);
}

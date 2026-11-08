namespace NetGuard.App;

/// <summary>Colours shared by the window banner, profile cards and tray icon.</summary>
public static class Theme
{
    public const string NormalInternet = "#2E7D6B";
    public const string ServiceDown = "#7A2E2E";

    public static string ProfileColor(string? profile) => profile?.ToLowerInvariant() switch
    {
        null => NormalInternet,
        "offline" => "#3D4B5C",
        "wow" => "#1F4E79",
        "maintenance" => "#8A5A12",
        _ => "#5B3F8C",
    };
}

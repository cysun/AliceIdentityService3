namespace AliceIdentityService.Models;

/// <summary>Release metadata shared by the web and console hosts.</summary>
public sealed record ApplicationInfo(string Name, string Version, int ImplementationStep)
{
    public static ApplicationInfo Current { get; } = new("Alice Identity Service", "3.0.0-preview.8", 8);
}

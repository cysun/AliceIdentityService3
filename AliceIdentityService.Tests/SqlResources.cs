using AliceIdentityService.Data;
namespace AliceIdentityService.Tests;

internal static class SqlResources
{
    internal static string Read(string fileName, string folder = "Fixtures")
    {
        using var stream = typeof(SqlResources).Assembly.GetManifestResourceStream($"AliceIdentityService.Tests.{folder}.{fileName}")
            ?? throw new InvalidOperationException($"Missing embedded persistence resource: {fileName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

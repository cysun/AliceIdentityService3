using System.Text.Json;
using AliceIdentityService.Core;

if (args.Length == 0 || args is ["info"])
{
    Console.WriteLine(JsonSerializer.Serialize(ApplicationInfo.Current, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

Console.Error.WriteLine("Usage: dotnet run --project src/AliceIdentityService.Console -- info");
Console.Error.WriteLine("Database initialization and administrator setup start in Step 2.");
return 1;

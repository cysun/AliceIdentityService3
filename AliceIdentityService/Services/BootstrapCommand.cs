using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AliceIdentityService.Services;

public static class BootstrapCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        try
        {
            if (args.Length == 0 || args.SequenceEqual(["--help"]))
            {
                Console.WriteLine("bootstrap --email <email> --first-name <name> --last-name <name> [--screen-name <name>] [--password-stdin]");
                Console.WriteLine("Uses the application's appsettings.json and environment variables. Password input is hidden by default.");
                return 0;
            }
            var values = new Dictionary<string, string>();
            var stdin = false;
            for (var i = 0; i < args.Length; i++)
            {
                var name = args[i];
                if (name == "--password-stdin" && !stdin) { stdin = true; continue; }
                if (name is not ("--email" or "--first-name" or "--last-name" or "--screen-name"))
                    throw new ArgumentException("Unknown or duplicate option. Passwords must use the hidden prompt or --password-stdin.");
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(name, args[i]))
                    throw new ArgumentException("An option is missing its value or was supplied twice.");
            }
            if (new[] { "--email", "--first-name", "--last-name" }.Any(name => !values.ContainsKey(name)))
                throw new ArgumentException("Bootstrap requires --email, --first-name and --last-name.");

            await using var scope = services.CreateAsyncScope();
            var request = new AdministratorRequest(values["--email"], values["--first-name"], values["--last-name"], values.GetValueOrDefault("--screen-name"));
            var result = await scope.ServiceProvider.GetRequiredService<BootstrapService>()
                .RunAsync(request, () => ReadPasswordAsync(stdin));
            Console.WriteLine(result.AdministratorCreated ? "Confirmed administrator created with the ais_admin claim." : "Administrator already exists; account details and password were left unchanged.");
            Console.WriteLine($"Standard scopes added: {result.ScopesCreated}.");
            return 0;
        }
        catch (Exception error) when (error is NpgsqlException or DbUpdateException)
        {
            Console.Error.WriteLine("Bootstrap failed; database changes were rolled back. Check the connection and initialize a fresh database with Scripts/CreateSchema.sql before retrying.");
            return 1;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<string> ReadPasswordAsync(bool stdin)
    {
        if (stdin) return await Console.In.ReadLineAsync() ?? throw new ArgumentException("No password was supplied on standard input.");
        if (Console.IsInputRedirected) throw new ArgumentException("Use --password-stdin when input is redirected.");
        Console.Write("Administrator password: ");
        var password = ReadHidden();
        Console.Write("Confirm password: ");
        if (ReadHidden() != password) throw new ArgumentException("Passwords do not match.");
        return password;
    }

    private static string ReadHidden()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

/// <summary>Local administrator commands run before the HTTP server starts.</summary>
public static partial class ServerCommandLine
{
    public static async Task<bool> TryRunAsync(string[] args, IServiceProvider services)
    {
        if (await TryPackageMsiAsync(args, services))
        {
            return true;
        }
        Store store = services.GetRequiredService<Store>();
        if (Array.IndexOf(args, "--bootstrap-admin") >= 0)
        {
            BootstrapAdministrator(store);
            return true;
        }

        int enrollmentIndex = Array.IndexOf(args, "--create-enrollment");
        if (enrollmentIndex >= 0)
        {
            string name = enrollmentIndex + 1 < args.Length ? args[enrollmentIndex + 1] : "Installation";
            (string Id, string Token) key = store.CreateToken(name, "default", 1, 24);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id = key.Id,
                token = key.Token
            }, Wire.Json));
            return true;
        }

        int policyIndex = Array.IndexOf(args, "--native-policy");
        if (policyIndex >= 0)
        {
            if (policyIndex + 1 >= args.Length)
            {
                throw new ArgumentException("Missing enrollment key.");
            }

            Console.Write(services.GetRequiredService<NativeAgentHub>().Policy(args[policyIndex + 1]));
            return true;
        }

        int resetIndex = Array.IndexOf(args, "--reset-two-factor");
        if (resetIndex >= 0)
        {
            string name = resetIndex + 1 < args.Length ? args[resetIndex + 1] : throw new ArgumentException("Supply the account username.");
            UserInfo user = store.Users().SingleOrDefault(user => string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Account not found.");
            store.ResetTwoFactor(user.Id);
            store.Audit("Local administrator", "Two-factor authentication reset", user.Name);
            Console.WriteLine("Two-factor authentication reset; existing account sessions revoked.");
            return true;
        }

        return false;
    }

    private static void BootstrapAdministrator(Store store)
    {
        if (store.HasUsers)
        {
            Console.WriteLine("{\"created\":false}");
            return;
        }

        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        UserInfo admin = store.Setup(File.ReadAllText(store.BootstrapPath), "admin", password);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            created = true,
            username = admin.Name,
            password
        }, Wire.Json));
    }
}

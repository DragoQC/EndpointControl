namespace MeshAll.Server.Services;

public static partial class ServerCommandLine
{
    private static async Task<bool> TryPackageMsiAsync(string[] args, IServiceProvider services)
    {
        int keyIndex = Array.IndexOf(args, "--agent-msi");
        int executableIndex = Array.IndexOf(args, "--agent-msi-from");
        if (keyIndex < 0 && executableIndex < 0)
        {
            return false;
        }
        if (keyIndex >= 0 && executableIndex >= 0)
        {
            throw new ArgumentException("Choose either --agent-msi or --agent-msi-from.");
        }

        int inputIndex = keyIndex >= 0 ? keyIndex : executableIndex;
        int outputIndex = Array.IndexOf(args, "--output");
        if (inputIndex + 1 >= args.Length || outputIndex < 0 || outputIndex + 1 >= args.Length)
        {
            throw new ArgumentException("Supply --agent-msi ENROLLMENT_KEY or --agent-msi-from ENROLLMENT_READY_EXE, and --output PACKAGE.msi.");
        }
        string path = Path.GetFullPath(args[outputIndex + 1]);
        if (File.Exists(path))
        {
            throw new IOException("The MSI output already exists. Choose a new output path.");
        }

        // Packaging an already-personalized EXE does not open/create controller state.
        byte[] executable = keyIndex >= 0
            ? services.GetRequiredService<NativeAgentHub>().Executable(args[inputIndex + 1], NativeMsiPackage.Platform)
            : await File.ReadAllBytesAsync(args[inputIndex + 1]);
        NativeMsiBuilder packages = services.GetRequiredService<NativeMsiBuilder>();
        byte[] package = await packages.BuildAsync(executable, NativeAgentExecutable.LicenseNotices());
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using FileStream output = new(path, options);
        await output.WriteAsync(package);
        Console.WriteLine($"Created native Windows x64 MSI: {path}");
        return true;
    }
}

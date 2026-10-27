using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

/// <summary>Declarative Windows Installer authoring for the enrollment-ready native agent.</summary>
public static class NativeMsiPackage
{
    public const string Platform = "win-x64";
    public const string AgentFileName = "MeshAllAgent.exe";
    public const string NoticesFileName = "EndpointControl-Agent-Notices.txt";

    // Installer identities remain stable across releases and display-name changes.
    public const string UpgradeCode = "1D438ED1-ECCB-439A-9AF2-B438F3D466A4";
    private const string ComponentCode = "E8CB1052-2913-4306-A1A9-120A7A4E1E7A";
    private const string RegistryKey = @"SOFTWARE\MeshAll\Agent";
    private static readonly XNamespace Wix = "http://schemas.microsoft.com/wix/2006/wi";

    public static XDocument Definition(byte[] executable, string version)
    {
        ValidateVersion(version);
        string instance = ServerIdentity(executable);
        string payloadHash = Convert.ToHexStringLower(SHA256.HashData(executable));
        byte[] productHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{UpgradeCode}/{version}/{payloadHash}"));
        string productCode = new Guid(productHash.AsSpan(0, 16)).ToString("D").ToUpperInvariant();

        return new XDocument(new XElement(Wix + "Wix",
            new XElement(Wix + "Product",
                new XAttribute("Id", productCode),
                new XAttribute("Name", ProductBrand.AgentName),
                new XAttribute("Language", "1033"),
                new XAttribute("Version", version),
                new XAttribute("Manufacturer", ProductBrand.Name),
                new XAttribute("UpgradeCode", UpgradeCode),
                new XElement(Wix + "Package",
                    new XAttribute("InstallerVersion", "500"),
                    new XAttribute("Compressed", "yes"),
                    new XAttribute("InstallScope", "perMachine")),
                new XElement(Wix + "MajorUpgrade",
                    new XAttribute("Schedule", "afterInstallInitialize"),
                    new XAttribute("AllowSameVersionUpgrades", "yes"),
                    new XAttribute("DowngradeErrorMessage", "A newer EndpointControl Agent MSI is already installed.")),
                new XElement(Wix + "Media", new XAttribute("Id", "1"),
                    new XAttribute("Cabinet", "agent.cab"), new XAttribute("EmbedCab", "yes")),
                Property("ARPNOMODIFY", "1"),
                Property("ARPPRODUCTICON", "EndpointControlIcon"),
                new XElement(Wix + "Icon", new XAttribute("Id", "EndpointControlIcon"), new XAttribute("SourceFile", "EndpointControl.ico")),
                Property("SERVERINSTANCE", instance),
                RegistrySearch("EXISTINGINSTANCE", RegistryKey, "ServerId"),
                RegistrySearch("EXISTINGAGENTSERVICE", @"SYSTEM\CurrentControlSet\Services\MeshAllAgent", "ImagePath"),
                Condition("VersionNT64", "This package requires 64-bit Windows."),
                Condition("Installed OR NOT EXISTINGINSTANCE OR EXISTINGINSTANCE = SERVERINSTANCE",
                    "This computer is managed by another EndpointControl instance. Use its existing deployment package."),
                Condition("Installed OR WIX_UPGRADE_DETECTED OR WIX_SAME_VERSION_UPGRADE_DETECTED OR NOT EXISTINGAGENTSERVICE",
                    "An agent installed outside Windows Installer already exists. Migrate it before assigning this MSI."),
                new XElement(Wix + "Directory", new XAttribute("Id", "TARGETDIR"), new XAttribute("Name", "SourceDir"),
                    new XElement(Wix + "Directory", new XAttribute("Id", "ProgramFiles64Folder"),
                        new XElement(Wix + "Directory", new XAttribute("Id", "CompanyFolder"), new XAttribute("Name", "MeshAll"),
                            new XElement(Wix + "Directory", new XAttribute("Id", "INSTALLDIR"), new XAttribute("Name", "MeshAllAgent"),
                                AgentComponent(instance))))),
                new XElement(Wix + "Feature", new XAttribute("Id", "Agent"), new XAttribute("Level", "1"),
                    new XAttribute("Title", ProductBrand.AgentName),
                    new XElement(Wix + "ComponentRef", new XAttribute("Id", "AgentService"))))));
    }

    private static XElement AgentComponent(string instance) => new(Wix + "Component",
        new XAttribute("Id", "AgentService"), new XAttribute("Guid", ComponentCode), new XAttribute("Win64", "yes"),
        new XElement(Wix + "File", new XAttribute("Id", "AgentExecutable"),
            new XAttribute("Source", AgentFileName), new XAttribute("KeyPath", "yes")),
        new XElement(Wix + "File", new XAttribute("Id", "AgentNotices"), new XAttribute("Source", NoticesFileName)),
        new XElement(Wix + "RegistryValue", new XAttribute("Id", "InstanceRegistry"),
            new XAttribute("Root", "HKLM"), new XAttribute("Key", RegistryKey),
            new XAttribute("Name", "ServerId"), new XAttribute("Type", "string"), new XAttribute("Value", instance)),
        new XElement(Wix + "ServiceInstall", new XAttribute("Id", "InstallAgentService"),
            new XAttribute("Name", "MeshAllAgent"), new XAttribute("DisplayName", ProductBrand.AgentName),
            new XAttribute("Description", "EndpointControl remote-management agent"), new XAttribute("Type", "ownProcess"),
            new XAttribute("Start", "auto"), new XAttribute("ErrorControl", "normal"), new XAttribute("Vital", "yes")),
        new XElement(Wix + "ServiceControl", new XAttribute("Id", "ControlAgentService"),
            new XAttribute("Name", "MeshAllAgent"), new XAttribute("Start", "install"),
            new XAttribute("Stop", "both"), new XAttribute("Remove", "uninstall"), new XAttribute("Wait", "yes")),
        // Native runtime state is not an MSI payload. Never wildcard-delete its database/identity.
        new XElement(Wix + "RemoveFolder", new XAttribute("Id", "RemoveEmptyAgentFolder"), new XAttribute("On", "uninstall")));

    private static XElement Property(string name, string value) => new(Wix + "Property",
        new XAttribute("Id", name), new XAttribute("Value", value));

    private static XElement RegistrySearch(string property, string key, string name) => new(Wix + "Property",
        new XAttribute("Id", property),
        new XElement(Wix + "RegistrySearch", new XAttribute("Id", property + "Search"),
            new XAttribute("Root", "HKLM"), new XAttribute("Key", key), new XAttribute("Name", name),
            new XAttribute("Type", "raw"), new XAttribute("Win64", "yes")));

    private static XElement Condition(string expression, string message) => new(Wix + "Condition",
        new XAttribute("Message", message), expression);

    public static string ServerIdentity(byte[] executable)
    {
        const string invalid = "Use an enrollment-ready Windows x64 native agent executable.";
        if (executable.Length < 256 || executable.Length > 64 * 1024 * 1024
            || executable[0] != 'M' || executable[1] != 'Z'
            || !executable.AsSpan(executable.Length - 16).SequenceEqual(NativeAgentExecutable.PolicyMarker))
        {
            throw new InvalidOperationException(invalid);
        }

        int pe = BinaryPrimitives.ReadInt32LittleEndian(executable.AsSpan(0x3c));
        if (pe < 64 || pe > executable.Length - 6
            || !executable.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(executable.AsSpan(pe + 4)) != 0x8664)
        {
            throw new InvalidOperationException(invalid);
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(executable.AsSpan(executable.Length - 20));
        if (length is < 1 or > 65536 || length > executable.Length - 20)
        {
            throw new InvalidOperationException(invalid);
        }
        string policy = Encoding.UTF8.GetString(executable, executable.Length - 20 - length, length);
        string Value(string name)
        {
            string prefix = name + "=";
            string[] matches = policy.Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 ? matches[0][prefix.Length..].Trim() : "";
        }
        string instance = Value("ServerID");
        string mesh = Value("MeshID");
        if (instance.Length != 96 || !instance.All(char.IsAsciiHexDigit)
            || mesh.Length != 98 || !mesh.StartsWith("0x", StringComparison.Ordinal) || !mesh[2..].All(char.IsAsciiHexDigit)
            || Value("meshServiceName") != "MeshAllAgent" || Value("fileName") != "MeshAllAgent"
            || !Uri.TryCreate(Value("MeshServer"), UriKind.Absolute, out Uri? server) || server.Scheme is not ("wss" or "ws"))
        {
            throw new InvalidOperationException(invalid);
        }
        return instance.ToLowerInvariant();
    }

    private static void ValidateVersion(string version)
    {
        string[] fields = version.Split('.');
        if (fields.Length != 3 || !fields.All(field => field.Length > 0 && field.All(char.IsAsciiDigit))
            || !int.TryParse(fields[0], out int major) || major > 255
            || !int.TryParse(fields[1], out int minor) || minor > 255
            || !int.TryParse(fields[2], out int build) || build > 65535)
        {
            throw new InvalidOperationException("MSI versions require three numeric fields: 0–255.0–255.0–65535.");
        }
    }
}

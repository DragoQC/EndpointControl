using System.Buffers.Binary;
using System.Xml.Linq;
using MeshAll.Server.Services;
using Xunit;

namespace MeshAll.Tests;

public sealed class NativeMsiPackageTests
{
    private static readonly XNamespace Wix = "http://schemas.microsoft.com/wix/2006/wi";

    private static byte[] Executable(string mesh = "a", ushort machine = 0x8664)
    {
        byte[] native = new byte[256];
        native[0] = (byte)'M';
        native[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(native.AsSpan(0x3c), 128);
        "PE\0\0"u8.CopyTo(native.AsSpan(128));
        BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(132), machine);
        string policy = $"ServerID={new string('b', 96)}\r\nMeshID=0x{string.Concat(Enumerable.Repeat(mesh, 96))}\r\nMeshServer=wss://controller.example.invalid/agent.ashx\r\nmeshServiceName=MeshAllAgent\r\nfileName=MeshAllAgent\r\n";
        return NativeAgentExecutable.Build(native, policy, "Apache License 2.0");
    }

    [Fact]
    public void MsiUsesStandardMachineServiceActionsAndLeavesRuntimeIdentityUnowned()
    {
        XDocument definition = NativeMsiPackage.Definition(Executable(), "0.9.0");
        XElement service = Assert.Single(definition.Descendants(Wix + "ServiceInstall"));
        Assert.Equal("MeshAllAgent", service.Attribute("Name")!.Value);
        Assert.Equal("auto", service.Attribute("Start")!.Value);
        Assert.Equal("ownProcess", service.Attribute("Type")!.Value);
        Assert.Null(service.Attribute("Account")); // Windows Installer's LocalSystem default.
        XElement control = Assert.Single(definition.Descendants(Wix + "ServiceControl"));
        Assert.Equal("both", control.Attribute("Stop")!.Value);
        Assert.Equal("uninstall", control.Attribute("Remove")!.Value);
        Assert.Equal("perMachine", Assert.Single(definition.Descendants(Wix + "Package")).Attribute("InstallScope")!.Value);
        Assert.Empty(definition.Descendants(Wix + "CustomAction"));
        Assert.Empty(definition.Descendants(Wix + "RemoveFile"));
        string[] files = definition.Descendants(Wix + "File").Select(file => file.Attribute("Source")!.Value).ToArray();
        Assert.Equal(new[] { "MeshAllAgent.exe", "EndpointControl-Agent-Notices.txt" }, files);
        Assert.DoesNotContain(files, file => file.EndsWith(".db", StringComparison.Ordinal));
    }

    [Fact]
    public void UpgradeFamilyAndComponentsStayStableButPayloadAndReleaseChangesHaveNewProducts()
    {
        XElement Product(byte[] executable, string version) => NativeMsiPackage.Definition(executable, version).Root!.Element(Wix + "Product")!;
        XElement first = Product(Executable(), "0.9.0");
        XElement repeat = Product(Executable(), "0.9.0");
        XElement renewedKey = Product(Executable("c"), "0.9.0");
        XElement nextRelease = Product(Executable(), "0.9.1");
        Assert.Equal(first.Attribute("Id")!.Value, repeat.Attribute("Id")!.Value);
        Assert.NotEqual(first.Attribute("Id")!.Value, renewedKey.Attribute("Id")!.Value);
        Assert.NotEqual(first.Attribute("Id")!.Value, nextRelease.Attribute("Id")!.Value);
        foreach (XElement product in new[] { first, renewedKey, nextRelease })
        {
            Assert.Equal(NativeMsiPackage.UpgradeCode, product.Attribute("UpgradeCode")!.Value);
            XElement upgrade = product.Element(Wix + "MajorUpgrade")!;
            Assert.Equal("afterInstallInitialize", upgrade.Attribute("Schedule")!.Value);
            Assert.Equal("yes", upgrade.Attribute("AllowSameVersionUpgrades")!.Value);
            Assert.Contains(product.Elements(Wix + "Condition"), condition => condition.Value.Contains("EXISTINGINSTANCE = SERVERINSTANCE", StringComparison.Ordinal));
            Assert.Contains(product.Elements(Wix + "Condition"), condition => condition.Value.Contains("NOT EXISTINGAGENTSERVICE", StringComparison.Ordinal));
        }
        Assert.Equal(first.Descendants(Wix + "Component").Single().Attribute("Guid")!.Value,
            renewedKey.Descendants(Wix + "Component").Single().Attribute("Guid")!.Value);
    }

    [Theory]
    [InlineData(0x014c)]
    [InlineData(0xaa64)]
    public void MsiCannotMislabelAnotherArchitectureAsWindowsX64(int machine) =>
        Assert.Throws<InvalidOperationException>(() => NativeMsiPackage.Definition(Executable(machine: (ushort)machine), "0.9.0"));

    [Fact]
    public void InvalidOrMissingEmbeddedEnrollmentIsRejectedBeforeCompilerExecution()
    {
        byte[] executable = Executable();
        executable[^1] ^= 1;
        Assert.Throws<InvalidOperationException>(() => NativeMsiPackage.ServerIdentity(executable));
        executable = Executable();
        BinaryPrimitives.WriteInt32BigEndian(executable.AsSpan(executable.Length - 20), int.MaxValue);
        Assert.Throws<InvalidOperationException>(() => NativeMsiPackage.ServerIdentity(executable));
        Assert.Throws<InvalidOperationException>(() => NativeMsiPackage.ServerIdentity([]));
        Assert.Throws<InvalidOperationException>(() => NativeMsiPackage.ServerIdentity(Executable("!")));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("256.0.0")]
    [InlineData("0.256.0")]
    [InlineData("0.1.65536")]
    [InlineData("0.1.-1")]
    public void UnsupportedMsiVersionsAreRejected(string version) =>
        Assert.Throws<InvalidOperationException>(() => NativeMsiPackage.Definition(Executable(), version));

    [Fact]
    public void DeploymentKeysSupportBoundedLargeComputerAssignments()
    {
        using TestServer server = new();
        (string Id, string Token) token = server.Store.CreateToken("GPO rollout", "default", EnrollmentPolicy.MaximumDevices, EnrollmentPolicy.MaximumHours);
        Assert.Equal(10_000, server.Store.Tokens().Single(key => key.Id == token.Id).Remaining);
        Assert.Throws<InvalidOperationException>(() => server.Store.CreateToken("Too many", "default", EnrollmentPolicy.MaximumDevices + 1, 24));
        Assert.Throws<InvalidOperationException>(() => server.Store.CreateToken("Too long", "default", 1, EnrollmentPolicy.MaximumHours + 1));
    }
}

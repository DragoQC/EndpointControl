using System.Net;
using System.Net.NetworkInformation;
using MeshAll.Shared;

namespace MeshAll.Server.Services;

public static class ControllerBoundary
{
    private static readonly Lazy<HashSet<string>> Addresses = new(() => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(address => Normalize(address.Address)).ToHashSet(StringComparer.Ordinal));

    private static string Normalize(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    public static bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address) || Addresses.Value.Contains(Normalize(address));

    public static bool IsController(Inventory inventory) => string.Equals(inventory.Hostname, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        && IPAddress.TryParse(inventory.ConnectionAddress, out IPAddress? address) && IsLocal(address);
}

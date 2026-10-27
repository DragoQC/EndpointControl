namespace MeshAll.Server.Services;

public static class RemoteTools
{
    public static bool IsKnown(string tool) => tool is "desktop" or "terminal" or "powershell" or "rdp" or "vnc" or "ssh" or "amt";

    public static string Label(string tool) => tool switch
    {
        "desktop" => "Agent desktop",
        "terminal" => "Agent terminal",
        "powershell" => "PowerShell terminal",
        "rdp" => "Remote Desktop (RDP)",
        "vnc" => "VNC",
        "ssh" => "SSH",
        "amt" => "Intel AMT KVM",
        _ => "Remote tool"
    };
}

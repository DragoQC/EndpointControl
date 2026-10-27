namespace MeshAll.Shared;

public static class ScriptTypes
{
    public static readonly string[] All = [".ps1", ".cmd", "bash", ".sh", "auto"];

    public static string Resolve(string type, bool windows)
    {
        type = type.Trim().ToLowerInvariant();
        if (type == "auto")
        {
            return windows ? ".ps1" : ".sh";
        }

        if (!All.Contains(type, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Unknown script type.");
        }

        if (windows && type is ".sh" or "bash" || !windows && type is ".ps1" or ".cmd")
        {
            throw new InvalidOperationException("This script type does not match the target operating system.");
        }

        return type;
    }
    public static string Label(string type) => type switch
    {
        ".ps1" => "PowerShell (.ps1)",
        ".cmd" => "Command Prompt (.cmd)",
        "bash" => "Bash (.sh)",
        ".sh" => "Shell (.sh)",
        _ => "Automatic (platform default)"
    };

    public static bool Compatible(string type, bool windows) => type == "auto" || (windows ? type is ".ps1" or ".cmd" : type is ".sh" or "bash");

    public static string Extension(string type) => type == "bash" ? ".sh" : type;

    public static string PowerShellFileInvocation(string pathExpression) => "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $global:LASTEXITCODE=$null; & " + pathExpression + "; $meshallSuccess=$?; if($null -ne $LASTEXITCODE){exit $LASTEXITCODE}; if(!$meshallSuccess){exit 1}";

    public static string Infer(string name, string type) => type == "auto"
        ? name.EndsWith(".bash", StringComparison.OrdinalIgnoreCase) ? "bash"
            : All.FirstOrDefault(t => t.StartsWith('.') && name.EndsWith(t, StringComparison.OrdinalIgnoreCase)) ?? type
        : type;

    public static string FileName(string name, string type)
    {
        if (type == "auto")
        {
            return name;
        }

        string? extension = new[] { ".ps1", ".cmd", ".sh", ".bash" }.FirstOrDefault(value => name.EndsWith(value, StringComparison.OrdinalIgnoreCase));
        return (extension is null ? name : name[..^extension.Length]) + Extension(type);
    }
}

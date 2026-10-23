namespace MeshAll.Shared;

public static class ScriptTypes
{
    public static readonly string[] All=[".ps1",".cmd",".sh","auto"];
    public static string Resolve(string type,bool windows)
    {
        type=type.Trim().ToLowerInvariant();if(type=="auto")return windows?".ps1":".sh";
        if(!All.Contains(type,StringComparer.Ordinal))throw new InvalidOperationException("Unknown script type.");
        if(windows && type==".sh" || !windows && type is ".ps1" or ".cmd")throw new InvalidOperationException("This script type does not match the target operating system.");
        return type;
    }
    public static string Label(string type)=>type switch{".ps1"=>"PowerShell (.ps1)",".cmd"=>"Command Prompt (.cmd)",".sh"=>"Shell (.sh)",_=>"Automatic (platform default)"};
    public static bool Compatible(string type,bool windows)=>type=="auto"||(windows?type is ".ps1" or ".cmd":type==".sh");
    public static string PowerShellFileInvocation(string pathExpression)=>"[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $global:LASTEXITCODE=$null; & "+pathExpression+"; $meshallSuccess=$?; if($null -ne $LASTEXITCODE){exit $LASTEXITCODE}; if(!$meshallSuccess){exit 1}";
    public static string Infer(string name,string type)=>type=="auto"?All.FirstOrDefault(t=>t!="auto"&&name.EndsWith(t,StringComparison.OrdinalIgnoreCase))??type:type;
    public static string FileName(string name,string type)
    {
        if(type=="auto")return name;
        var extension=All.FirstOrDefault(t=>t!="auto"&&name.EndsWith(t,StringComparison.OrdinalIgnoreCase));
        return(extension is null?name:name[..^extension.Length])+type;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeshAll.Server.Services;

public sealed class NativeCoreBuilder(IWebHostEnvironment environment)
{
    public (byte[] Bytes, byte[] Hash) Build(bool windows, bool intel)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "AgentCore");
        if (!Directory.Exists(path))
        {
            path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../../components/agent-core"));
        }

        StringBuilder builder = new StringBuilder("\0\0\0\0var addedModules=[];\r\n");
        foreach (string? file in Directory.GetFiles(Path.Combine(path, "modules_meshcore")).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("win-", StringComparison.Ordinal) && !windows || name.StartsWith("linux-", StringComparison.Ordinal) && windows || (name.StartsWith("amt-", StringComparison.Ordinal) || name == "smbios") && !intel)
            {
                continue;
            }

            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append("var ").Append(name).Append("=JSON.parse(").Append(JsonSerializer.Serialize(File.ReadAllText(file))).Append(");\r\n");
            }
            else if (file.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append("try{addModule(").Append(JsonSerializer.Serialize(name)).Append(',').Append(JsonSerializer.Serialize(File.ReadAllText(file))).Append(");addedModules.push(").Append(JsonSerializer.Serialize(name)).Append(");}catch(ex){}\r\n");
            }
        }
        builder.Append(File.ReadAllText(Path.Combine(path, "meshcore.js")));
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return (bytes, SHA384.HashData(bytes));
    }
}

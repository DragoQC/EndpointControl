using MeshAll.Shared;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Services;

public sealed record ScriptOption(string Id, string Name, string Platform, string Kind);

public sealed partial class Store
{
    public List<ScriptInfo> Scripts() => Query(
        "SELECT id,name,platform,code,kind FROM scripts ORDER BY name", ReadScript);

    public PageResult<ScriptInfo> ScriptPage(PageRequest request) => ReadPage(
        "SELECT id,name,platform,code,kind FROM scripts ORDER BY name COLLATE NOCASE,id",
        "SELECT COUNT(*) FROM scripts",
        ReadScript,
        request);

    public ScriptInfo? FindScript(string id) => Query(
        "SELECT id,name,platform,code,kind FROM scripts WHERE id=$0", ReadScript, id).SingleOrDefault();

    /// <summary>Dropdowns need names and types, not the contents of every script.</summary>
    public List<ScriptOption> ScriptOptions(string platform) => Query(
        "SELECT id,name,platform,kind FROM scripts WHERE platform=$0 OR platform='Any' ORDER BY name COLLATE NOCASE,id",
        reader => new ScriptOption(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            ScriptTypes.Infer(reader.GetString(1), reader.GetString(3))),
        platform);

    public void SaveScript(string? id, string name, string platform, string code, string kind = "auto")
    {
        if (platform is not ("Windows" or "Linux" or "macOS" or "Any"))
        {
            throw new InvalidOperationException("Invalid platform.");
        }

        kind = ScriptTypes.Infer(name, kind);
        if (!ScriptTypes.All.Contains(kind, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Choose a script type.");
        }
        if (kind is ".ps1" or ".cmd" && platform != "Windows" || kind is ".sh" or "bash" && platform == "Windows")
        {
            throw new InvalidOperationException("Choose a compatible script type and platform.");
        }
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000)
        {
            throw new InvalidOperationException("Enter a command up to 16,000 characters.");
        }

        Execute("INSERT INTO scripts(id,name,platform,code,kind) VALUES($0,$1,$2,$3,$4) ON CONFLICT(id) DO UPDATE SET name=$1,platform=$2,code=$3,kind=$4",
            id ?? Guid.NewGuid().ToString("N"), Required(ScriptTypes.FileName(name.Trim(), kind), 80), platform, code, kind);
    }

    public void DeleteScript(string id) => Execute("DELETE FROM scripts WHERE id=$0", id);

    private static ScriptInfo ReadScript(SqliteDataReader reader)
    {
        string kind = ScriptTypes.Infer(reader.GetString(1), reader.GetString(4));
        string platform = kind is ".ps1" or ".cmd" ? "Windows" : reader.GetString(2);

        return new ScriptInfo(reader.GetString(0), reader.GetString(1), platform, reader.GetString(3), kind);
    }
}

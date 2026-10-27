namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public string? RemoteIdentity(string deviceId, string tool, int port) => Query(
        "SELECT value FROM remote_identities WHERE deviceId=$0 AND tool=$1 AND port=$2", reader => reader.GetString(0), deviceId, tool, port).SingleOrDefault();

    public void SaveRemoteIdentity(string deviceId, string tool, int port, string value) => Execute(
        "INSERT INTO remote_identities(deviceId,tool,port,value) VALUES($0,$1,$2,$3) ON CONFLICT(deviceId,tool,port) DO UPDATE SET value=$3", deviceId, tool, port, value);
}

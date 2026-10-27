using System.Globalization;
using System.Text.Json;
using MeshAll.Shared;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    public List<DeviceResourceInfo> ConnectedResources(IReadOnlyCollection<string> connectedIds)
    {
        if (connectedIds.Count == 0)
        {
            return [];
        }

        // Select only resource fields. Network-interface inventories, notes and
        // session histories are not materialized to calculate device statistics.
        return Query("""
            SELECT d.id,d.name,COALESCE(json_extract(d.inventory,'$.platform'),'Pending'),
                COALESCE(json_extract(d.inventory,'$.ip'),''),d.groupId,COALESCE(g.name,'Unassigned'),
                COALESCE(json_extract(d.inventory,'$.cores'),0),
                COALESCE(json_extract(d.inventory,'$.cpu'),-1),
                COALESCE(json_extract(d.inventory,'$.memoryTotal'),0),
                COALESCE(json_extract(d.inventory,'$.memoryUsed'),0),
                COALESCE(json_extract(d.inventory,'$.disks'),'[]'),
                json_extract(d.inventory,'$.cpuReportedAt'),
                json_extract(d.inventory,'$.memoryReportedAt'),
                json_extract(d.inventory,'$.disksReportedAt')
            FROM devices d LEFT JOIN groups g ON d.groupId=g.id
            WHERE d.removed=0 AND d.disabled=0 AND d.id IN (SELECT value FROM json_each($0))
            ORDER BY d.name COLLATE NOCASE,d.id
            """, ReadResourceSummary, JsonSerializer.Serialize(connectedIds));
    }

    private static DeviceResourceInfo ReadResourceSummary(SqliteDataReader reader)
    {
        DiskInfo[] disks = JsonSerializer.Deserialize<DiskInfo[]>(reader.GetString(10), Wire.Json) ?? [];

        return new DeviceResourceInfo(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetDouble(7),
            reader.GetInt64(8), reader.GetInt64(9), disks,
            ReadSampleTime(reader, 11), ReadSampleTime(reader, 12), ReadSampleTime(reader, 13));
    }

    private static DateTimeOffset? ReadSampleTime(SqliteDataReader reader, int column) =>
        !reader.IsDBNull(column) && DateTimeOffset.TryParse(reader.GetString(column),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset sampledAt)
            ? sampledAt
            : null;
}

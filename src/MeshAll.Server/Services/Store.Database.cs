using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MeshAll.Server.Services;

public sealed partial class Store
{
    private SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql, params object?[] values)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, sql, values);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params object?[] values)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, sql, values);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] values)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, sql, values);
        using SqliteDataReader reader = command.ExecuteReader();
        List<T> rows = [];
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, object?[] values)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        for (int index = 0; index < values.Length; index++)
        {
            command.Parameters.AddWithValue($"${index}", values[index] ?? DBNull.Value);
        }

        return command;
    }
}

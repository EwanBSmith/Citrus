using System.Data;
using System.Text.Json;
using Citrus.Data;

namespace Citrus.Desktop;

/// <summary>Converts backtest records into typed, flattened tables for desktop result views.</summary>
internal static class ResultTable
{
    private const int MaximumRows = 5000;

    /// <summary>Builds a table with a bounded row count for display; the caller owns its storage.</summary>
    internal static DataTable Create<T>(IEnumerable<T> records)
    {
        var rows = records.Take(MaximumRows).Select(record =>
        {
            var values = new Dictionary<string, object>();
            Flatten(JsonSerializer.SerializeToElement(record, Json.Options), "", values);
            return values;
        }).ToArray();

        var table = new DataTable();
        foreach (var key in rows.SelectMany(row => row.Keys).Distinct())
        {
            var value = rows.Select(row => row.GetValueOrDefault(key))
                .FirstOrDefault(value => value is not null && value != DBNull.Value);
            table.Columns.Add(key, value?.GetType() ?? typeof(string));
        }
        foreach (var row in rows)
            table.Rows.Add(table.Columns.Cast<DataColumn>()
                .Select(column => row.GetValueOrDefault(column.ColumnName, DBNull.Value)).ToArray());
        return table;
    }

    /// <summary>Expands objects such as instruments and substrategy dictionaries into named scalar columns.</summary>
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, object> row)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, row);
            return;
        }
        row[prefix] = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => DBNull.Value,
            _ => element.ToString()
        };
    }
}

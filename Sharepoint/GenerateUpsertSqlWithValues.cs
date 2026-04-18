using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;

public string GenerateUpsertSqlWithValues(string tableName, Dictionary<string, object> data, string constraintColumn)
{
    if (string.IsNullOrEmpty(tableName) || data == null || data.Count == 0)
        throw new ArgumentException("Table name and data are required.");

    var columns = data.Keys.ToList();
    
    // 1. Format the literal values (handling strings, dates, and nulls)
    var formattedValues = data.Values.Select(FormatSqlValue);

    // 2. Build the UPDATE assignments (e.g., Name = 'John')
    var updateFields = data
        .Where(kvp => !kvp.Key.Equals(constraintColumn, StringComparison.OrdinalIgnoreCase))
        .Select(kvp => $"{kvp.Key} = {FormatSqlValue(kvp.Value)}");

    var sql = new StringBuilder();
    sql.AppendLine($"INSERT INTO {tableName} ({string.Join(", ", columns)})");
    sql.AppendLine($"VALUES ({string.Join(", ", formattedValues)})");
    sql.AppendLine($"ON CONFLICT ({constraintColumn})");
    sql.AppendLine($"DO UPDATE SET {string.Join(", ", updateFields)};");

    return sql.ToString();
}

private string FormatSqlValue(object value)
{
    if (value == null || value is DBNull) return "NULL";

    return value switch
    {
        string s => $"'{s.Replace("'", "''")}'", // Escape single quotes
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        bool b => b ? "TRUE" : "FALSE",
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        decimal dec => dec.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
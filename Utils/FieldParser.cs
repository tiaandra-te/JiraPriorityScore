using System.Text.Json;

namespace JiraPriorityScore.Utils;

public static class FieldParser
{
    public static string? GetFieldString(JsonElement fields, string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || !fields.TryGetProperty(fieldId, out var fieldValue))
        {
            return null;
        }

        if (fieldValue.ValueKind == JsonValueKind.String)
        {
            return fieldValue.GetString();
        }

        if (fieldValue.ValueKind == JsonValueKind.Object)
        {
            if (fieldValue.TryGetProperty("name", out var nameProp))
            {
                return nameProp.GetString();
            }

            if (fieldValue.TryGetProperty("value", out var valueProp))
            {
                return valueProp.GetString();
            }
        }

        return fieldValue.ToString();
    }

    public static double? GetFieldNumber(JsonElement fields, string? fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || !fields.TryGetProperty(fieldId, out var fieldValue))
        {
            return null;
        }

        if (fieldValue.ValueKind == JsonValueKind.Number && fieldValue.TryGetDouble(out var number))
        {
            return number;
        }

        if (fieldValue.ValueKind == JsonValueKind.String && double.TryParse(fieldValue.GetString(), out var parsed))
        {
            return parsed;
        }

        if (fieldValue.ValueKind == JsonValueKind.Object)
        {
            if (fieldValue.TryGetProperty("value", out var valueProp) &&
                valueProp.ValueKind == JsonValueKind.String &&
                double.TryParse(valueProp.GetString(), out var valueParsed))
            {
                return valueParsed;
            }

            if (fieldValue.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String &&
                double.TryParse(nameProp.GetString(), out var nameParsed))
            {
                return nameParsed;
            }
        }

        if (fieldValue.ValueKind == JsonValueKind.Array)
        {
            var first = fieldValue.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Number && first.TryGetDouble(out var firstNumber))
            {
                return firstNumber;
            }

            if (first.ValueKind == JsonValueKind.String && double.TryParse(first.GetString(), out var firstParsed))
            {
                return firstParsed;
            }
        }

        return null;
    }

    public static string FormatNumber(double? value)
    {
        return value.HasValue ? value.Value.ToString("0.####") : "null";
    }

    public static bool IsMatch(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

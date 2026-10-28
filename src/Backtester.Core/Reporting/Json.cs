using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backtester.Core.Reporting;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Same settings on one line, for JSON Lines files.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>
    /// Reads a number from any JSON node: parsed or in-memory, int or double, or the "NaN"/"Infinity" strings
    /// written for non-finite values. Anything else is NaN.
    /// </summary>
    public static double Number(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is not System.Text.Json.Nodes.JsonValue v) return double.NaN;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String => v.GetValue<string>() switch
            {
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                var s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
            },
            _ => double.NaN,
        };
    }

    public static void Write(string path, object value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }
}

using System.Reflection;
using System.Text.Json;
using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// T-S7-05. Per-entity field allow-list + JSON-equality filter
/// interpreter for the basic custom report builder. Pure
/// reflection / JSON parsing — no IO, no DB, no DI. Service
/// calls Validate at write time and Match / Project at run
/// time. Operators beyond equality (gt / lt / ne / contains) →
/// v1.1 / B-060. Cross-entity joins → B-056.
/// </summary>
public static class CustomReportRunner
{
    /// <summary>Per-entity allow-list of fields callers may filter
    /// on or project. Tied to the entity's own property names so
    /// the JSON contract is the same surface developers already
    /// see in code. Adding new entries is the supported extension
    /// point for v1.1.</summary>
    public static readonly IReadOnlyDictionary<CustomReportEntityType, IReadOnlyList<string>>
        AllowedFields = new Dictionary<CustomReportEntityType, IReadOnlyList<string>>
        {
            [CustomReportEntityType.Risk] = new[]
            {
                "Title", "Probability", "Impact", "Score", "Status",
                "ResponseStrategy", "OwnerId", "ContingencyAmount",
                "AssessedAt", "CreatedAt",
            },
            [CustomReportEntityType.ActionItem] = new[]
            {
                "Title", "Source", "Priority", "Status",
                "DueDate", "ClosedAt", "AssigneeId", "CreatedAt",
            },
            [CustomReportEntityType.Rfi] = new[]
            {
                "RfiNumber", "Subject", "Discipline", "Priority",
                "Status", "DueDate", "ClosedAt", "AssignedToId", "CreatedAt",
            },
            [CustomReportEntityType.Variation] = new[]
            {
                "VariationNumber", "Title", "State",
                "EstimatedCostImpact", "EstimatedTimeImpactDays",
                "DecidedAt", "CreatedAt",
            },
            [CustomReportEntityType.ChangeRequest] = new[]
            {
                "Number", "Title", "Category", "BsaCategory", "State",
                "EstimatedCostImpact", "EstimatedTimeImpactDays",
                "RaisedAt", "AssessedAt", "DecisionAt",
                "ImplementedAt", "ClosedAt", "CreatedAt",
            },
        };

    private static ValidationException Bad(string msg) =>
        new(new List<string> { msg });

    /// <summary>Validate JSON shape + every key against the
    /// per-entity allow-list. Throws <see cref="ValidationException"/>
    /// on any unknown field. Returns the parsed filter dictionary
    /// for re-use at run time.</summary>
    public static Dictionary<string, JsonElement> ValidateFilterJson(
        string filterJson, CustomReportEntityType entityType)
    {
        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(filterJson);
        }
        catch (JsonException ex)
        {
            throw Bad($"FilterJson is not a valid JSON object: {ex.Message}");
        }
        parsed ??= new Dictionary<string, JsonElement>();
        var allowed = AllowedFields[entityType];
        foreach (var key in parsed.Keys)
            if (!allowed.Contains(key))
                throw Bad($"Filter field '{key}' not in allow-list for {entityType}");
        return parsed;
    }

    /// <summary>Validate JSON shape + every entry against the
    /// per-entity allow-list. Throws <see cref="ValidationException"/>
    /// on any unknown column. Empty array means "all allow-list
    /// columns".</summary>
    public static List<string> ValidateColumnsJson(
        string columnsJson, CustomReportEntityType entityType)
    {
        List<string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<string>>(columnsJson);
        }
        catch (JsonException ex)
        {
            throw Bad($"ColumnsJson is not a valid JSON array: {ex.Message}");
        }
        parsed ??= new List<string>();
        var allowed = AllowedFields[entityType];
        if (parsed.Count == 0) return allowed.ToList();
        foreach (var col in parsed)
            if (!allowed.Contains(col))
                throw Bad($"Column '{col}' not in allow-list for {entityType}");
        return parsed;
    }

    /// <summary>Reflection equality between an entity property
    /// value and a JSON-supplied filter value. Handles strings,
    /// ints, decimals, DateTime, Guid, bool, and enums (string or
    /// numeric). Null on either side matches null only.</summary>
    public static bool MatchesFilter(
        object entity, Dictionary<string, JsonElement> filter)
    {
        var t = entity.GetType();
        foreach (var (key, expected) in filter)
        {
            var prop = t.GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
                throw Bad($"Field '{key}' not found on {t.Name}");
            var actual = prop.GetValue(entity);
            if (!ValuesEqual(actual, prop.PropertyType, expected))
                return false;
        }
        return true;
    }

    /// <summary>Project a single entity row to the requested
    /// columns. Enums become their string name for JSON-friendly
    /// output. Unknown column names are guarded by ValidateColumnsJson
    /// at write time, so this is a fast happy-path lookup.</summary>
    public static Dictionary<string, object?> ProjectColumns(
        object entity, IReadOnlyList<string> columns)
    {
        var t = entity.GetType();
        var dict = new Dictionary<string, object?>(columns.Count);
        foreach (var col in columns)
        {
            var prop = t.GetProperty(col, BindingFlags.Public | BindingFlags.Instance);
            var value = prop?.GetValue(entity);
            if (value is Enum e) value = e.ToString();
            dict[col] = value;
        }
        return dict;
    }

    private static bool ValuesEqual(object? actual, Type propType, JsonElement expected)
    {
        if (actual is null) return expected.ValueKind == JsonValueKind.Null;
        var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
        if (underlying.IsEnum)
        {
            if (expected.ValueKind == JsonValueKind.String)
                return string.Equals(actual.ToString(), expected.GetString(),
                    StringComparison.OrdinalIgnoreCase);
            if (expected.ValueKind == JsonValueKind.Number)
                return Convert.ToInt32(actual) == expected.GetInt32();
            return false;
        }
        if (underlying == typeof(string))
            return string.Equals((string)actual, expected.GetString(),
                StringComparison.OrdinalIgnoreCase);
        if (underlying == typeof(int))
            return (int)actual == expected.GetInt32();
        if (underlying == typeof(decimal))
            return (decimal)actual == expected.GetDecimal();
        if (underlying == typeof(DateTime))
            return (DateTime)actual == expected.GetDateTime();
        if (underlying == typeof(Guid))
        {
            var s = expected.GetString();
            return s is not null && (Guid)actual == Guid.Parse(s);
        }
        if (underlying == typeof(bool))
            return (bool)actual == expected.GetBoolean();
        return false;
    }
}

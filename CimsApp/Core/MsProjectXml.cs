using System.Globalization;
using System.Xml.Linq;
using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// Microsoft Project XML import / export primitives (T-S4-09,
/// PAFM-SD F.5 fifth bullet — "Import from and export to MS Project
/// XML"). Pure functions, no IO into the DB; the parser takes an
/// XML stream and returns parsed activity + dependency snapshots.
/// The export path (T-S4-10) is deferred to v1.1 per CR-005 / B-031.
///
/// MS Project XML structure (relevant elements):
///   &lt;Project xmlns="http://schemas.microsoft.com/project"&gt;
///     &lt;Name&gt;...&lt;/Name&gt;
///     &lt;StartDate&gt;...&lt;/StartDate&gt;
///     &lt;Tasks&gt;
///       &lt;Task&gt;
///         &lt;UID&gt;...&lt;/UID&gt;
///         &lt;Name&gt;...&lt;/Name&gt;
///         &lt;Duration&gt;PT8H0M0S&lt;/Duration&gt;     ← ISO 8601 duration
///         &lt;Start&gt;...&lt;/Start&gt;
///         &lt;Finish&gt;...&lt;/Finish&gt;
///         &lt;PercentComplete&gt;...&lt;/PercentComplete&gt;
///         &lt;PredecessorLink&gt;
///           &lt;PredecessorUID&gt;...&lt;/PredecessorUID&gt;
///           &lt;Type&gt;1&lt;/Type&gt;     ← 0=FF, 1=FS, 2=SF, 3=SS
///           &lt;LinkLag&gt;...&lt;/LinkLag&gt;
///         &lt;/PredecessorLink&gt;
///       &lt;/Task&gt;
///     &lt;/Tasks&gt;
///   &lt;/Project&gt;
///
/// Strict-on-core, lenient-on-optional: missing UID / Name / Duration
/// is a parse error; missing Start / Finish / PercentComplete is
/// silently substituted with sensible defaults (null / 0).
/// </summary>
public static class MsProjectXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/project";

    /// <summary>Parsed activity from MSP XML. UID is the MS-Project
    /// stable identifier (string-typed because the XML uses arbitrary
    /// integers; we treat them as opaque). Duration is converted to
    /// decimal days at 8 working-hours-per-day; v1.1 candidate is
    /// honouring the MSP project calendar's hours-per-day.</summary>
    public readonly record struct ParsedActivity(
        string Uid,
        string Name,
        decimal DurationDays,
        DateTime? Start,
        DateTime? Finish,
        decimal PercentComplete);

    /// <summary>Parsed predecessor link. PredecessorUid points at the
    /// activity the dependency sources from; SuccessorUid is the
    /// activity the link is *attached to* in the MSP XML (since each
    /// Task carries its own incoming PredecessorLinks).</summary>
    public readonly record struct ParsedDependency(
        string PredecessorUid,
        string SuccessorUid,
        DependencyType Type,
        decimal LagDays);

    public readonly record struct ImportResult(
        string? ProjectName,
        DateTime? ProjectStart,
        IReadOnlyList<ParsedActivity> Activities,
        IReadOnlyList<ParsedDependency> Dependencies);

    /// <summary>
    /// Parse an MSP XML stream. Strict on the core shape: throws
    /// <see cref="FormatException"/> on missing root element / missing
    /// namespace / malformed Duration. Lenient on per-Task optional
    /// fields. Caller owns the stream.
    /// </summary>
    public static ImportResult Parse(Stream xml)
    {
        XDocument doc;
        try { doc = XDocument.Load(xml); }
        catch (Exception ex) when (ex is System.Xml.XmlException)
            { throw new FormatException("MS Project XML is not well-formed", ex); }

        var root = doc.Root ?? throw new FormatException("MS Project XML has no root element");
        if (root.Name != Ns + "Project")
            throw new FormatException(
                $"MS Project XML root must be {{{Ns}}}Project; got {root.Name}");

        var name  = root.Element(Ns + "Name")?.Value;
        var start = ParseOptionalDate(root.Element(Ns + "StartDate")?.Value);

        var tasksRoot = root.Element(Ns + "Tasks");
        var activities = new List<ParsedActivity>();
        var dependencies = new List<ParsedDependency>();
        if (tasksRoot is not null)
        {
            foreach (var taskEl in tasksRoot.Elements(Ns + "Task"))
            {
                var (act, succUid) = ParseTask(taskEl);
                activities.Add(act);

                foreach (var linkEl in taskEl.Elements(Ns + "PredecessorLink"))
                {
                    dependencies.Add(ParsePredecessorLink(linkEl, succUid));
                }
            }
        }

        return new ImportResult(name, start, activities, dependencies);
    }

    private static (ParsedActivity activity, string uid) ParseTask(XElement task)
    {
        var uid = task.Element(Ns + "UID")?.Value
            ?? throw new FormatException("Task is missing required <UID>");
        var taskName = task.Element(Ns + "Name")?.Value
            ?? throw new FormatException($"Task UID={uid} is missing required <Name>");
        var durationStr = task.Element(Ns + "Duration")?.Value
            ?? throw new FormatException($"Task UID={uid} is missing required <Duration>");

        var duration = ParseIsoDurationDays(durationStr);
        var taskStart  = ParseOptionalDate(task.Element(Ns + "Start")?.Value);
        var taskFinish = ParseOptionalDate(task.Element(Ns + "Finish")?.Value);
        var pct = ParseOptionalDecimal(task.Element(Ns + "PercentComplete")?.Value);
        // MSP PercentComplete is 0..100; we store 0..1.
        var pctFraction = (pct ?? 0m) / 100m;

        return (new ParsedActivity(uid, taskName, duration, taskStart, taskFinish, pctFraction), uid);
    }

    private static ParsedDependency ParsePredecessorLink(XElement link, string successorUid)
    {
        var predUid = link.Element(Ns + "PredecessorUID")?.Value
            ?? throw new FormatException("PredecessorLink is missing <PredecessorUID>");
        var typeStr = link.Element(Ns + "Type")?.Value ?? "1";   // FS default
        var lagStr  = link.Element(Ns + "LinkLag")?.Value ?? "0";

        var type = MapDependencyType(typeStr);
        // MSP LinkLag is in 10ths of a minute; convert to days at
        // 8h/day = 4800 tenth-minutes per day. v1.1 candidate: honour
        // MSP project calendar's working-hours-per-day.
        if (!int.TryParse(lagStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lagTenthMins))
            throw new FormatException($"PredecessorLink LinkLag '{lagStr}' is not an integer");
        var lagDays = (decimal)lagTenthMins / 4800m;

        return new ParsedDependency(predUid, successorUid, type, lagDays);
    }

    private static DependencyType MapDependencyType(string mspType) => mspType switch
    {
        "0" => DependencyType.FF,
        "1" => DependencyType.FS,
        "2" => DependencyType.SF,
        "3" => DependencyType.SS,
        _   => throw new FormatException(
            $"Unknown MSP PredecessorLink Type '{mspType}'; expected 0/1/2/3"),
    };

    /// <summary>
    /// Parse ISO 8601 duration like PT8H0M0S, P3D, P1DT4H30M, etc.
    /// Returns the duration in decimal days at 8 working-hours-per-day.
    /// MSP exports usually use the PT...H...M...S form for working
    /// time. Throws <see cref="FormatException"/> on unrecognised
    /// format.
    /// </summary>
    public static decimal ParseIsoDurationDays(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso) || iso[0] != 'P')
            throw new FormatException($"Duration '{iso}' must start with P");

        decimal days = 0m;
        decimal hours = 0m;
        decimal minutes = 0m;
        decimal seconds = 0m;

        // Split into date / time halves on T.
        var tIdx = iso.IndexOf('T');
        var datePart = tIdx >= 0 ? iso.Substring(1, tIdx - 1) : iso[1..];
        var timePart = tIdx >= 0 ? iso[(tIdx + 1)..] : "";

        days = ExtractUnit(ref datePart, 'D');
        if (datePart.Length > 0)
            throw new FormatException($"Duration '{iso}' has unrecognised date-part token '{datePart}'");

        hours   = ExtractUnit(ref timePart, 'H');
        minutes = ExtractUnit(ref timePart, 'M');
        seconds = ExtractUnit(ref timePart, 'S');
        if (timePart.Length > 0)
            throw new FormatException($"Duration '{iso}' has unrecognised time-part token '{timePart}'");

        return days
            + hours / 8m
            + minutes / 480m
            + seconds / 28800m;
    }

    private static decimal ExtractUnit(ref string remainder, char unit)
    {
        var idx = remainder.IndexOf(unit);
        if (idx < 0) return 0m;
        var numStr = remainder[..idx];
        if (!decimal.TryParse(numStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
            throw new FormatException($"Duration unit '{unit}' has non-numeric value '{numStr}'");
        remainder = remainder[(idx + 1)..];
        return val;
    }

    private static DateTime? ParseOptionalDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : (DateTime?)null;
    }

    private static decimal? ParseOptionalDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

using System.Text;
using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="MsProjectXml"/> (T-S4-09).
/// Fixture-based: each test feeds a hand-crafted XML blob and asserts
/// the parsed result. No DB / DI / IO.
/// </summary>
public class MsProjectXmlTests
{
    private const string Ns = "http://schemas.microsoft.com/project";

    private static MsProjectXml.ImportResult ParseString(string xml)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return MsProjectXml.Parse(ms);
    }

    // ── Duration parser unit ────────────────────────────────────────

    [Theory]
    [InlineData("PT8H0M0S",   1.0)]    // 1 working day at 8h
    [InlineData("PT16H0M0S",  2.0)]    // 2 working days
    [InlineData("PT4H0M0S",   0.5)]
    [InlineData("PT0H30M0S",  0.0625)] // 30 min = 0.0625 days
    [InlineData("P3D",        3.0)]    // bare day form
    [InlineData("P1DT4H0M0S", 1.5)]    // 1 day + 4h = 1.5 days
    public void ParseIsoDurationDays_handles_common_MSP_formats(string iso, double expectedDays)
    {
        var actual = MsProjectXml.ParseIsoDurationDays(iso);
        Assert.Equal((decimal)expectedDays, actual);
    }

    [Theory]
    [InlineData("8H0M0S")]       // missing P
    [InlineData("PT8X0M0S")]     // unrecognised unit
    [InlineData("Pblah")]
    public void ParseIsoDurationDays_throws_on_malformed(string bad)
    {
        Assert.Throws<FormatException>(() => MsProjectXml.ParseIsoDurationDays(bad));
    }

    // ── Empty / minimal Project XML ─────────────────────────────────

    [Fact]
    public void Parse_returns_empty_lists_for_project_with_no_tasks()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Name>Empty</Name>
  <StartDate>2026-06-01T08:00:00</StartDate>
</Project>";
        var r = ParseString(xml);
        Assert.Equal("Empty",                 r.ProjectName);
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), r.ProjectStart);
        Assert.Empty(r.Activities);
        Assert.Empty(r.Dependencies);
    }

    [Fact]
    public void Parse_throws_on_wrong_namespace_root()
    {
        var xml = @"<?xml version=""1.0""?>
<Project xmlns=""http://example.com/wrong"">
  <Name>X</Name>
</Project>";
        Assert.Throws<FormatException>(() => ParseString(xml));
    }

    [Fact]
    public void Parse_throws_on_missing_root()
    {
        var xml = @"<?xml version=""1.0""?>";
        Assert.Throws<FormatException>(() => ParseString(xml));
    }

    // ── Single task ─────────────────────────────────────────────────

    [Fact]
    public void Parse_extracts_single_task_with_required_fields()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task>
      <UID>1</UID>
      <Name>Excavate foundations</Name>
      <Duration>PT40H0M0S</Duration>
      <Start>2026-06-01T08:00:00</Start>
      <Finish>2026-06-05T17:00:00</Finish>
      <PercentComplete>25</PercentComplete>
    </Task>
  </Tasks>
</Project>";
        var r = ParseString(xml);
        var act = r.Activities.Single();
        Assert.Equal("1",                            act.Uid);
        Assert.Equal("Excavate foundations",         act.Name);
        Assert.Equal(5m,                             act.DurationDays);   // 40h / 8h
        Assert.Equal(0.25m,                          act.PercentComplete); // 25 / 100
        Assert.NotNull(act.Start);
        Assert.NotNull(act.Finish);
    }

    [Fact]
    public void Parse_throws_when_task_missing_required_field()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task>
      <UID>1</UID>
      <!-- Name missing -->
      <Duration>PT8H0M0S</Duration>
    </Task>
  </Tasks>
</Project>";
        Assert.Throws<FormatException>(() => ParseString(xml));
    }

    [Fact]
    public void Parse_uses_zero_for_missing_PercentComplete()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task>
      <UID>1</UID>
      <Name>X</Name>
      <Duration>PT8H0M0S</Duration>
    </Task>
  </Tasks>
</Project>";
        var r = ParseString(xml);
        Assert.Equal(0m, r.Activities.Single().PercentComplete);
    }

    // ── Predecessor links ───────────────────────────────────────────

    [Fact]
    public void Parse_FS_dependency_with_default_lag()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task><UID>1</UID><Name>A</Name><Duration>PT8H0M0S</Duration></Task>
    <Task>
      <UID>2</UID><Name>B</Name><Duration>PT8H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID>
        <Type>1</Type>
        <LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        var r = ParseString(xml);
        var d = r.Dependencies.Single();
        Assert.Equal("1",                  d.PredecessorUid);
        Assert.Equal("2",                  d.SuccessorUid);
        Assert.Equal(DependencyType.FS,    d.Type);
        Assert.Equal(0m,                   d.LagDays);
    }

    [Theory]
    [InlineData("0", DependencyType.FF)]
    [InlineData("1", DependencyType.FS)]
    [InlineData("2", DependencyType.SF)]
    [InlineData("3", DependencyType.SS)]
    public void Parse_maps_MSP_link_type_codes(string mspType, DependencyType expected)
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task><UID>1</UID><Name>A</Name><Duration>PT8H0M0S</Duration></Task>
    <Task>
      <UID>2</UID><Name>B</Name><Duration>PT8H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID>
        <Type>{mspType}</Type>
        <LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        var r = ParseString(xml);
        Assert.Equal(expected, r.Dependencies.Single().Type);
    }

    [Fact]
    public void Parse_rejects_unknown_MSP_link_type()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task><UID>1</UID><Name>A</Name><Duration>PT8H0M0S</Duration></Task>
    <Task>
      <UID>2</UID><Name>B</Name><Duration>PT8H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID>
        <Type>9</Type>
        <LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        Assert.Throws<FormatException>(() => ParseString(xml));
    }

    [Fact]
    public void Parse_LinkLag_tenths_of_minutes_become_days()
    {
        // 4800 tenth-minutes = 8 hours = 1 working day.
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task><UID>1</UID><Name>A</Name><Duration>PT8H0M0S</Duration></Task>
    <Task>
      <UID>2</UID><Name>B</Name><Duration>PT8H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID>
        <Type>1</Type>
        <LinkLag>9600</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        var r = ParseString(xml);
        Assert.Equal(2m, r.Dependencies.Single().LagDays);
    }

    [Fact]
    public void Parse_supports_multiple_predecessor_links_on_one_task()
    {
        var xml = $@"<?xml version=""1.0""?>
<Project xmlns=""{Ns}"">
  <Tasks>
    <Task><UID>1</UID><Name>A</Name><Duration>PT8H0M0S</Duration></Task>
    <Task><UID>2</UID><Name>B</Name><Duration>PT8H0M0S</Duration></Task>
    <Task>
      <UID>3</UID><Name>C</Name><Duration>PT8H0M0S</Duration>
      <PredecessorLink>
        <PredecessorUID>1</PredecessorUID><Type>1</Type><LinkLag>0</LinkLag>
      </PredecessorLink>
      <PredecessorLink>
        <PredecessorUID>2</PredecessorUID><Type>1</Type><LinkLag>0</LinkLag>
      </PredecessorLink>
    </Task>
  </Tasks>
</Project>";
        var r = ParseString(xml);
        Assert.Equal(3, r.Activities.Count);
        Assert.Equal(2, r.Dependencies.Count);
        Assert.All(r.Dependencies, d => Assert.Equal("3", d.SuccessorUid));
    }
}

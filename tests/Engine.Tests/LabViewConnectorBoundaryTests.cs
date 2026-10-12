using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class LabViewConnectorBoundaryTests
{
    [Theory]
    [InlineData(PortDirection.Input)]
    [InlineData(PortDirection.Output)]
    [InlineData(PortDirection.InOut)]
    public void Connector_signature_and_marshalling_follow_port_direction(PortDirection direction)
    {
        var ports = new[] { new CodePort("Text", VarType.String, direction), new CodePort("Number", VarType.Double, direction), new CodePort("Count", VarType.Int, direction), new CodePort("Flag", VarType.Bool, direction) };
        var settings = LabViewSettings.Parse("# comment\nnot an assignment\nfunction = Evaluate\naufruf = stdcall\ndll = C:\\fixture\\test.dll");
        Assert.Equal("Evaluate", settings.Function); Assert.Equal("stdcall", settings.CallingConvention);
        var glue = settings.GenerateGlue(ports); Assert.Contains("#define TU_CC __stdcall", glue);
        Assert.Contains(direction == PortDirection.Input ? "tu_vi_fn)(const char*, double, int32_t, uint8_t)" : "tu_vi_fn)(char*, int32_t, double*, int32_t*, uint8_t*)", glue);
        if (direction == PortDirection.Input) { Assert.Contains("get_bool", glue); Assert.Contains("get_int", glue); Assert.DoesNotContain("host->set_string", glue); }
        else { Assert.Contains("host->set_string", glue); Assert.Contains("host->set_double", glue); Assert.Contains("host->set_int", glue); Assert.Contains("host->set_bool", glue); Assert.Equal(direction == PortDirection.InOut, glue.Contains("std::copy_n")); }
        var unsupported = settings.GenerateGlue([new CodePort("Date", VarType.DateTime, PortDirection.Input)]); Assert.Contains("wird nicht unterstützt", unsupported);
        Assert.Contains("tu_vi_fn)(void)", new LabViewSettings(null, null, null, "C").GenerateGlue([]));
    }
    [Theory]
    [InlineData(VarType.Int, PortDirection.Input, true)]
    [InlineData(VarType.Double, PortDirection.Output, true)]
    [InlineData(VarType.Int, PortDirection.Output, false)]
    [InlineData(VarType.Int, PortDirection.InOut, false)]
    public void Rating_port_requires_integer_output_and_missing_files_are_diagnostic(VarType type, PortDirection direction, bool invalid)
    {
        var settings = new LabViewSettings(null, null, null, "C"); var ports = new[] { new CodePort("Bewertung", type, direction), new CodePort("Date", VarType.DateTime, PortDirection.Input) };
        var problems = settings.Check(ports); Assert.Contains(problems, p => p.Message.Contains("Keine gebaute DLL")); Assert.Contains(problems, p => p.Message.Contains("Funktionsname")); Assert.Contains(problems, p => p.Message.Contains("DateTime")); Assert.Equal(invalid, problems.Any(p => p.Message.Contains("muss ein Int-Ausgang")));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll");
        try
        {
            File.WriteAllText(path, "fixture"); Assert.Empty(new LabViewSettings(path, path, "Evaluate", "C").Check([]));
            var missing = new LabViewSettings(path + ".missing", path + ".missing", "Evaluate", "C").Check([]); Assert.Equal(2, missing.Count); Assert.All(missing, p => Assert.False(p.IsError));
        }
        finally { File.Delete(path); }
    }
}

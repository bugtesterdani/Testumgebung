using Testumgebung.Domain.Curves;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CurvePointTableTests
{
    private static CurveDefinition Source(params CurveChannel[] channels) => new()
    {
        Id = "test", Name = "Test", Version = "1", SampleRate = 2, Channels = channels
    };

    [Fact]
    public void Editing_a_value_keeps_the_fixed_sample_grid_and_original_curve()
    {
        var curve = Source(new CurveChannel("L1", "V", [1, 2, 3]), new("L2", "V", [4, 5, 6]));
        var table = new CurvePointTable(curve);
        var page = table.ReadPage(1, 0).ToArray();
        page[1].Value = "15";
        table.KeepChanges(1, page);

        var edited = table.Build();
        Assert.Null(edited.Times);
        Assert.Equal(2, edited.SampleRate);
        Assert.Equal(new double[] { 4, 15, 6 }, edited.Channels[1].Samples);
        Assert.Same(curve.Channels[0], edited.Channels[0]);
        Assert.Equal(new double[] { 4, 5, 6 }, curve.Channels[1].Samples);
    }

    [Fact]
    public void Moving_a_point_preserves_the_other_line_on_the_shared_time_grid()
    {
        var curve = Source(new CurveChannel("L1", "V", [0, 10, 0]), new("L2", "V", [0, 20, 40]));
        var edited = CurveOps.ReplacePoints(curve, 0, [(1, 0), (0.25, 10), (0, 0)]);
        Assert.Equal(new double[] { 0, 0.25, 0.5, 1 }, edited.Times);
        Assert.Equal(10, edited.ValueAt(0, 0.25));
        Assert.Equal(10, edited.ValueAt(1, 0.25));
        Assert.Equal(20, edited.ValueAt(1, 0.5));
        Assert.Equal(curve.ValueAt(1, 0.75), edited.ValueAt(1, 0.75));
    }

    [Fact]
    public void Page_and_line_changes_retain_edits_additions_and_removals()
    {
        var curve = Source(new CurveChannel("L1", "V", new double[501]), new("L2", "A", new double[501]));
        var table = new CurvePointTable(curve);
        Assert.Equal(2, table.PageCount(0));
        var first = table.ReadPage(0, 0).First();
        first.Value = "12";
        table.KeepChanges(0, [first]);
        var secondLine = table.ReadPage(1, 1).Single();
        table.Remove(1, secondLine);
        var newPoint = table.Add(1);
        newPoint.Time = "250.25";
        newPoint.Value = "8";
        Assert.Single(table.ReadPage(1, 1));
        Assert.Equal("12", table.ReadPage(0, 0).First().Value);

        var edited = table.Build();
        Assert.Equal(12, edited.ValueAt(0, 0));
        Assert.Equal(8, edited.ValueAt(1, 250.25));
        Assert.Equal(0, curve.ValueAt(1, 250.25));
    }

    [Fact]
    public void Rejects_invalid_or_duplicate_points_and_leaves_source_untouched()
    {
        var curve = Source(new CurveChannel("L1", "V", [1, 2]));
        var table = new CurvePointTable(curve);
        Assert.Same(curve, table.Build());
        var row = table.Add(0);
        row.Time = "NaN";
        row.Value = "1";
        Assert.Throws<ArgumentException>(() => table.Build());
        row.Time = "0";
        Assert.Throws<ArgumentException>(() => table.Build());
        Assert.Equal(new double[] { 1, 2 }, curve.Channels[0].Samples);
    }

    [Fact]
    public void Deleting_a_point_on_a_single_line_removes_it_from_the_time_grid()
    {
        var curve = Source(new CurveChannel("L1", "V", [0, 20, 0]));
        var table = new CurvePointTable(curve);
        var page = table.ReadPage(0, 0).ToArray();
        table.Remove(0, page[1]);
        var edited = table.Build();
        Assert.Equal(new double[] { 0, 1 }, edited.Times);
        Assert.Equal(new double[] { 0, 0 }, edited.Channels[0].Samples);
    }
}

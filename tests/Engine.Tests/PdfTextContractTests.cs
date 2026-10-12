using Testumgebung.Persistence.Reports;

namespace Testumgebung.Engine.Tests;

public sealed class PdfTextContractTests
{
    [Theory]
    [InlineData("\r\n\t", "   ")]
    [InlineData("→←≤≥×σμ−Ω", "-><-<=>=×sµ-Ohm")]
    [InlineData("中文\u0080\u009f", "????")]
    [InlineData("€‚„…‘’“”•–—™", "€‚„…‘’“”•–—™")]
    [InlineData("äöüÄÖÜß±°µ", "äöüÄÖÜß±°µ")]
    [InlineData("", "")]
    public void Unsupported_characters_have_readable_winansi_fallbacks(string input, string expected)
        => Assert.Equal(expected, PdfText.Normalize(input));

    public static IEnumerable<object[]> Layouts()
    {
        foreach (var font in Enum.GetValues<PdfFont>())
            foreach (var text in new[] { "", "Text", "äöüáàâóòôúùûéèêß", "€ 中文 →", "A longer report heading", "one\ntwo\r\nthree", "singlelongwordwithoutspaces" })
                foreach (var width in new[] { 10d, 50, 200 })
                    yield return [font, text, width];
    }

    [Theory, MemberData(nameof(Layouts))]
    public void Text_layout_preserves_short_text_and_wraps_long_text(PdfFont font, string text, double width)
    {
        var measured = PdfText.Width(text, 10, font);
        Assert.True(measured >= 0);
        var fitted = PdfText.Fit(text, width, 10, font);
        if (measured <= width) Assert.Equal(text, fitted);
        else Assert.EndsWith("…", fitted);
        var lines = PdfText.Wrap(text, width, 10, font);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.DoesNotContain('\n', line));
        Assert.Equal(PdfText.Width(text, 10, PdfFont.Regular) * 1.06, PdfText.Width(text, 10, PdfFont.Bold), 8);
        Assert.Equal(text.Length * 6d, PdfText.Width(text, 10, PdfFont.Mono), 8);
    }
}

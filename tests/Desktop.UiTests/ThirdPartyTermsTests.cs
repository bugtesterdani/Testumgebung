using System.IO;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class ThirdPartyTermsTests
{
    [Theory]
    [InlineData("current", true)]
    [InlineData(" current\r\n", true)]
    [InlineData("previous", false)]
    [InlineData("", false)]
    public void ReceiptRequiresTheCurrentTermsHash(string contents, bool expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, contents);
            Assert.Equal(expected, ThirdPartyTerms.IsCurrentReceipt(path, "current"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingReceiptDoesNotGrantConsent()
    {
        var path = Path.GetTempFileName();
        File.Delete(path);
        Assert.False(ThirdPartyTerms.IsCurrentReceipt(path, "current"));
    }
}

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

    [Fact]
    public void ConsentIncludesOriginalContractsAndChangesWhenTheyChange()
    {
        var folder = Path.Combine(Path.GetTempPath(), "terms-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "licenses/third-party/microsoft"));
            File.WriteAllText(Path.Combine(folder, "THIRD-PARTY-TERMS.md"), "summary");
            var dotnet = Path.Combine(folder, "licenses/third-party/microsoft/dotnet-library.txt");
            File.WriteAllText(dotnet, "original-dotnet");
            File.WriteAllText(Path.Combine(folder, "licenses/third-party/microsoft/windows-sdk.txt"), "original-sdk");
            var first = ThirdPartyTerms.ReadConsentText(folder);
            Assert.Contains("original-dotnet", first);
            Assert.Contains("original-sdk", first);
            File.WriteAllText(dotnet, "changed-dotnet");
            Assert.NotEqual(first, ThirdPartyTerms.ReadConsentText(folder));
            Directory.CreateDirectory(Path.Combine(folder, "workers/python/runtime"));
            File.WriteAllText(Path.Combine(folder, "workers/python/runtime/python.exe"), "fixture");
            Assert.Throws<FileNotFoundException>(() => ThirdPartyTerms.ReadConsentText(folder));
            File.WriteAllText(Path.Combine(folder, "workers/python/runtime/LICENSE.txt"), "python-original");
            Assert.Contains("python-original", ThirdPartyTerms.ReadConsentText(folder));
            File.Delete(dotnet);
            Assert.Throws<FileNotFoundException>(() => ThirdPartyTerms.ReadConsentText(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

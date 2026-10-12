using Testumgebung.Domain.Code;
using Testumgebung.Persistence;

namespace Testumgebung.Engine.Tests;

public sealed class AssemblyValidationBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../library.dll")]
    [InlineData("folder\\library.dll")]
    [InlineData("stream:library.dll")]
    [InlineData("library.exe")]
    [InlineData("library.dll")]
    public void Invalid_assembly_names_and_payloads_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => ManagedAssemblyImport.Validate(new ManagedAssembly(name, [])));
        Assert.Throws<ArgumentException>(() => ManagedAssemblyImport.Validate(new ManagedAssembly(name, null!)));
        Assert.Throws<ArgumentException>(() => ManagedAssemblyImport.Validate(new ManagedAssembly(name, [1, 2, 3])));
    }
    [Fact]
    public void Real_managed_assembly_must_match_declared_filename_case_insensitively()
    {
        var assembly = ManagedAssemblyImport.Read(typeof(OrderHelper.Rules).Assembly.Location); ManagedAssemblyImport.Validate(assembly with { Name = assembly.Name.ToUpperInvariant() });
        Assert.Contains("Assemblynamen", Assert.Throws<ArgumentException>(() => ManagedAssemblyImport.Validate(assembly with { Name = "Different.dll" })).Message);
    }
}

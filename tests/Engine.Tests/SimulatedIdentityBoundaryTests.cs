using Testumgebung.Identity;

namespace Testumgebung.Engine.Tests;

public sealed class SimulatedIdentityBoundaryTests
{
    [Theory]
    [InlineData("  Ä Ö Ü ß .-_ 12 ! 漢  ", "aous.-_12@simuliert.local", "Ä Ö Ü ß .-_ 12 ! 漢")]
    [InlineData(" Worker@example.test ", "Worker@example.test", "Worker")]
    [InlineData(" A B ", "ab@simuliert.local", "A B")]
    public async Task Simulated_signin_normalizes_local_names_and_preserves_account_names(string input, string upn, string display)
    {
        var provider = new SimulatedIdentityProvider(() => input);
        var identity = await provider.SignInAsync(true, CancellationToken.None);
        Assert.Equal(upn, identity.Upn); Assert.Equal(display, identity.DisplayName); Assert.False(identity.IsConfirmed);
        Assert.Equal("Simuliert", identity.Source); Assert.Contains("keine Entra", provider.Name);
        await provider.SignOutAsync(identity, CancellationToken.None);
        Assert.Contains(upn, identity.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_simulated_signin_is_rejected(string input)
    {
        await Assert.ThrowsAsync<SignInException>(() => new SimulatedIdentityProvider(() => input).SignInAsync(false, CancellationToken.None));
    }
}

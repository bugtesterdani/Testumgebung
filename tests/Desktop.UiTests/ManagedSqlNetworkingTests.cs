using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Testumgebung.DatabaseAdapters;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class ManagedSqlNetworkingTests
{
    [Fact]
    public async Task SqlServerFactoryUsesManagedNetworkingWithoutNativeSni()
    {
        var factory = new DatabaseProviderRegistry().Factory(DatabaseProvider.SqlServer);
        Assert.Same(SqlClientFactory.Instance, factory);
        Assert.True(AppContext.TryGetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", out var enabled) && enabled);
        // Reserve a local port that deliberately does not accept connections.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        await using var connection = new SqlConnection($"Server=tcp:127.0.0.1,{port};User ID=probe;Password=probe;Encrypt=false;Connect Timeout=1;Pooling=false");
        await Assert.ThrowsAsync<SqlException>(() => connection.OpenAsync());
        Assert.DoesNotContain(System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>(),
            module => module.ModuleName.Contains("SqlClient.SNI", StringComparison.OrdinalIgnoreCase));
    }
}

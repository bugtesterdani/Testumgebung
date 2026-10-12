using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Desktop;

/// <summary>ADO.NET-Treiber der vier Datenbanksysteme (NuGet-Pakete der Anwendung).</summary>
internal sealed class DatabaseProviderRegistry : IDatabaseProviderRegistry
{
    public DbProviderFactory? Factory(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => Microsoft.Data.SqlClient.SqlClientFactory.Instance,
        DatabaseProvider.PostgreSql => Npgsql.NpgsqlFactory.Instance,
        DatabaseProvider.MySql => MySqlConnector.MySqlConnectorFactory.Instance,
        DatabaseProvider.Sqlite => Microsoft.Data.Sqlite.SqliteFactory.Instance,
        _ => null,
    };
}

/// <summary>Windows-DPAPI: Geheimnisse nur für den angemeldeten Benutzer auf diesem Rechner lesbar.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Testumgebung.Einstellungen.v1");

    public string Protect(string plain)
        => Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return PlainSecretProtector.Instance.Unprotect(stored);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

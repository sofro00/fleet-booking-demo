using System.Text.RegularExpressions;
using FleetBooking.Api.Data;
using FleetBooking.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FleetBooking.Api.Services;

public static partial class DemoSafety
{
    public const int Port = DemoEndpoint.Port;
    public const string DatabaseName = "FleetBookingDemo";
    private const string MarkerName = "FleetBookingPortfolioDemo";
    private const string MarkerValue = "synthetic-data-v1";

    public static void ValidateConnection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Demo connection is missing.");
        var connection = new SqlConnectionStringBuilder(value);
        var localServers = new[] { @".\SQLEXPRESS", @"localhost\SQLEXPRESS", @"(localdb)\MSSQLLocalDB", ".", "localhost" };
        if (!localServers.Contains(connection.DataSource, StringComparer.OrdinalIgnoreCase) ||
            !connection.InitialCatalog.Equals(DatabaseName, StringComparison.Ordinal) ||
            !connection.IntegratedSecurity || connection.UserID.Length != 0 || connection.Password.Length != 0 ||
            connection.AttachDBFilename.Length != 0 || connection.FailoverPartner.Length != 0 ||
            connection.UserInstance || connection.ApplicationIntent != ApplicationIntent.ReadWrite)
            throw new InvalidOperationException("Only the local FleetBookingDemo database with Windows authentication is allowed.");
    }

    public static void ValidateConfiguration(IConfiguration config)
    {
        ValidateConnection(config.GetConnectionString("FleetBooking"));
        if (config.GetValue<bool>("PublicAccess:Enabled") || config.GetValue<bool>("Backup:Enabled") ||
            config.GetSection("Kestrel").GetChildren().Any() ||
            !string.IsNullOrEmpty(config["Urls"]) || !string.IsNullOrEmpty(config["service-settings"]) ||
            !string.IsNullOrEmpty(config["Server:CertificateThumbprint"]))
            throw new InvalidOperationException("External listeners, service configuration and production backups are disabled in this demo.");
    }

    public static bool IsDemoEmail(string? value) => value is not null &&
        DemoEmail().IsMatch(value.Trim());
    public static bool IsDemoRegistration(string? value) => value is not null &&
        DemoRegistration().IsMatch(value.Trim());

    [GeneratedRegex(@"\A[a-zA-Z0-9._+-]+@example\.com\z", RegexOptions.CultureInvariant)]
    private static partial Regex DemoEmail();
    [GeneratedRegex(@"\ADEMO-[0-9]{3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DemoRegistration();

    // Never migrate an existing, unrecognised database, even if its name matches.
    public static async Task PrepareDatabaseAsync(AppDbContext db)
    {
        var value = db.Database.GetConnectionString();
        ValidateConnection(value);
        var masterSettings = new SqlConnectionStringBuilder(value) { InitialCatalog = "master" };
        await using var master = new SqlConnection(masterSettings.ConnectionString);
        await master.OpenAsync();
        await using var check = master.CreateCommand();
        check.CommandText = "SELECT DB_ID(@name)";
        check.Parameters.AddWithValue("@name", DatabaseName);
        var exists = await check.ExecuteScalarAsync() is not DBNull and not null;
        if (!exists)
        {
            await using var create = master.CreateCommand();
            create.CommandText = "CREATE DATABASE [FleetBookingDemo]";
            await create.ExecuteNonQueryAsync();
        }
        await using var demo = new SqlConnection(value);
        await demo.OpenAsync();
        if (!exists)
        {
            await using var mark = demo.CreateCommand();
            mark.CommandText = "EXEC sys.sp_addextendedproperty @name=@name, @value=@value";
            mark.Parameters.AddWithValue("@name", MarkerName);
            mark.Parameters.AddWithValue("@value", MarkerValue);
            await mark.ExecuteNonQueryAsync();
        }
        await using var verify = demo.CreateCommand();
        verify.CommandText = "SELECT CAST(value AS nvarchar(100)) FROM sys.extended_properties WHERE class=0 AND name=@name";
        verify.Parameters.AddWithValue("@name", MarkerName);
        if (await verify.ExecuteScalarAsync() as string != MarkerValue)
            throw new InvalidOperationException("An existing database has no demo marker. It will not be modified.");
    }
}

using System.Text.Json;
using FleetBooking.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FleetBooking.Tests;

public sealed class BackupFailureTests
{
    [Fact]
    public async Task Failed_backup_preserves_last_success_and_failure_survives_restart()
    {
        var root = Directory.CreateTempSubdirectory("FleetBackupTest-").FullName;
        try
        {
            var lastSuccess = DateTime.UtcNow.AddDays(-3);
            var previousFile = Path.Combine(root, "previous.bak");
            File.WriteAllBytes(previousFile, [1]); // Fixture: an existing earlier backup.
            File.WriteAllText(Path.Combine(root, "backup-state.json"), JsonSerializer.Serialize(new
            {
                LastAttemptUtc = lastSuccess, LastSuccessUtc = lastSuccess, Failed = false, File = previousFile
            }));
            var options = Options.Create(new BackupOptions { Enabled = true, Directory = root, StateDirectory = root });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FleetBooking"] = "Server=tcp:127.0.0.1,1;Database=unused;User ID=invalid;Password=invalid;Connect Timeout=1;ConnectRetryCount=0;Encrypt=False"
            }).Build();
            using (var worker = new DatabaseBackupService(options, configuration, NullLogger<DatabaseBackupService>.Instance))
            {
                await worker.StartAsync(default);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((!worker.Status.LastAttemptFailed || worker.Status.Running) && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                await worker.StopAsync(default);
                Assert.True(worker.Status.LastAttemptFailed);
                Assert.Equal(lastSuccess, worker.Status.LastSuccessUtc);
                Assert.DoesNotContain("Password", worker.Status.Message ?? "");
            }
            using var restarted = new DatabaseBackupService(options, configuration, NullLogger<DatabaseBackupService>.Instance);
            Assert.True(restarted.Status.LastAttemptFailed);
            Assert.Equal(lastSuccess, restarted.Status.LastSuccessUtc);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

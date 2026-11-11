using System.Globalization;
using System.Text.Json;
using FleetBooking.Shared;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace FleetBooking.Api.Services;

public sealed class BackupOptions
{
    public bool Enabled { get; set; }
    public string DailyTime { get; set; } = "02:00";
    public string Directory { get; set; } = "";
    public string StateDirectory { get; set; } = "";
}

public static class BackupSchedule
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Podgorica");

    public static bool IsDue(DateTime utcNow, DateTime? lastSuccessUtc, TimeOnly dailyTime)
    {
        if (lastSuccessUtc is null) return true; // Initial installation and missing state: make a copy immediately.
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), Zone);
        var last = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(lastSuccessUtc.Value, DateTimeKind.Utc), Zone);
        return last.Date < now.Date && TimeOnly.FromDateTime(now) >= dailyTime;
    }
}

// SQL Express has no SQL Agent. This worker runs under the API Windows service, including before login.
public sealed class DatabaseBackupService : BackgroundService
{
    private readonly BackupOptions _options;
    private readonly string _connectionString;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly object _sync = new();
    private BackupState _state = new(null, null, false, null);
    private bool _running;
    private readonly TimeOnly _dailyTime;

    private sealed record BackupState(DateTime? LastAttemptUtc, DateTime? LastSuccessUtc, bool Failed, string? File);

    public DatabaseBackupService(IOptions<BackupOptions> options, IConfiguration configuration, ILogger<DatabaseBackupService> logger)
    {
        _options = options.Value;
        _connectionString = configuration.GetConnectionString("FleetBooking")!;
        _logger = logger;
        if (!TimeOnly.TryParseExact(_options.DailyTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _dailyTime))
            throw new InvalidOperationException("Backup:DailyTime mora biti u formatu HH:mm.");
        if (_options.Enabled && (!Path.IsPathFullyQualified(_options.Directory) || !Path.IsPathFullyQualified(_options.StateDirectory)))
            throw new InvalidOperationException("Backup:Directory i Backup:StateDirectory moraju biti apsolutne putanje na serveru.");
        if (_options.Enabled)
        {
            System.IO.Directory.CreateDirectory(_options.StateDirectory);
            try
            {
                if (File.Exists(StatePath))
                    _state = JsonSerializer.Deserialize<BackupState>(File.ReadAllText(StatePath)) ?? _state;
                if (_state.File is not null && !File.Exists(_state.File))
                    _state = _state with { LastSuccessUtc = null, File = null };
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Nije moguće učitati stanje rezervne kopije."); }
        }
    }

    private string StatePath => Path.Combine(_options.StateDirectory, "backup-state.json");
    public BackupStatusDto Status
    {
        get
        {
            lock (_sync)
                return new(_options.Enabled, _options.DailyTime, "Europe/Podgorica", _running,
                    _state.LastAttemptUtc, _state.LastSuccessUtc, _state.Failed,
                    !_options.Enabled ? "Automatske kopije nijesu uključene na serveru." :
                    _state.Failed ? "Posljednji pokušaj nije uspio. Obratite se IT administratoru; detalji su u serverskom logu." :
                    _running ? "Izrada kopije je u toku." :
                    _state.LastSuccessUtc is null ? "Čeka se prva uspješna kopija." : "Kopija je uspješno napravljena uz SQL CHECKSUM.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var state = Status;
            var retryReady = !state.LastAttemptFailed || state.LastAttemptUtc <= DateTime.UtcNow.AddMinutes(-30);
            if (retryReady && BackupSchedule.IsDue(DateTime.UtcNow, state.LastSuccessUtc, _dailyTime))
                await MakeBackupAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task MakeBackupAsync(CancellationToken ct)
    {
        lock (_sync) _running = true;
        var attempted = DateTime.UtcNow;
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            // Session-owned SQL lock prevents two API instances from backing up simultaneously.
            await using var acquire = connection.CreateCommand();
            acquire.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'FleetDemo:DailyBackup', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @result;";
            if (Convert.ToInt32(await acquire.ExecuteScalarAsync(ct)) < 0) return;
            var database = connection.Database.Replace("]", "]]");
            var name = $"FleetBooking-{attempted:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            var path = Path.Combine(_options.Directory, name);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 900;
            command.CommandText = $"BACKUP DATABASE [{database}] TO DISK=@file WITH COPY_ONLY, CHECKSUM, STOP_ON_ERROR;";
            command.Parameters.AddWithValue("@file", path);
            await command.ExecuteNonQueryAsync(ct);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new IOException("SQL kopija nije pronađena na disku.");
            SaveState(new(attempted, DateTime.UtcNow, false, path));
            _logger.LogInformation("Rezervna kopija završena: {BackupFile}", path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dnevna rezervna kopija nije uspjela. Ponovni pokušaj za 30 minuta.");
            BackupState failed;
            lock (_sync) failed = _state with { LastAttemptUtc = attempted, Failed = true };
            try { SaveState(failed); }
            catch (Exception stateError)
            {
                lock (_sync) _state = failed;
                _logger.LogError(stateError, "Stanje rezervne kopije nije sačuvano.");
            }
        }
        finally { lock (_sync) _running = false; }
    }

    private void SaveState(BackupState state)
    {
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, StatePath, overwrite: true);
            lock (_sync) _state = state;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

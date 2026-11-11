using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Api.Services;
using FleetBooking.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FleetBooking.Tests;

public sealed class OperationsTests : IDisposable
{
    private const string Password = "Test-only!123";
    private readonly ApiFactory _factory = new();
    private readonly DateTime _from = new(2030, 6, 10, 8, 0, 0, DateTimeKind.Utc);
    private readonly Guid _assignedId;

    public OperationsTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational()) db.Database.Migrate();
        var hasher = new PasswordHasher<AppUser>();
        var employee = new AppUser { FullName = "Test Zaposleni", Email = "employee@test.invalid", PasswordHash = "", MustChangePassword = false };
        var admin = new AppUser { FullName = "Test Administrator", Email = "admin@test.invalid", Role = Roles.Administrator, PasswordHash = "", MustChangePassword = false };
        employee.PasswordHash = hasher.HashPassword(employee, Password);
        admin.PasswordHash = hasher.HashPassword(admin, Password);
        Vehicle Vehicle(string registration) => new() { MakeModel = registration, Registration = registration, SeatCount = 5, FuelType = "Dizel", PickupLocation = "Podgorica" };
        var free = Vehicle("FREE"); var reserved = Vehicle("RESERVED"); var service = Vehicle("SERVICE");
        var assigned = Vehicle("ASSIGNED"); assigned.AssignedUserId = admin.Id; _assignedId = assigned.Id;
        var cancelled = Vehicle("CANCELLED"); var boundary = Vehicle("BOUNDARY");
        db.AddRange(employee, admin, free, reserved, service, assigned, cancelled, boundary);
        Reservation Reserve(Vehicle vehicle, DateTime start, DateTime end, string status = ReservationStatuses.Active) =>
            new() { VehicleId = vehicle.Id, UserId = admin.Id, StartUtc = start, EndUtc = end, Status = status,
                Purpose = "Privatni detalji putovanja", Destination = "Privatno", DepartureLocation = "Podgorica", ContactPhone = "067000000" };
        db.AddRange(Reserve(reserved, _from, _from.AddHours(2)),
            Reserve(cancelled, _from, _from.AddHours(2), ReservationStatuses.Cancelled),
            Reserve(boundary, _from.AddHours(-1), _from));
        db.Unavailabilities.Add(new() { VehicleId = service.Id, StartUtc = _from, EndUtc = _from.AddHours(2), Reason = "Servis" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Backup_status_requires_admin_and_health_is_public()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/system/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        using var employee = await LoginAsync("employee@test.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/system/backup")).StatusCode);
        using var admin = await LoginAsync("admin@test.invalid");
        var status = await admin.GetFromJsonAsync<BackupStatusDto>("/api/system/backup");
        Assert.NotNull(status);
        Assert.False(status.Enabled);
        Assert.Null(status.LastSuccessUtc);
    }

    [Fact]
    public async Task Password_without_digits_works_for_create_reset_change_and_login()
    {
        using var admin = await LoginAsync("admin@test.invalid");
        const string email = "no-digits@example.com";
        var create = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(
            "Test Bez Brojeva", email, Roles.Employee, "ProbnaLozinka!", null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var user = (await create.Content.ReadFromJsonAsync<UserDto>())!;
        using (var firstLogin = await LoginAsync(email, "ProbnaLozinka!"))
        {
            var session = await firstLogin.GetFromJsonAsync<CurrentUserDto>("/api/auth/session");
            Assert.True(session!.MustChangePassword);
        }
        var reset = await admin.PostAsJsonAsync($"/api/users/{user.Id}/reset-password", new ResetPasswordRequest("DrugaLozinka!"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using (var employee = await LoginAsync(email, "DrugaLozinka!"))
        {
            var change = await employee.PostAsJsonAsync("/api/auth/change-password",
                new ChangePasswordRequest("DrugaLozinka!", "KonacnaLozinka!"));
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }
        using var finalLogin = await LoginAsync(email, "KonacnaLozinka!");
        Assert.False((await finalLogin.GetFromJsonAsync<CurrentUserDto>("/api/auth/session"))!.MustChangePassword);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "DrugaLozinka!"))).StatusCode);
    }

    [Fact]
    public async Task Api_accepts_new_and_updated_reservation_without_telephone()
    {
        using var client = await LoginAsync("employee@test.invalid");
        var vehicles = (await client.GetFromJsonAsync<List<VehicleDto>>("/api/vehicles?includeInactive=false"))!;
        var vehicle = vehicles.Single(x => x.Registration == "FREE");
        var start = DateTime.UtcNow.AddDays(2);
        var request = new SaveReservationRequest(vehicle.Id, start, start.AddHours(2),
            "Terenski rad", "Zeta", "Podgorica", null, null, null);
        var response = await client.PostAsJsonAsync("/api/reservations", new
        {
            request.VehicleId, request.StartUtc, request.EndUtc, request.Purpose, request.Destination,
            request.DepartureLocation, request.PassengerCount, request.Note // ContactPhone deliberately omitted.
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ReservationDto>())!;
        Assert.Equal("", created.ContactPhone);
        var update = await client.PutAsJsonAsync($"/api/reservations/{created.Id}", request with { Purpose = "Obilazak radnje", ContactPhone = "" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("", (await update.Content.ReadFromJsonAsync<ReservationDto>())!.ContactPhone);
    }

    [Fact]
    public async Task Vehicle_statuses_respect_overlap_cancellations_boundaries_and_assignment()
    {
        using var client = await LoginAsync("employee@test.invalid");
        var url = $"/api/vehicles?includeInactive=false&availableOnly=false&fromUtc={Uri.EscapeDataString(_from.ToString("O"))}&toUtc={Uri.EscapeDataString(_from.AddHours(1).ToString("O"))}";
        var vehicles = (await client.GetFromJsonAsync<List<VehicleDto>>(url))!;
        Assert.Equal(VehicleStates.Free, vehicles.Single(x => x.Registration == "FREE").StatusCode);
        Assert.Equal(VehicleStates.Reserved, vehicles.Single(x => x.Registration == "RESERVED").StatusCode);
        Assert.Equal(VehicleStates.Service, vehicles.Single(x => x.Registration == "SERVICE").StatusCode);
        Assert.Equal(VehicleStates.Assigned, vehicles.Single(x => x.Id == _assignedId).StatusCode);
        Assert.Equal("Test Administrator", vehicles.Single(x => x.Id == _assignedId).AssignedUserName);
        Assert.Equal(VehicleStates.Free, vehicles.Single(x => x.Registration == "CANCELLED").StatusCode);
        Assert.Equal(VehicleStates.Free, vehicles.Single(x => x.Registration == "BOUNDARY").StatusCode);
        var json = await client.GetStringAsync(url);
        Assert.DoesNotContain("Privatni detalji", json);
        Assert.DoesNotContain("067000000", json);
        var freeOnly = (await client.GetFromJsonAsync<List<VehicleDto>>(url.Replace("availableOnly=false", "availableOnly=true")))!;
        Assert.Equal(3, freeOnly.Count);
        Assert.DoesNotContain(freeOnly, x => x.Id == _assignedId);
    }

    [Theory]
    [InlineData("2026-10-19T00:01:00Z", null, true)] // Initial installation.
    [InlineData("2026-10-19T00:01:00Z", "2026-10-18T00:00:00Z", true)] // 02:01 Podgorica.
    [InlineData("2026-10-18T23:00:00Z", "2026-10-18T00:00:00Z", false)] // Before today's 02:00.
    [InlineData("2026-10-19T10:00:00Z", "2026-10-19T00:01:00Z", false)] // Only once per local day.
    [InlineData("2026-03-29T01:01:00Z", "2026-03-28T01:00:00Z", true)] // Spring DST skips 02:00.
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25T00:15:00Z", false)] // Autumn repeated hour.
    public void Daily_backup_uses_Podgorica_date_and_catches_up_after_downtime(string now, string? last, bool due)
    {
        var utc = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        DateTime? success = last is null ? null : DateTime.Parse(last, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(due, BackupSchedule.IsDue(utc, success, new TimeOnly(2, 0)));
    }

    [Fact]
    public void Assignment_and_unavailability_status_precedence_are_explicit()
    {
        var user = Guid.NewGuid();
        Assert.Equal(VehicleStates.Inactive, VehicleStates.Code(false, true, true, user));
        Assert.Equal(VehicleStates.Service, VehicleStates.Code(true, true, true, user));
        Assert.Equal(VehicleStates.Assigned, VehicleStates.Code(true, false, true, user));
        Assert.Contains("Ime Prezime", VehicleStates.Label(VehicleStates.Assigned, "Ime Prezime"));
    }

    private async Task<HttpClient> LoginAsync(string email, string password = Password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }
    public void Dispose()
    {
        if (_factory.UsesSql)
        {
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeleted();
        }
        _factory.Dispose();
    }

    internal class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _database = Guid.NewGuid().ToString("N");
        public bool UsesSql => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLEET_SQL_TEST_CONNECTION"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            builder.UseSetting("Jwt:Secret", "test-only-0123456789abcdef0123456789abcdef");
            builder.UseSetting("Jwt:Issuer", "FleetBooking.Api");
            builder.UseSetting("Jwt:Audience", "FleetBooking.Client");
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Demo:Seed", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-only-0123456789abcdef0123456789abcdef",
                ["Jwt:Issuer"] = "FleetBooking.Api", ["Jwt:Audience"] = "FleetBooking.Client",
                ["ConnectionStrings:FleetBooking"] = "Server=.\\SQLEXPRESS;Database=FleetBookingDemo;Integrated Security=True;TrustServerCertificate=True",
                ["Demo:Seed"] = "false", ["Database:AutoMigrate"] = "false", ["Backup:Enabled"] = "false", ["Server:CertificateThumbprint"] = ""
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options =>
                {
                    if (UsesSql)
                    {
                        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FLEET_SQL_TEST_CONNECTION"))
                        {
                            InitialCatalog = "FleetBookingTest_" + _database
                        };
                        options.UseSqlServer(connection.ConnectionString);
                    }
                    else options.UseInMemoryDatabase(_database);
                });
            });
        }
    }
}

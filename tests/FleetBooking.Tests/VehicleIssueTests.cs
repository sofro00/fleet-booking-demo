using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FleetBooking.Tests;

public sealed class VehicleIssueTests : IDisposable
{
    private readonly OperationsTests.ApiFactory _factory = new();
    private const string Password = "TestOnly!123";
    private readonly Guid _employeeId, _adminId, _vehicleId, _inactiveId;
    public VehicleIssueTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational()) db.Database.Migrate();
        var hasher = new PasswordHasher<AppUser>();
        AppUser User(string email, string role = Roles.Employee)
        {
            var value = new AppUser { FullName = email, Email = email, Role = role, PasswordHash = "", MustChangePassword = false };
            value.PasswordHash = hasher.HashPassword(value, Password); return value;
        }
        var employee = User("first@test.invalid"); _employeeId = employee.Id;
        var admin = User("admin@test.invalid", Roles.Administrator); _adminId = admin.Id;
        var vehicle = new Vehicle { MakeModel = "Test Renault", Registration = "TEST", SeatCount = 5,
            FuelType = "Benzin", PickupLocation = "Podgorica", AssignedUserId = admin.Id }; _vehicleId = vehicle.Id;
        var inactive = new Vehicle { MakeModel = "Neaktivno", Registration = "OFF", SeatCount = 5, FuelType = "Dizel",
            PickupLocation = "Podgorica", IsActive = false }; _inactiveId = inactive.Id;
        db.AddRange(employee, admin, User("second@test.invalid"), vehicle, inactive); db.SaveChanges();
    }
    private async Task<HttpClient> Login(string email)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return client;
    }
    private async Task<VehicleIssueDto> Create(HttpClient client, string text = "Nedostaje prva pomoć.")
    {
        var response = await client.PostAsJsonAsync("/api/vehicle-issues", new CreateVehicleIssueRequest(_vehicleId, VehicleIssueCategories.MissingEquipment, text));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VehicleIssueDto>())!;
    }
    [Fact]
    public async Task Anonymous_cannot_read_or_create_reports()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/vehicle-issues")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/vehicle-issues",
            new CreateVehicleIssueRequest(_vehicleId, VehicleIssueCategories.Fault, "Problem"))).StatusCode);
    }
    [Fact]
    public async Task Reporter_status_and_timestamps_are_server_controlled_and_assignment_does_not_prevent_reporting()
    {
        using var client = await Login("first@test.invalid");
        var before = DateTime.UtcNow.AddSeconds(-1);
        var response = await client.PostAsJsonAsync("/api/vehicle-issues", new { vehicleId = _vehicleId,
            category = VehicleIssueCategories.Fault, description = "  Ne radi svijetlo.  ",
            reporterUserId = _adminId, status = VehicleIssueStatuses.Resolved, adminNote = "Spoof", createdUtc = "2000-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<VehicleIssueDto>())!;
        Assert.Equal(_employeeId, item.ReporterUserId); Assert.Equal(VehicleIssueStatuses.Open, item.Status);
        Assert.Equal("Ne radi svijetlo.", item.Description); Assert.Null(item.AdminNote); Assert.Null(item.ResolvedUtc);
        Assert.InRange(item.CreatedUtc, before, DateTime.UtcNow.AddSeconds(1)); Assert.Equal(1, item.Version);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(_adminId, (await db.Vehicles.FindAsync(_vehicleId))!.AssignedUserId);
        Assert.Empty(db.Unavailabilities); Assert.Empty(db.Reservations);
        Assert.Contains(db.AuditLogs, a => a.EntityType == "VehicleIssue" && a.EntityId == item.Id.ToString() && a.ActorUserId == _employeeId);
    }
    [Fact]
    public async Task Employees_only_see_own_reports_admin_sees_all_and_employee_cannot_process()
    {
        using var first = await Login("first@test.invalid"); using var second = await Login("second@test.invalid");
        using var admin = await Login("admin@test.invalid");
        var mine = await Create(first); var other = await Create(second, "Nedostaje dizalica.");
        Assert.Single((await first.GetFromJsonAsync<List<VehicleIssueDto>>("/api/vehicle-issues"))!);
        Assert.Equal(2, (await admin.GetFromJsonAsync<List<VehicleIssueDto>>("/api/vehicle-issues"))!.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync("/api/vehicle-issues/" + other.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/vehicle-issues/" + mine.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/vehicle-issues/" + other.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PutAsJsonAsync("/api/vehicle-issues/" + mine.Id,
            new UpdateVehicleIssueRequest(VehicleIssueStatuses.Resolved, "Pokušaj", mine.Version))).StatusCode);
    }
    [Theory]
    [InlineData(null, "Opis")]
    [InlineData("Unknown", "Opis")]
    [InlineData("Fault", null)]
    [InlineData("Fault", "  ")]
    public async Task Invalid_required_fields_are_rejected(string? category, string? description)
    {
        using var client = await Login("first@test.invalid");
        var response = await client.PostAsJsonAsync("/api/vehicle-issues", new CreateVehicleIssueRequest(_vehicleId, category!, description!));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task Inactive_missing_vehicle_and_oversized_description_are_rejected()
    {
        using var client = await Login("first@test.invalid");
        foreach (var id in new[] { Guid.Empty, Guid.NewGuid(), _inactiveId })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/vehicle-issues",
                new CreateVehicleIssueRequest(id, VehicleIssueCategories.Fault, "Opis"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/vehicle-issues",
            new CreateVehicleIssueRequest(_vehicleId, VehicleIssueCategories.Other, new string('x', 4001)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/vehicle-issues",
            new CreateVehicleIssueRequest(_vehicleId, VehicleIssueCategories.Other, new string('x', 4000)))).StatusCode);
    }
    [Fact]
    public async Task Admin_can_process_resolve_and_reopen_without_changing_original_report()
    {
        using var employee = await Login("first@test.invalid"); using var admin = await Login("admin@test.invalid");
        var item = await Create(employee);
        var path = "/api/vehicle-issues/" + item.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path,
            new UpdateVehicleIssueRequest(VehicleIssueStatuses.Resolved, " ", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path,
            new UpdateVehicleIssueRequest("Unknown", null, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path,
            new UpdateVehicleIssueRequest(VehicleIssueStatuses.InProgress, new string('x', 4001), 1))).StatusCode);
        var processing = await admin.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.InProgress, "Naručena oprema.", 1));
        Assert.Equal(HttpStatusCode.OK, processing.StatusCode);
        Assert.Equal(2, (await processing.Content.ReadFromJsonAsync<VehicleIssueDto>())!.Version);
        var resolved = await admin.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.Resolved, "  Oprema dopunjena.  ", 2));
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var result = (await resolved.Content.ReadFromJsonAsync<VehicleIssueDto>())!;
        Assert.NotNull(result.ResolvedUtc); Assert.Equal("Oprema dopunjena.", result.AdminNote);
        Assert.Equal(item.Description, result.Description); Assert.Equal(item.CreatedUtc, result.CreatedUtc);
        Assert.Equal(item.ReporterUserId, result.ReporterUserId); Assert.Equal("admin@test.invalid", result.UpdatedByName);
        var visibleToReporter = await employee.GetFromJsonAsync<VehicleIssueDto>(path);
        Assert.Equal(VehicleIssueStatuses.Resolved, visibleToReporter!.Status); Assert.Equal(result.AdminNote, visibleToReporter.AdminNote);
        var reopened = await admin.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.Open, "Potrebna dopuna.", 3));
        Assert.Null((await reopened.Content.ReadFromJsonAsync<VehicleIssueDto>())!.ResolvedUtc);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.CountAsync(x => x.EntityId == item.Id.ToString()));
    }
    [Fact]
    public async Task Stale_update_is_rejected_without_overwriting_note()
    {
        using var employee = await Login("first@test.invalid"); using var admin = await Login("admin@test.invalid");
        var item = await Create(employee); var path = "/api/vehicle-issues/" + item.Id;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.InProgress, "Prva obrada", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.Resolved, "Zastarjela izmjena", 1))).StatusCode);
        Assert.Equal("Prva obrada", (await admin.GetFromJsonAsync<VehicleIssueDto>(path))!.AdminNote);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/vehicle-issues/" + Guid.NewGuid(),
            new UpdateVehicleIssueRequest(VehicleIssueStatuses.InProgress, "", 1))).StatusCode);
    }
    [Fact]
    public async Task Concurrent_processing_has_exactly_one_winner()
    {
        using var employee = await Login("first@test.invalid"); using var first = await Login("admin@test.invalid"); using var second = await Login("admin@test.invalid");
        var item = await Create(employee); var path = "/api/vehicle-issues/" + item.Id;
        var results = await Task.WhenAll(first.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.InProgress, "A", 1)),
            second.PutAsJsonAsync(path, new UpdateVehicleIssueRequest(VehicleIssueStatuses.InProgress, "B", 1)));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
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
}

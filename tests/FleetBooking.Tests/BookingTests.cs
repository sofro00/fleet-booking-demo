using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Api.Services;
using FleetBooking.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace FleetBooking.Tests;

public sealed class BookingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" +00000000000 ")]
    public async Task Telephone_is_optional_on_create_and_update(string? phone)
    {
        var database = Guid.NewGuid().ToString();
        var (user, vehicle) = await SeedAsync(database);
        await using var db = Context(database);
        var service = new BookingService(db);
        var request = Request(vehicle.Id) with { ContactPhone = phone };
        var created = await service.CreateAsync(user.Id, request, user.FullName, default);
        Assert.Equal(phone?.Trim() ?? "", created.ContactPhone);
        var updated = await service.UpdateAsync(created.Id, user.Id, false,
            request with { ContactPhone = null, Purpose = "Obilazak radnje" }, user.FullName, default);
        Assert.Equal("", updated.ContactPhone);
        Assert.Equal("Obilazak radnje", updated.Purpose);
    }

    [Fact]
    public void Reservation_choices_contain_all_municipalities_and_preserve_historical_free_text()
    {
        Assert.Equal(25, ReservationOptions.Municipalities.Count);
        Assert.Equal(25, ReservationOptions.Municipalities.Distinct().Count());
        Assert.Contains("Tuzi", ReservationOptions.Municipalities);
        Assert.Contains("Zeta", ReservationOptions.Municipalities);
        Assert.Contains(ReservationOptions.DefaultDeparture, ReservationOptions.Municipalities);
        Assert.Contains("Terenski rad", ReservationOptions.Purposes);
        Assert.Contains("Prijem robe", ReservationOptions.Purposes);
        Assert.Contains("Obilazak radnje", ReservationOptions.Purposes);
        Assert.Contains("Ostalo", ReservationOptions.Purposes);
        Assert.Contains("Ranije unesena lokacija", ReservationOptions.WithExisting(ReservationOptions.Municipalities, "Ranije unesena lokacija"));
        Assert.Equal(25, ReservationOptions.WithExisting(ReservationOptions.Municipalities, "Podgorica").Count);
    }

    [Fact]
    public void Overlap_rule_allows_touching_boundaries()
    {
        var start = DateTime.UtcNow.AddDays(2);
        Assert.False(BookingRules.Overlaps(start, start.AddHours(2), start.AddHours(2), start.AddHours(3)));
        Assert.True(BookingRules.Overlaps(start, start.AddHours(2), start.AddHours(1), start.AddHours(3)));
    }

    [Fact]
    public void Employee_cannot_modify_another_users_reservation_but_admin_can()
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var future = DateTime.UtcNow.AddDays(1);
        Assert.Throws<UnauthorizedAccessException>(() => BookingRules.EnsureCanModify(owner, other, false, future));
        BookingRules.EnsureCanModify(owner, other, true, future);
    }

    [Fact]
    public async Task Cancelled_reservation_is_kept_but_no_longer_blocks_period()
    {
        var database = Guid.NewGuid().ToString(); var (user, vehicle) = await SeedAsync(database);
        await using var db1 = Context(database); var service1 = new BookingService(db1); var request = Request(vehicle.Id);
        var first = await service1.CreateAsync(user.Id, request, user.FullName, default);
        await service1.CancelAsync(first.Id, user.Id, false, user.FullName, "Put je otkazan", default);
        Assert.Equal(ReservationStatuses.Cancelled, (await db1.Reservations.FindAsync(first.Id))!.Status);
        await using var db2 = Context(database); var second = await new BookingService(db2).CreateAsync(user.Id, request, user.FullName, default);
        Assert.Equal(ReservationStatuses.Active, second.Status);
    }

    [Fact]
    public async Task Service_period_conflicts_with_reservation()
    {
        var database = Guid.NewGuid().ToString(); var (user, vehicle) = await SeedAsync(database); var request = Request(vehicle.Id);
        await using var db = Context(database); var service = new BookingService(db);
        await service.CreateAsync(user.Id, request, user.FullName, default);
        await Assert.ThrowsAsync<RuleException>(() => service.AddUnavailabilityAsync(user.Id, user.FullName, new(vehicle.Id, request.StartUtc.AddMinutes(30), request.EndUtc.AddMinutes(30), "Redovni servis"), default));
    }

    [Fact]
    public async Task Two_simultaneous_attempts_produce_exactly_one_reservation()
    {
        var database = Guid.NewGuid().ToString(); var (user, vehicle) = await SeedAsync(database); var request = Request(vehicle.Id);
        async Task<bool> TryBook()
        {
            await using var db = Context(database);
            try { await new BookingService(db).CreateAsync(user.Id, request, user.FullName, default); return true; }
            catch (RuleException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(TryBook), Task.Run(TryBook));
        Assert.Single(results, x => x);
        await using var verify = Context(database); Assert.Equal(1, await verify.Reservations.CountAsync(x => x.Status == ReservationStatuses.Active));
    }

    [Fact]
    public async Task Assigned_vehicle_can_only_be_reserved_by_assigned_user_and_error_names_them()
    {
        var database = Guid.NewGuid().ToString();
        var assignedUser = new AppUser { FullName = "Marko Marković", Email = $"{Guid.NewGuid()}@example.com", PasswordHash = "test" };
        var otherUser = new AppUser { FullName = "Drugi Korisnik", Email = $"{Guid.NewGuid()}@example.com", PasswordHash = "test" };
        var vehicle = new Vehicle { MakeModel = "Zaduženo vozilo", Registration = Guid.NewGuid().ToString("N")[..8], SeatCount = 5, FuelType = "Dizel", PickupLocation = "Podgorica", AssignedUserId = assignedUser.Id };
        await using (var seed = Context(database)) { seed.AddRange(assignedUser, otherUser, vehicle); await seed.SaveChangesAsync(); }

        await using (var blocked = Context(database))
        {
            var error = await Assert.ThrowsAsync<RuleException>(() => new BookingService(blocked).CreateAsync(otherUser.Id, Request(vehicle.Id), otherUser.FullName, default));
            Assert.Contains(assignedUser.FullName, error.Message);
        }
        await using var allowed = Context(database);
        var reservation = await new BookingService(allowed).CreateAsync(assignedUser.Id, Request(vehicle.Id), assignedUser.FullName, default);
        Assert.Equal(assignedUser.Id, reservation.UserId);
    }

    [Fact]
    public async Task Vehicle_cannot_be_assigned_while_another_user_has_a_future_reservation()
    {
        var database = Guid.NewGuid().ToString(); var (reservationOwner, vehicle) = await SeedAsync(database);
        var assignedUser = new AppUser { FullName = "Zaduženi Korisnik", Email = $"{Guid.NewGuid()}@example.com", PasswordHash = "test" };
        await using (var addUser = Context(database)) { addUser.Users.Add(assignedUser); await addUser.SaveChangesAsync(); }
        await using (var reserve = Context(database))
            await new BookingService(reserve).CreateAsync(reservationOwner.Id, Request(vehicle.Id), reservationOwner.FullName, default);

        await using var assign = Context(database);
        await Assert.ThrowsAsync<RuleException>(() => new BookingService(assign).AssignVehicleAsync(vehicle.Id, assignedUser.Id, Guid.NewGuid(), "Administrator", default));
    }

    [Fact]
    public void Initial_sql_server_migration_is_discoverable()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=(local);Database=unused;Trusted_Connection=True;TrustServerCertificate=True").Options);
        Assert.Contains(db.Database.GetMigrations(), x => x.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Remember_me_extends_token_from_eight_hours_to_thirty_days()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "0123456789abcdef0123456789abcdef0123456789abcdef",
            ["Jwt:Issuer"] = "FleetBooking.Tests",
            ["Jwt:Audience"] = "FleetBooking.Tests"
        }).Build();
        var service = new TokenService(configuration);
        var user = new AppUser { FullName = "Test korisnik", Email = "test@example.com", PasswordHash = "test" };
        var handler = new JwtSecurityTokenHandler();

        var normal = handler.ReadJwtToken(service.Create(user));
        var remembered = handler.ReadJwtToken(service.Create(user, true));

        var now = DateTime.UtcNow;
        Assert.InRange(normal.ValidTo - now, TimeSpan.FromHours(7.9), TimeSpan.FromHours(8.1));
        Assert.InRange(remembered.ValidTo - now, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
    }

    [Fact]
    public async Task Sql_server_migration_and_application_lock_batch_work_when_test_instance_is_configured()
    {
        var configured = Environment.GetEnvironmentVariable("FLEET_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "FleetBookingTest_" + Guid.NewGuid().ToString("N")
        };
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var setup = new AppDbContext(options);
        try
        {
            await setup.Database.MigrateAsync();
            var user = new AppUser { FullName = "SQL test", Email = "sql-test@example.com", PasswordHash = "test" };
            var vehicle = new Vehicle { MakeModel = "SQL test vozilo", Registration = "TEST-001", SeatCount = 5, FuelType = "Test", PickupLocation = "Test" };
            setup.AddRange(user, vehicle); await setup.SaveChangesAsync();
            var start = DateTime.UtcNow.AddDays(2);
            var created = await new BookingService(setup).CreateAsync(user.Id, new(vehicle.Id, start, start.AddHours(1), "Test", "Test", "Test", "067000000", 1, null), user.FullName, default);
            Assert.NotEqual(Guid.Empty, created.Id);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }

    private static SaveReservationRequest Request(Guid vehicleId)
    {
        var start = DateTime.UtcNow.AddDays(3); return new(vehicleId, start, start.AddHours(2), "Sastanak", "Nikšić", "Centrala", "+00000000000", 2, null);
    }
    private static AppDbContext Context(string database) => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options);
    private static async Task<(AppUser, Vehicle)> SeedAsync(string database)
    {
        await using var db = Context(database);
        var user = new AppUser { FullName = "Test Zaposleni", Email = $"{Guid.NewGuid()}@example.com", PasswordHash = "test" };
        var vehicle = new Vehicle { MakeModel = "Test vozilo", Registration = Guid.NewGuid().ToString("N")[..8], SeatCount = 5, FuelType = "Dizel", PickupLocation = "Podgorica" };
        db.AddRange(user, vehicle); await db.SaveChangesAsync(); return (user, vehicle);
    }
}

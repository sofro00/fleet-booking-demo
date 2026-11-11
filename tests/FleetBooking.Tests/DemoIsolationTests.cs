using System.Security.Cryptography;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Api.Services;
using FleetBooking.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FleetBooking.Tests;

public sealed class DemoIsolationTests
{
    private const string Connection = @"Server=.\SQLEXPRESS;Database=FleetBookingDemo;Integrated Security=True;TrustServerCertificate=True";

    [Theory]
    [InlineData(@"Server=remote.example.com;Database=FleetBookingDemo;Integrated Security=True")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=OtherDatabase;Integrated Security=True")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=FleetBookingDemo;User ID=demo;Password=invalid")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=FleetBookingDemo;Integrated Security=True;Failover Partner=remote.example.com")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=FleetBookingDemo;Integrated Security=True;AttachDbFilename=C:\demo.mdf")]
    public void Business_or_remote_database_configuration_is_rejected(string connection) =>
        Assert.Throws<InvalidOperationException>(() => DemoSafety.ValidateConnection(connection));

    [Fact]
    public void Dedicated_local_demo_database_is_allowed() => DemoSafety.ValidateConnection(Connection);

    [Theory]
    [InlineData("PublicAccess:Enabled", "true")]
    [InlineData("Backup:Enabled", "true")]
    [InlineData("Kestrel:Endpoints:Http:Url", "http://0.0.0.0:8080")]
    [InlineData("Urls", "https://0.0.0.0:58443")]
    [InlineData("service-settings", "server.json")]
    public void Production_settings_are_rejected(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FleetBooking"] = Connection, [key] = value
        }).Build();
        Assert.Throws<InvalidOperationException>(() => DemoSafety.ValidateConfiguration(config));
    }

    [Theory]
    [InlineData("https://localhost:58443/", true)]
    [InlineData("https://remote.example.com:58443/", false)]
    [InlineData("https://localhost:8443/", false)]
    [InlineData("http://localhost:58443/", false)]
    [InlineData("https://localhost:58443/other", false)]
    [InlineData("https://localhost:58443/?redirect=elsewhere", false)]
    public void Desktop_credentials_can_only_be_sent_to_the_demo_endpoint(string value, bool allowed) =>
        Assert.Equal(allowed, DemoEndpoint.IsAllowed(new Uri(value)));

    [Fact]
    public void New_users_and_vehicle_plates_must_use_demo_identifiers()
    {
        Assert.True(DemoSafety.IsDemoEmail("ana@example.com"));
        Assert.False(DemoSafety.IsDemoEmail("ana@company.invalid"));
        Assert.False(DemoSafety.IsDemoEmail("ana@example.com.attacker.invalid"));
        Assert.True(DemoSafety.IsDemoRegistration("DEMO-001"));
        Assert.False(DemoSafety.IsDemoRegistration("REAL-001"));
    }

    [Fact]
    public async Task Seed_is_synthetic_idempotent_and_preserves_password_hashes()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var password = "Demo!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "a1";
        var hasher = new PasswordHasher<AppUser>();
        await DemoSeeder.SeedAsync(db, hasher, password);
        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(5, await db.Vehicles.CountAsync());
        Assert.Equal(5, await db.Reservations.CountAsync());
        Assert.All(await db.Users.ToListAsync(), user =>
        {
            Assert.True(DemoSafety.IsDemoEmail(user.Email));
            Assert.Null(user.Phone);
            Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, user.PasswordHash, password));
        });
        Assert.All(await db.Vehicles.ToListAsync(), vehicle =>
        {
            Assert.True(DemoSafety.IsDemoRegistration(vehicle.Registration));
            Assert.NotNull(vehicle.ImageContent);
            Assert.Equal("image/png", vehicle.ImageContentType);
            Assert.NotNull(vehicle.ImageUpdatedUtc);
            Assert.InRange(vehicle.ImageContent!.Length, 8, 3 * 1024 * 1024);
        });
        Assert.All(await db.Reservations.ToListAsync(), reservation => Assert.Equal("", reservation.ContactPhone));
        var hashes = await db.Users.OrderBy(user => user.Email).Select(user => user.PasswordHash).ToListAsync();
        await DemoSeeder.SeedAsync(db, hasher, "unused");
        Assert.Equal(hashes, await db.Users.OrderBy(user => user.Email).Select(user => user.PasswordHash).ToListAsync());
        Assert.Equal(5, await db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Existing_demo_gets_missing_images_without_reseeding_or_overwriting_custom_images()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var hasher = new PasswordHasher<AppUser>();
        var password = "Demo!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "a1";
        await DemoSeeder.SeedAsync(db, hasher, password);
        var vehicles = await db.Vehicles.OrderBy(vehicle => vehicle.Registration).ToListAsync();
        vehicles[0].ImageContent = null;
        vehicles[0].ImageContentType = null;
        vehicles[0].ImageUpdatedUtc = null;
        vehicles[1].ImageContent = [];
        byte[] custom = [1, 2, 3, 4];
        var customUpdated = DateTime.UtcNow.AddDays(-2);
        vehicles[2].ImageContent = custom;
        vehicles[2].ImageUpdatedUtc = customUpdated;
        var originalHashes = await db.Users.OrderBy(user => user.Email).Select(user => user.PasswordHash).ToListAsync();
        await db.SaveChangesAsync();

        await DemoSeeder.SeedAsync(db, hasher, null);
        Assert.NotEmpty(vehicles[0].ImageContent!);
        Assert.NotEmpty(vehicles[1].ImageContent!);
        Assert.Equal("image/png", vehicles[0].ImageContentType);
        Assert.NotNull(vehicles[0].ImageUpdatedUtc);
        Assert.Equal(custom, vehicles[2].ImageContent);
        Assert.Equal(customUpdated, vehicles[2].ImageUpdatedUtc);
        Assert.Equal(originalHashes, await db.Users.OrderBy(user => user.Email).Select(user => user.PasswordHash).ToListAsync());
        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(5, await db.Vehicles.CountAsync());
        Assert.Equal(5, await db.Reservations.CountAsync());
    }
}

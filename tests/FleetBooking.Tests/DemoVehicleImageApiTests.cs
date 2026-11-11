using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Api.Services;
using FleetBooking.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FleetBooking.Tests;

public sealed class DemoVehicleImageApiTests
{
    [Fact]
    public async Task All_five_demo_vehicle_images_are_available_through_the_authenticated_api()
    {
        using var factory = new OperationsTests.ApiFactory();
        try
        {
            var password = "Demo!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "a1";
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (db.Database.IsRelational()) await db.Database.MigrateAsync();
                await DemoSeeder.SeedAsync(db, new PasswordHasher<AppUser>(), password);
            }
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", password));
            response.EnsureSuccessStatusCode();
            var session = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            var vehicles = (await client.GetFromJsonAsync<List<VehicleDto>>("/api/vehicles?includeInactive=true"))!;
            Assert.Equal(5, vehicles.Count);
            var imageHashes = new HashSet<string>();
            foreach (var vehicle in vehicles)
            {
                Assert.True(vehicle.HasImage);
                Assert.NotNull(vehicle.ImageUpdatedUtc);
                using var image = await client.GetAsync($"/api/vehicles/{vehicle.Id}/image");
                image.EnsureSuccessStatusCode();
                Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
                var content = await image.Content.ReadAsByteArrayAsync();
                Assert.InRange(content.Length, 8, 3 * 1024 * 1024);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, content[..8]);
                imageHashes.Add(Convert.ToHexString(SHA256.HashData(content)));
            }
            Assert.Equal(5, imageHashes.Count);
            using var anonymous = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.GetAsync($"/api/vehicles/{vehicles[0].Id}/image")).StatusCode);
        }
        finally
        {
            if (factory.UsesSql)
            {
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
            }
        }
    }
}

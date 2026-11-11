using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace FleetBooking.Api.Services;

public static class DemoVehicleImages
{
    private sealed record Asset(string Model, string File, string PreviousSha256);
    private static readonly IReadOnlyDictionary<string, Asset> Catalog =
        new Dictionary<string, Asset>(StringComparer.Ordinal)
        {
            ["DEMO-001"] = new("Toyota Corolla", "toyota-corolla.png", "b179cb6b38510033f4d052b9bfe7147abea34049d2f0376f3412928ef89b51e7"),
            ["DEMO-002"] = new("Škoda Octavia", "skoda-octavia.png", "cd3e37cafd35ab1dd20c630fe29122aabb433ccc37abc040e650b37dad6bd6af"),
            ["DEMO-003"] = new("Renault Clio", "renault-clio.png", "54ffa26e4609dd3b16889663e312a8f5ceb069b921f45ab85c0ba1678147d946"),
            ["DEMO-004"] = new("Ford Transit", "ford-transit.png", "1ad9470c3730d830991d57e39d8fb1cc041471b933e1a03d72b13fbe11afde01"),
            ["DEMO-005"] = new("Volkswagen Golf", "volkswagen-golf.png", "334698110e54154129a68f543de5a9ba085c3ba81b060699b4b7d3f8e233a5f7")
        };

    public static bool ApplyDefault(Vehicle vehicle)
    {
        if (!Catalog.TryGetValue(vehicle.Registration, out var asset) ||
            vehicle.MakeModel != asset.Model) return false;
        // Upgrade only our exact previous default, preserving any custom image.
        if (vehicle.ImageContent is { Length: > 0 } current &&
            !Convert.ToHexString(SHA256.HashData(current)).Equals(asset.PreviousSha256, StringComparison.OrdinalIgnoreCase))
            return false;

        using var stream = typeof(DemoVehicleImages).Assembly.GetManifestResourceStream(
            "FleetBooking.DemoVehicleImages." + asset.File)
            ?? throw new InvalidOperationException("The packaged demo vehicle image is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var content = buffer.ToArray();
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (content.Length > 3 * 1024 * 1024 || !content.AsSpan().StartsWith(signature))
            throw new InvalidOperationException("The packaged demo vehicle image is invalid.");
        vehicle.ImageContent = content;
        vehicle.ImageContentType = "image/png";
        vehicle.ImageUpdatedUtc = DateTime.UtcNow;
        return true;
    }

    public static async Task UpgradeAsync(AppDbContext db)
    {
        var registrations = Catalog.Keys.ToArray();
        var candidates = await db.Vehicles.Where(vehicle => registrations.Contains(vehicle.Registration)).ToListAsync();
        var changed = false;
        foreach (var vehicle in candidates) changed |= ApplyDefault(vehicle);
        if (changed) await db.SaveChangesAsync();
    }
}

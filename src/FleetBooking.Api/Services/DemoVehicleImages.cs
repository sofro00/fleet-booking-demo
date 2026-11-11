using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FleetBooking.Api.Services;

public static class DemoVehicleImages
{
    private static readonly IReadOnlyDictionary<string, (string Model, string File)> Catalog =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["DEMO-001"] = ("Toyota Corolla", "toyota-corolla.png"),
            ["DEMO-002"] = ("Škoda Octavia", "skoda-octavia.png"),
            ["DEMO-003"] = ("Renault Clio", "renault-clio.png"),
            ["DEMO-004"] = ("Ford Transit", "ford-transit.png"),
            ["DEMO-005"] = ("Volkswagen Golf", "volkswagen-golf.png")
        };

    public static bool ApplyIfMissing(Vehicle vehicle)
    {
        if (vehicle.ImageContent is { Length: > 0 } ||
            !Catalog.TryGetValue(vehicle.Registration, out var asset) ||
            vehicle.MakeModel != asset.Model) return false;

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

    public static async Task AddMissingAsync(AppDbContext db)
    {
        var candidates = await db.Vehicles.Where(vehicle =>
            vehicle.ImageContent == null || vehicle.ImageContent.Length == 0).ToListAsync();
        var changed = false;
        foreach (var vehicle in candidates) changed |= ApplyIfMissing(vehicle);
        if (changed) await db.SaveChangesAsync();
    }
}

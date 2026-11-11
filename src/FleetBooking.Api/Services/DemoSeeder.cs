using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FleetBooking.Api.Services;

public static class DemoSeeder
{
    public static async Task SeedAsync(AppDbContext db, IPasswordHasher<AppUser> hasher, string? password)
    {
        if (await db.Users.AnyAsync())
        {
            await DemoVehicleImages.UpgradeAsync(db);
            return;
        }
        if (!PasswordPolicy.IsValid(password ?? ""))
            throw new InvalidOperationException("Run scripts/Start-Demo.ps1 to generate local demo credentials.");

        AppUser User(string name, string email, string role)
        {
            var user = new AppUser { FullName = name, Email = email, Role = role, PasswordHash = "", MustChangePassword = false };
            user.PasswordHash = hasher.HashPassword(user, password!);
            return user;
        }
        var admin = User("Demo Administrator", "admin@example.com", Roles.Administrator);
        var ana = User("Ana Demo", "ana@example.com", Roles.Employee);
        var luka = User("Luka Demo", "luka@example.com", Roles.Employee);
        var vehicles = new[]
        {
            new Vehicle { MakeModel = "Toyota Corolla", Registration = "DEMO-001", SeatCount = 5, FuelType = "Hibrid", PickupLocation = "Demo centar", Note = "Izmišljeni podaci za portfolio." },
            new Vehicle { MakeModel = "Škoda Octavia", Registration = "DEMO-002", SeatCount = 5, FuelType = "Dizel", PickupLocation = "Demo centar" },
            new Vehicle { MakeModel = "Renault Clio", Registration = "DEMO-003", SeatCount = 5, FuelType = "Benzin", PickupLocation = "Demo poslovnica", AssignedUserId = ana.Id },
            new Vehicle { MakeModel = "Ford Transit", Registration = "DEMO-004", SeatCount = 3, FuelType = "Dizel", PickupLocation = "Demo skladište" },
            new Vehicle { MakeModel = "Volkswagen Golf", Registration = "DEMO-005", SeatCount = 5, FuelType = "Benzin", PickupLocation = "Demo poslovnica", IsActive = false }
        };
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Podgorica");
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        DateTime At(int day, int hour) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(today.AddDays(day).AddHours(hour), DateTimeKind.Unspecified), zone);
        Reservation Reservation(AppUser user, Vehicle vehicle, int day, int start, int end, string purpose, string destination) => new()
        {
            UserId = user.Id, VehicleId = vehicle.Id, StartUtc = At(day, start), EndUtc = At(day, end),
            Purpose = purpose, Destination = destination, DepartureLocation = "Podgorica", ContactPhone = "",
            PassengerCount = 1, Note = "Izmišljena demo rezervacija."
        };
        foreach (var vehicle in vehicles) DemoVehicleImages.ApplyDefault(vehicle);
        db.Users.AddRange(admin, ana, luka);
        db.Vehicles.AddRange(vehicles);
        db.Reservations.AddRange(
            Reservation(ana, vehicles[0], 1, 9, 12, "Terenski rad", "Nikšić"),
            Reservation(luka, vehicles[0], 1, 12, 15, "Službeni sastanak", "Cetinje"),
            Reservation(admin, vehicles[1], 1, 10, 14, "Obuka zaposlenih", "Budva"),
            Reservation(ana, vehicles[2], 2, 9, 16, "Obilazak radnje", "Bar"),
            Reservation(luka, vehicles[3], 3, 8, 13, "Dostava i prevoz robe", "Kotor"));
        db.Unavailabilities.Add(new VehicleUnavailability { VehicleId = vehicles[3].Id, StartUtc = At(0, 0), EndUtc = At(1, 0), Reason = "Demo: redovni servis" });
        db.VehicleIssues.Add(new VehicleIssue { VehicleId = vehicles[1].Id, ReporterUserId = luka.Id, Category = VehicleIssueCategories.Other, Description = "Demo: provjeriti osvjetljenje kabine." });
        db.AuditLogs.Add(new AuditLog { ActorUserId = admin.Id, ActorName = admin.FullName, Action = "Demo inicijalizovan", EntityType = "Demo", EntityId = "synthetic-data-v1", Details = "Svi podaci su izmišljeni; nijesu uvezeni iz poslovnih sistema." });
        await db.SaveChangesAsync();
    }
}

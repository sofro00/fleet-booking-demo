using FleetBooking.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FleetBooking.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<VehicleUnavailability> Unavailabilities => Set<VehicleUnavailability>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<VehicleIssue> VehicleIssues => Set<VehicleIssue>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.AssignedUserId);
        b.Entity<Reservation>().HasIndex(x => new { x.VehicleId, x.StartUtc, x.EndUtc, x.Status });
        b.Entity<VehicleUnavailability>().HasIndex(x => new { x.VehicleId, x.StartUtc, x.EndUtc });
        b.Entity<AppUser>().Property(x => x.FullName).HasMaxLength(150);
        b.Entity<AppUser>().Property(x => x.Email).HasMaxLength(256);
        b.Entity<Vehicle>().Property(x => x.Registration).HasMaxLength(32);
        b.Entity<Vehicle>().Property(x => x.ImageContentType).HasMaxLength(64);
        b.Entity<Reservation>().Property(x => x.Status).HasMaxLength(20);
        b.Entity<Reservation>().HasOne(x => x.User).WithMany(x => x.Reservations).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Reservation>().HasOne(x => x.Vehicle).WithMany(x => x.Reservations).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VehicleUnavailability>().HasOne(x => x.Vehicle).WithMany(x => x.Unavailabilities).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Vehicle>().HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<VehicleIssue>().Property(x => x.Category).HasMaxLength(32);
        b.Entity<VehicleIssue>().Property(x => x.Status).HasMaxLength(20);
        b.Entity<VehicleIssue>().Property(x => x.Description).HasMaxLength(4000);
        b.Entity<VehicleIssue>().Property(x => x.AdminNote).HasMaxLength(4000);
        b.Entity<VehicleIssue>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<VehicleIssue>().HasIndex(x => new { x.VehicleId, x.Status, x.CreatedUtc });
        b.Entity<VehicleIssue>().HasIndex(x => new { x.ReporterUserId, x.CreatedUtc });
        b.Entity<VehicleIssue>().HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VehicleIssue>().HasOne(x => x.ReporterUser).WithMany().HasForeignKey(x => x.ReporterUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<VehicleIssue>().HasOne(x => x.UpdatedByUser).WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

using FleetBooking.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FleetBooking.Api.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FLEET_ConnectionStrings__FleetBooking")
            ?? @"Server=.\SQLEXPRESS;Database=FleetBookingDemo;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";
        DemoSafety.ValidateConnection(connection);
        return new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
    }
}

using FleetBooking.Shared;

namespace FleetBooking.Api.Models;

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public string Role { get; set; } = Roles.Employee;
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public string? Phone { get; set; }
    public ICollection<Reservation> Reservations { get; set; } = [];
}

public sealed class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string MakeModel { get; set; }
    public required string Registration { get; set; }
    public int SeatCount { get; set; }
    public required string FuelType { get; set; }
    public required string PickupLocation { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Note { get; set; }
    public byte[]? ImageContent { get; set; }
    public string? ImageContentType { get; set; }
    public DateTime? ImageUpdatedUtc { get; set; }
    public Guid? AssignedUserId { get; set; }
    public AppUser? AssignedUser { get; set; }
    public ICollection<Reservation> Reservations { get; set; } = [];
    public ICollection<VehicleUnavailability> Unavailabilities { get; set; } = [];
}

public sealed class Reservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public required string Purpose { get; set; }
    public required string Destination { get; set; }
    public required string DepartureLocation { get; set; }
    public required string ContactPhone { get; set; }
    public int? PassengerCount { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = ReservationStatuses.Active;
    public string? CancellationReason { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class VehicleUnavailability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public required string Reason { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public Guid? ActorUserId { get; set; }
    public required string ActorName { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string Details { get; set; }
}

public sealed class VehicleIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;
    public Guid ReporterUserId { get; set; }
    public AppUser ReporterUser { get; set; } = null!;
    public required string Category { get; set; }
    public required string Description { get; set; }
    public string Status { get; set; } = VehicleIssueStatuses.Open;
    public string? AdminNote { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public AppUser? UpdatedByUser { get; set; }
    public long Version { get; set; } = 1;
}

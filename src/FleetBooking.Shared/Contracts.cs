namespace FleetBooking.Shared;

public static class Roles { public const string Administrator = "Administrator"; public const string Employee = "Employee"; }
public static class ReservationStatuses { public const string Active = "Active"; public const string Cancelled = "Cancelled"; }
public static class VehicleStates
{
    public const string Free = "Free", Reserved = "Reserved", Service = "Service", Assigned = "Assigned", Inactive = "Inactive";
    public static string Code(bool active, bool unavailable, bool reserved, Guid? assignedUserId) =>
        !active ? Inactive : unavailable ? Service : assignedUserId.HasValue ? Assigned : reserved ? Reserved : Free;
    public static string Label(string code, string? assignedName) => code switch
    {
        Inactive => "Deaktivirano", Service => "Na servisu / nedostupno", Reserved => "Rezervisano",
        Assigned => $"Zaduženo: {assignedName ?? "korisnik"}", _ => "Slobodno"
    };
}

public record LoginRequest(string Email, string Password, bool RememberMe = false);
public record LoginResponse(string Token, Guid UserId, string FullName, string Email, string Role, bool MustChangePassword);
public record CurrentUserDto(Guid UserId, string FullName, string Email, string Role, bool MustChangePassword);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record UserDto(Guid Id, string FullName, string Email, string Role, bool IsActive, bool MustChangePassword, string? Phone);
public record CreateUserRequest(string FullName, string Email, string Role, string TemporaryPassword, string? Phone);
public record UpdateUserRequest(string FullName, string Role, bool IsActive, string? Phone);
public record ResetPasswordRequest(string NewTemporaryPassword);

public record VehicleDto(Guid Id, string MakeModel, string Registration, int SeatCount, string FuelType, string PickupLocation, bool IsActive, string? Note,
    bool HasImage = false, DateTime? ImageUpdatedUtc = null, Guid? AssignedUserId = null, string? AssignedUserName = null,
    string StatusCode = VehicleStates.Free, bool HasReservation = false);
public record SaveVehicleRequest(string MakeModel, string Registration, int SeatCount, string FuelType, string PickupLocation, string? Note);
public record AssignVehicleRequest(Guid? UserId);
public record SaveVehicleImageRequest(string ContentType, string Base64Data);

public record ReservationDto(Guid Id, Guid VehicleId, string Vehicle, string Registration, Guid UserId, string EmployeeName,
    DateTime StartUtc, DateTime EndUtc, string Purpose, string Destination, string DepartureLocation, string ContactPhone,
    int? PassengerCount, string? Note, string Status, string? CancellationReason, DateTime CreatedUtc);
public record SaveReservationRequest(Guid VehicleId, DateTime StartUtc, DateTime EndUtc, string Purpose, string Destination,
    string DepartureLocation, string? ContactPhone, int? PassengerCount, string? Note, string? AdminReason = null);
public record CancelReservationRequest(string Reason);

public record UnavailabilityDto(Guid Id, Guid VehicleId, string Vehicle, DateTime StartUtc, DateTime EndUtc, string Reason);
public record SaveUnavailabilityRequest(Guid VehicleId, DateTime StartUtc, DateTime EndUtc, string Reason);
public record OccupancyDto(Guid VehicleId, string Vehicle, DateTime StartUtc, DateTime EndUtc, string Type, string? Description);
public record AuditDto(long Id, DateTime TimestampUtc, string ActorName, string Action, string EntityType, string EntityId, string Details);
public record ApiError(string Message, IDictionary<string, string[]>? Errors = null);
public record BackupStatusDto(bool Enabled, string DailyTime, string TimeZone, bool Running,
    DateTime? LastAttemptUtc, DateTime? LastSuccessUtc, bool LastAttemptFailed, string? Message);
public record HealthDto(string Status, DateTime Utc, string Version);

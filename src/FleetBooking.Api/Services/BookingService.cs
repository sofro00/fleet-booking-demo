using System.Collections.Concurrent;
using System.Data;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Shared;
using Microsoft.EntityFrameworkCore;

namespace FleetBooking.Api.Services;

public sealed class RuleException(string message) : Exception(message);

public static class BookingRules
{
    public static bool Overlaps(DateTime existingStart, DateTime existingEnd, DateTime newStart, DateTime newEnd) =>
        existingStart < newEnd && existingEnd > newStart;

    public static void ValidatePeriod(DateTime startUtc, DateTime endUtc, bool requireFuture = true)
    {
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc)
            throw new RuleException("Vrijeme mora biti poslato u UTC formatu.");
        if (endUtc <= startUtc) throw new RuleException("Vrijeme vraćanja mora biti poslije vremena preuzimanja.");
        if (requireFuture && startUtc < DateTime.UtcNow.AddSeconds(-30)) throw new RuleException("Nova rezervacija ne može početi u prošlosti.");
    }

    public static void EnsureCanModify(Guid ownerId, Guid actorId, bool isAdmin, DateTime startUtc)
    {
        if (!isAdmin && ownerId != actorId) throw new UnauthorizedAccessException();
        if (!isAdmin && startUtc <= DateTime.UtcNow) throw new RuleException("Može se mijenjati samo buduća rezervacija koja nije počela.");
    }
}

public sealed class BookingService(AppDbContext db)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> LocalLocks = new();

    public async Task<Reservation> CreateAsync(Guid userId, SaveReservationRequest request, string actorName, CancellationToken ct)
    {
        BookingRules.ValidatePeriod(request.StartUtc, request.EndUtc);
        ValidateText(request.Purpose, "Svrha puta"); ValidateText(request.Destination, "Odredište");
        ValidateText(request.DepartureLocation, "Polazna lokacija");
        await using var handle = await AcquireAsync([request.VehicleId], ct);
        await using var transaction = await BeginTransactionAsync(ct);
        await AcquireDatabaseLockAsync(request.VehicleId, ct);
        var vehicle = await db.Vehicles.Include(x => x.AssignedUser).SingleOrDefaultAsync(x => x.Id == request.VehicleId, ct)
            ?? throw new RuleException("Vozilo ne postoji.");
        if (!vehicle.IsActive) throw new RuleException("Vozilo nije aktivno.");
        EnsureCanReserveAssignedVehicle(vehicle, userId);
        ValidateCapacity(vehicle, request.PassengerCount);
        await EnsureAvailableAsync(request.VehicleId, request.StartUtc, request.EndUtc, null, ct);
        var item = new Reservation
        {
            VehicleId = vehicle.Id, UserId = userId, StartUtc = request.StartUtc, EndUtc = request.EndUtc,
            Purpose = request.Purpose.Trim(), Destination = request.Destination.Trim(),
            DepartureLocation = request.DepartureLocation.Trim(), ContactPhone = request.ContactPhone?.Trim() ?? "",
            PassengerCount = request.PassengerCount, Note = Clean(request.Note)
        };
        db.Reservations.Add(item);
        Audit(userId, actorName, "Kreirana rezervacija", "Reservation", item.Id.ToString(), $"{vehicle.Registration}; {item.StartUtc:u} - {item.EndUtc:u}");
        await db.SaveChangesAsync(ct); await CommitAsync(transaction, ct);
        return item;
    }

    public async Task<Reservation> UpdateAsync(Guid id, Guid actorId, bool isAdmin, SaveReservationRequest request, string actorName, CancellationToken ct)
    {
        BookingRules.ValidatePeriod(request.StartUtc, request.EndUtc);
        if (isAdmin && string.IsNullOrWhiteSpace(request.AdminReason)) throw new RuleException("Administrator mora unijeti razlog izmjene.");
        ValidateText(request.Purpose, "Svrha puta"); ValidateText(request.Destination, "Odredište");
        ValidateText(request.DepartureLocation, "Polazna lokacija");
        var original = await db.Reservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RuleException("Rezervacija ne postoji.");
        BookingRules.EnsureCanModify(original.UserId, actorId, isAdmin, original.StartUtc);
        if (original.Status == ReservationStatuses.Cancelled) throw new RuleException("Otkazana rezervacija se ne može mijenjati.");
        await using var handle = await AcquireAsync([original.VehicleId, request.VehicleId], ct);
        await using var transaction = await BeginTransactionAsync(ct);
        foreach (var vehicleId in new[] { original.VehicleId, request.VehicleId }.Distinct().Order()) await AcquireDatabaseLockAsync(vehicleId, ct);
        var item = await db.Reservations.SingleAsync(x => x.Id == id, ct);
        var vehicle = await db.Vehicles.Include(x => x.AssignedUser).SingleOrDefaultAsync(x => x.Id == request.VehicleId, ct) ?? throw new RuleException("Vozilo ne postoji.");
        if (!vehicle.IsActive) throw new RuleException("Vozilo nije aktivno.");
        EnsureCanReserveAssignedVehicle(vehicle, original.UserId);
        ValidateCapacity(vehicle, request.PassengerCount);
        await EnsureAvailableAsync(request.VehicleId, request.StartUtc, request.EndUtc, id, ct);
        item.VehicleId = request.VehicleId; item.StartUtc = request.StartUtc; item.EndUtc = request.EndUtc;
        item.Purpose = request.Purpose.Trim(); item.Destination = request.Destination.Trim();
        item.DepartureLocation = request.DepartureLocation.Trim(); item.ContactPhone = request.ContactPhone?.Trim() ?? "";
        item.PassengerCount = request.PassengerCount; item.Note = Clean(request.Note); item.UpdatedUtc = DateTime.UtcNow;
        Audit(actorId, actorName, "Izmijenjena rezervacija", "Reservation", id.ToString(), isAdmin ? request.AdminReason! : "Korisnik izmijenio svoju rezervaciju");
        await db.SaveChangesAsync(ct); await CommitAsync(transaction, ct);
        return item;
    }

    public async Task CancelAsync(Guid id, Guid actorId, bool isAdmin, string actorName, string reason, CancellationToken ct)
    {
        ValidateText(reason, "Razlog otkazivanja");
        var item = await db.Reservations.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new RuleException("Rezervacija ne postoji.");
        BookingRules.EnsureCanModify(item.UserId, actorId, isAdmin, item.StartUtc);
        if (item.Status == ReservationStatuses.Cancelled) throw new RuleException("Rezervacija je već otkazana.");
        item.Status = ReservationStatuses.Cancelled; item.CancellationReason = reason.Trim(); item.UpdatedUtc = DateTime.UtcNow;
        Audit(actorId, actorName, "Otkazana rezervacija", "Reservation", id.ToString(), reason.Trim());
        await db.SaveChangesAsync(ct);
    }

    public async Task<VehicleUnavailability> AddUnavailabilityAsync(Guid actorId, string actorName, SaveUnavailabilityRequest request, CancellationToken ct)
    {
        BookingRules.ValidatePeriod(request.StartUtc, request.EndUtc, false); ValidateText(request.Reason, "Razlog nedostupnosti");
        await using var handle = await AcquireAsync([request.VehicleId], ct);
        await using var transaction = await BeginTransactionAsync(ct); await AcquireDatabaseLockAsync(request.VehicleId, ct);
        if (!await db.Vehicles.AnyAsync(x => x.Id == request.VehicleId, ct)) throw new RuleException("Vozilo ne postoji.");
        await EnsureAvailableAsync(request.VehicleId, request.StartUtc, request.EndUtc, null, ct);
        var item = new VehicleUnavailability { VehicleId = request.VehicleId, StartUtc = request.StartUtc, EndUtc = request.EndUtc, Reason = request.Reason.Trim() };
        db.Unavailabilities.Add(item);
        Audit(actorId, actorName, "Dodat period nedostupnosti", "VehicleUnavailability", item.Id.ToString(), request.Reason.Trim());
        await db.SaveChangesAsync(ct); await CommitAsync(transaction, ct); return item;
    }

    public async Task SetVehicleStatusAsync(Guid vehicleId, bool active, Guid actorId, string actorName, CancellationToken ct)
    {
        await using var handle = await AcquireAsync([vehicleId], ct);
        await using var transaction = await BeginTransactionAsync(ct); await AcquireDatabaseLockAsync(vehicleId, ct);
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(x => x.Id == vehicleId, ct) ?? throw new RuleException("Vozilo ne postoji.");
        if (!active && await db.Reservations.AnyAsync(x => x.VehicleId == vehicleId && x.Status == ReservationStatuses.Active && x.EndUtc > DateTime.UtcNow, ct))
            throw new RuleException("Vozilo ima buduće ili tekuće rezervacije. Prvo ih izmijenite ili otkažite.");
        vehicle.IsActive = active;
        Audit(actorId, actorName, active ? "Aktivirano vozilo" : "Deaktivirano vozilo", "Vehicle", vehicleId.ToString(), vehicle.Registration);
        await db.SaveChangesAsync(ct); await CommitAsync(transaction, ct);
    }

    public async Task AssignVehicleAsync(Guid vehicleId, Guid? assignedUserId, Guid actorId, string actorName, CancellationToken ct)
    {
        await using var handle = await AcquireAsync([vehicleId], ct);
        await using var transaction = await BeginTransactionAsync(ct); await AcquireDatabaseLockAsync(vehicleId, ct);
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(x => x.Id == vehicleId, ct) ?? throw new RuleException("Vozilo ne postoji.");
        AppUser? user = null;
        if (assignedUserId.HasValue)
        {
            user = await db.Users.SingleOrDefaultAsync(x => x.Id == assignedUserId.Value, ct);
            if (user is null || !user.IsActive) throw new RuleException("Vozilo se može zadužiti samo aktivnom korisniku.");
            var conflictingReservation = await db.Reservations.AnyAsync(x => x.VehicleId == vehicleId && x.UserId != user.Id &&
                x.Status == ReservationStatuses.Active && x.EndUtc > DateTime.UtcNow, ct);
            if (conflictingReservation)
                throw new RuleException("Vozilo ima tekuće ili buduće rezervacije drugih korisnika. Prvo ih izmijenite ili otkažite.");
        }
        vehicle.AssignedUserId = user?.Id;
        Audit(actorId, actorName, user is null ? "Razduženo vozilo" : "Zaduženo vozilo", "Vehicle", vehicleId.ToString(),
            user is null ? vehicle.Registration : $"{vehicle.Registration} — {user.FullName}");
        await db.SaveChangesAsync(ct); await CommitAsync(transaction, ct);
    }

    private async Task EnsureAvailableAsync(Guid vehicleId, DateTime start, DateTime end, Guid? excludedReservationId, CancellationToken ct)
    {
        var reservationConflict = await db.Reservations.AnyAsync(x => x.VehicleId == vehicleId && x.Id != excludedReservationId &&
            x.Status == ReservationStatuses.Active && x.StartUtc < end && x.EndUtc > start, ct);
        var unavailableConflict = await db.Unavailabilities.AnyAsync(x => x.VehicleId == vehicleId && x.StartUtc < end && x.EndUtc > start, ct);
        if (reservationConflict) throw new RuleException("Vozilo je već rezervisano u dijelu izabranog perioda.");
        if (unavailableConflict) throw new RuleException("Vozilo je nedostupno u dijelu izabranog perioda.");
    }

    private async Task AcquireDatabaseLockAsync(Guid vehicleId, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        var resource = $"FleetDemoVehicle:{vehicleId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock
                @Resource = {{resource}},
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;
            IF @lockResult < 0
                THROW 51000, 'Vozilo je trenutno u obradi. Pokušajte ponovo.', 1;
            """, ct);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return null;
        return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    }
    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx, CancellationToken ct) { if (tx is not null) await tx.CommitAsync(ct); }
    private static void EnsureCanReserveAssignedVehicle(Vehicle vehicle, Guid reservationUserId)
    {
        if (!vehicle.AssignedUserId.HasValue || vehicle.AssignedUserId.Value == reservationUserId) return;
        var assignedName = vehicle.AssignedUser?.FullName ?? "zaduženom korisniku";
        throw new RuleException($"Vozilo je zaduženo korisniku {assignedName} i ne može ga rezervisati drugi korisnik. Obratite se: {assignedName}.");
    }
    private static void ValidateCapacity(Vehicle v, int? passengers) { if (passengers is < 1) throw new RuleException("Broj putnika mora biti pozitivan."); if (passengers > v.SeatCount) throw new RuleException($"Vozilo ima najviše {v.SeatCount} sjedišta."); }
    private static void ValidateText(string value, string field) { if (string.IsNullOrWhiteSpace(value)) throw new RuleException($"Polje '{field}' je obavezno."); }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void Audit(Guid actorId, string actor, string action, string type, string id, string details) => db.AuditLogs.Add(new AuditLog { ActorUserId = actorId, ActorName = actor, Action = action, EntityType = type, EntityId = id, Details = details });

    private static async Task<IAsyncDisposable> AcquireAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var locks = ids.Distinct().Order().Select(id => LocalLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1))).ToList();
        foreach (var gate in locks) await gate.WaitAsync(ct);
        return new LockHandle(locks);
    }
    private sealed class LockHandle(List<SemaphoreSlim> locks) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { for (var i = locks.Count - 1; i >= 0; i--) locks[i].Release(); return ValueTask.CompletedTask; }
    }
}

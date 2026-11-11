using System.Security.Claims;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Api.Services;
using FleetBooking.Shared;
using Microsoft.EntityFrameworkCore;

namespace FleetBooking.Api.Endpoints;

public static class VehicleIssuesEndpoints
{
    public static void MapVehicleIssueEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/vehicle-issues").RequireAuthorization();
        api.MapGet("/", async (ClaimsPrincipal actor, AppDbContext db) =>
            Results.Ok(await Dtos(Visible(db, actor).OrderByDescending(x => x.CreatedUtc)).ToListAsync()));
        api.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal actor, AppDbContext db) =>
        {
            var item = await Dtos(Visible(db, actor).Where(x => x.Id == id)).SingleOrDefaultAsync();
            return item is null ? Results.NotFound(new ApiError("Prijava nije pronađena.")) : Results.Ok(item);
        });
        api.MapPost("/", async (CreateVehicleIssueRequest request, ClaimsPrincipal actor, AppDbContext db) =>
        {
            var description = request.Description?.Trim();
            if (request.VehicleId == Guid.Empty || !VehicleIssueCategories.IsValid(request.Category) ||
                string.IsNullOrWhiteSpace(description) || description.Length > 4000)
                return Results.BadRequest(new ApiError("Izaberite vozilo, vrstu prijave i unesite opis do 4000 znakova."));
            if (!await db.Vehicles.AnyAsync(x => x.Id == request.VehicleId && x.IsActive))
                return Results.BadRequest(new ApiError("Izabrano vozilo nije aktivno ili ne postoji."));
            var issue = new VehicleIssue { VehicleId = request.VehicleId, ReporterUserId = actor.UserId(),
                Category = request.Category, Description = description };
            db.VehicleIssues.Add(issue);
            db.AuditLogs.Add(Audit(actor, issue, "Prijavljen problem na vozilu", VehicleIssueCategories.Label(issue.Category)));
            await db.SaveChangesAsync();
            return Results.Created($"/api/vehicle-issues/{issue.Id}",
                await Dtos(db.VehicleIssues.AsNoTracking().Where(x => x.Id == issue.Id)).SingleAsync());
        });
        api.MapPut("/{id:guid}", async (Guid id, UpdateVehicleIssueRequest request, ClaimsPrincipal actor, AppDbContext db) =>
        {
            var note = request.AdminNote?.Trim();
            if (!VehicleIssueStatuses.IsValid(request.Status) || request.Version < 1 || note?.Length > 4000 ||
                (request.Status == VehicleIssueStatuses.Resolved && string.IsNullOrWhiteSpace(note)))
                return Results.BadRequest(new ApiError("Izaberite ispravan status. Za rješavanje prijave obavezna je napomena (do 4000 znakova)."));
            var issue = await db.VehicleIssues.SingleOrDefaultAsync(x => x.Id == id);
            if (issue is null) return Results.NotFound(new ApiError("Prijava nije pronađena."));
            if (issue.Version != request.Version) return Conflict();
            var previous = issue.Status;
            issue.Status = request.Status; issue.AdminNote = string.IsNullOrWhiteSpace(note) ? null : note;
            issue.UpdatedByUserId = actor.UserId(); issue.UpdatedUtc = DateTime.UtcNow; issue.Version++;
            issue.ResolvedUtc = request.Status == VehicleIssueStatuses.Resolved ? issue.ResolvedUtc ?? issue.UpdatedUtc : null;
            db.AuditLogs.Add(Audit(actor, issue, "Obrađena prijava problema", $"{VehicleIssueStatuses.Label(previous)} → {VehicleIssueStatuses.Label(issue.Status)}"));
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.Ok(await Dtos(db.VehicleIssues.AsNoTracking().Where(x => x.Id == id)).SingleAsync());
        }).RequireAuthorization(p => p.RequireRole(Roles.Administrator));
    }
    private static IResult Conflict() => Results.Conflict(new ApiError("Prijavu je u međuvremenu izmijenio drugi administrator. Osvježite pregled prije nove izmjene."));
    private static IQueryable<VehicleIssue> Visible(AppDbContext db, ClaimsPrincipal actor) =>
        actor.IsInRole(Roles.Administrator) ? db.VehicleIssues.AsNoTracking() :
            db.VehicleIssues.AsNoTracking().Where(x => x.ReporterUserId == actor.UserId());
    private static IQueryable<VehicleIssueDto> Dtos(IQueryable<VehicleIssue> query) => query.Select(x =>
        new VehicleIssueDto(x.Id, x.VehicleId, x.Vehicle.MakeModel, x.Vehicle.Registration, x.ReporterUserId, x.ReporterUser.FullName,
            x.Category, x.Description, x.Status, x.AdminNote, x.CreatedUtc, x.UpdatedUtc, x.ResolvedUtc,
            x.UpdatedByUser == null ? null : x.UpdatedByUser.FullName, x.Version));
    private static AuditLog Audit(ClaimsPrincipal actor, VehicleIssue issue, string action, string details) => new()
    {
        ActorUserId = actor.UserId(), ActorName = actor.DisplayName(), Action = action,
        EntityType = "VehicleIssue", EntityId = issue.Id.ToString(), Details = details
    };
}

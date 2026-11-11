using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using FleetBooking.Api.Data;
using FleetBooking.Api.Models;
using FleetBooking.Api.Services;
using FleetBooking.Shared;
using FleetBooking.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;


// This portfolio build accepts only its own configuration and local demo database.
// It never loads production settings, user secrets or command-line settings.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [], ContentRootPath = AppContext.BaseDirectory
});
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "FLEET_");
DemoSafety.ValidateConfiguration(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString("FleetBooking")!;
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret)) jwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
if (Encoding.UTF8.GetByteCount(jwtSecret) < 32) throw new InvalidOperationException("Jwt:Secret must contain at least 32 bytes.");
builder.Configuration["Jwt:Secret"] = jwtSecret;
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(DemoSafety.Port, listener => listener.UseHttps()));
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
LoginProtection.Configure(builder);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"], ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!)), ClockSkew = TimeSpan.FromMinutes(1)
    };
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var idText = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(idText, out var id)) { context.Fail("Neispravan korisnički identitet."); return; }
            var database = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var current = await database.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.IsActive, x.Role, x.MustChangePassword }).SingleOrDefaultAsync();
            if (current is null || !current.IsActive || !context.Principal!.IsInRole(current.Role)) context.Fail("Nalog nije aktivan ili je uloga promijenjena.");
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddHostedService(services => services.GetRequiredService<DatabaseBackupService>());

var app = builder.Build();
app.UseExceptionHandler(exceptionHandlerApp => exceptionHandlerApp.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch { UnauthorizedAccessException => 403, RuleException => 409, _ => 500 };
    await context.Response.WriteAsJsonAsync(new ApiError(error is RuleException ? error.Message : error is UnauthorizedAccessException ? "Nemate ovlašćenje za ovu radnju." : "Došlo je do greške na serveru."));
}));
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["X-Fleet-Demo"] = "synthetic-data-only";
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (builder.Configuration.GetValue("Database:AutoMigrate", true))
    {
        await DemoSafety.PrepareDatabaseAsync(db);
        await db.Database.MigrateAsync();
    }
    if (builder.Configuration.GetValue("Demo:Seed", true))
        await DemoSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>(), builder.Configuration["Demo:Password"]);

}

app.MapGet("/api/health", () => Results.Ok(new HealthDto("ok", DateTime.UtcNow, "1.0.0-demo"))).AllowAnonymous();
app.MapVehicleIssueEndpoints();
app.MapGet("/api/system/backup", (DatabaseBackupService backups) => Results.Ok(backups.Status))
    .RequireAuthorization(x => x.RequireRole(Roles.Administrator));

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, AppDbContext db,
    IPasswordHasher<AppUser> hasher, TokenService tokens, LoginAttemptLimiter attempts) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
        string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
        return Results.BadRequest(new ApiError("Unesite ispravnu e-mail adresu i lozinku."));
    var email = request.Email.Trim().ToLowerInvariant();
    if (!attempts.TryAcquire(email, out var retrySeconds))
    {
        context.Response.Headers.RetryAfter = retrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new ApiError("Previše pokušaja prijave. Sačekajte i pokušajte ponovo."), statusCode: 429);
    }
    var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (user is null || !user.IsActive || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        return Results.Json(new ApiError("Pogrešna e-mail adresa ili lozinka."), statusCode: 401);
    return Results.Ok(new LoginResponse(tokens.Create(user, request.RememberMe), user.Id, user.FullName, user.Email, user.Role, user.MustChangePassword));
}).AllowAnonymous().RequireRateLimiting("login");

app.MapGet("/api/auth/session", async (ClaimsPrincipal principal, AppDbContext db) =>
{
    var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == principal.UserId());
    return Results.Ok(new CurrentUserDto(user.Id, user.FullName, user.Email, user.Role, user.MustChangePassword));
}).RequireAuthorization();

app.MapPost("/api/auth/change-password", async (ChangePasswordRequest request, ClaimsPrincipal principal, AppDbContext db, IPasswordHasher<AppUser> hasher) =>
{
    var user = await db.Users.SingleAsync(x => x.Id == principal.UserId());
    if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        return Results.BadRequest(new ApiError("Trenutna lozinka nije ispravna."));
    if (!ValidPassword(request.NewPassword)) return Results.BadRequest(new ApiError(PasswordPolicy.Message));
    user.PasswordHash = hasher.HashPassword(user, request.NewPassword); user.MustChangePassword = false;
    db.AuditLogs.Add(Audit(principal, "Promijenjena lozinka", "User", user.Id.ToString(), "Korisnik promijenio svoju lozinku"));
    await db.SaveChangesAsync(); return Results.NoContent();
}).RequireAuthorization();

var vehicleApi = app.MapGroup("/api/vehicles").RequireAuthorization();
vehicleApi.MapGet("/", async (DateTime? fromUtc, DateTime? toUtc, bool includeInactive, bool? availableOnly, ClaimsPrincipal principal, AppDbContext db) =>
{
    var query = db.Vehicles.AsNoTracking();
    if (!includeInactive || !principal.IsInRole(Roles.Administrator)) query = query.Where(x => x.IsActive);
    if (fromUtc.HasValue != toUtc.HasValue) return Results.BadRequest(new ApiError("Unesite početak i kraj perioda."));
    var from = fromUtc.HasValue ? AsUtc(fromUtc.Value) : DateTime.UtcNow;
    var to = toUtc.HasValue ? AsUtc(toUtc.Value) : from.AddTicks(1);
    BookingRules.ValidatePeriod(from, to, false);
    if (fromUtc.HasValue && availableOnly != false)
    {
        query = query.Where(v => !v.Reservations.Any(r => r.Status == ReservationStatuses.Active && r.StartUtc < to && r.EndUtc > from)
            && !v.Unavailabilities.Any(u => u.StartUtc < to && u.EndUtc > from)
            && (v.AssignedUserId == null || v.AssignedUserId == principal.UserId()));
    }
    var rows = await query.OrderBy(x => x.MakeModel).Select(x => new
    {
        Vehicle = new VehicleDto(x.Id, x.MakeModel, x.Registration, x.SeatCount, x.FuelType, x.PickupLocation, x.IsActive, x.Note,
            x.ImageContent != null, x.ImageUpdatedUtc, x.AssignedUserId, x.AssignedUser == null ? null : x.AssignedUser.FullName),
        Unavailable = x.Unavailabilities.Any(u => u.StartUtc < to && u.EndUtc > from),
        Reserved = x.Reservations.Any(r => r.Status == ReservationStatuses.Active && r.StartUtc < to && r.EndUtc > from)
    }).ToListAsync();
    return Results.Ok(rows.Select(x => x.Vehicle with
    {
        StatusCode = VehicleStates.Code(x.Vehicle.IsActive, x.Unavailable, x.Reserved, x.Vehicle.AssignedUserId),
        HasReservation = x.Reserved
    }));
});
vehicleApi.MapPost("/", async (SaveVehicleRequest request, ClaimsPrincipal p, AppDbContext db) =>
{
    var error = ValidateVehicle(request); if (error is not null) return Results.BadRequest(new ApiError(error));
    var registration = NormalizeRegistration(request.Registration);
    if (await db.Vehicles.AnyAsync(x => x.Registration == registration)) return Results.Conflict(new ApiError("Registarska oznaka već postoji."));
    var v = new Vehicle { MakeModel = request.MakeModel.Trim(), Registration = registration, SeatCount = request.SeatCount, FuelType = request.FuelType.Trim(), PickupLocation = request.PickupLocation.Trim(), Note = Clean(request.Note) };
    db.Vehicles.Add(v); db.AuditLogs.Add(Audit(p, "Dodato vozilo", "Vehicle", v.Id.ToString(), v.Registration)); await db.SaveChangesAsync(); return Results.Created($"/api/vehicles/{v.Id}", MapVehicle(v));
}).RequireAuthorization(x => x.RequireRole(Roles.Administrator));
vehicleApi.MapPut("/{id:guid}", async (Guid id, SaveVehicleRequest request, ClaimsPrincipal p, AppDbContext db) =>
{
    var error = ValidateVehicle(request); if (error is not null) return Results.BadRequest(new ApiError(error));
    var v = await db.Vehicles.FindAsync(id); if (v is null) return Results.NotFound(new ApiError("Vozilo ne postoji."));
    var registration = NormalizeRegistration(request.Registration);
    if (await db.Vehicles.AnyAsync(x => x.Id != id && x.Registration == registration)) return Results.Conflict(new ApiError("Registarska oznaka već postoji."));
    v.MakeModel = request.MakeModel.Trim(); v.Registration = registration; v.SeatCount = request.SeatCount; v.FuelType = request.FuelType.Trim(); v.PickupLocation = request.PickupLocation.Trim(); v.Note = Clean(request.Note);
    db.AuditLogs.Add(Audit(p, "Izmijenjeno vozilo", "Vehicle", id.ToString(), registration)); await db.SaveChangesAsync();
    if (v.AssignedUserId.HasValue) await db.Entry(v).Reference(x => x.AssignedUser).LoadAsync();
    return Results.Ok(MapVehicle(v));
}).RequireAuthorization(x => x.RequireRole(Roles.Administrator));
vehicleApi.MapPut("/{id:guid}/status/{active:bool}", async (Guid id, bool active, ClaimsPrincipal p, BookingService service, CancellationToken ct) =>
{
    await service.SetVehicleStatusAsync(id, active, p.UserId(), p.DisplayName(), ct); return Results.NoContent();
}).RequireAuthorization(x => x.RequireRole(Roles.Administrator));
vehicleApi.MapPut("/{id:guid}/assignment", async (Guid id, AssignVehicleRequest request, ClaimsPrincipal p, BookingService service, CancellationToken ct) =>
{
    await service.AssignVehicleAsync(id, request.UserId, p.UserId(), p.DisplayName(), ct); return Results.NoContent();
}).RequireAuthorization(x => x.RequireRole(Roles.Administrator));
vehicleApi.MapGet("/{id:guid}/image", async (Guid id, AppDbContext db) =>
{
    var image = await db.Vehicles.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.ImageContent, x.ImageContentType }).SingleOrDefaultAsync();
    return image?.ImageContent is null ? Results.NotFound() : Results.File(image.ImageContent, image.ImageContentType ?? "image/jpeg");
});
vehicleApi.MapPut("/{id:guid}/image", async (Guid id, SaveVehicleImageRequest request, ClaimsPrincipal p, AppDbContext db) =>
{
    var vehicle = await db.Vehicles.FindAsync(id); if (vehicle is null) return Results.NotFound(new ApiError("Vozilo ne postoji."));
    byte[] content;
    try { content = Convert.FromBase64String(request.Base64Data); }
    catch (FormatException) { return Results.BadRequest(new ApiError("Slika nije ispravna.")); }
    if (content.Length is 0 or > 3 * 1024 * 1024) return Results.BadRequest(new ApiError("Slika mora biti manja od 3 MB."));
    if (!ValidVehicleImage(content, request.ContentType)) return Results.BadRequest(new ApiError("Dozvoljene su samo ispravne JPG i PNG slike."));
    vehicle.ImageContent = content; vehicle.ImageContentType = request.ContentType.ToLowerInvariant(); vehicle.ImageUpdatedUtc = DateTime.UtcNow;
    db.AuditLogs.Add(Audit(p, "Promijenjena slika vozila", "Vehicle", id.ToString(), vehicle.Registration));
    await db.SaveChangesAsync(); return Results.NoContent();
}).RequireAuthorization(x => x.RequireRole(Roles.Administrator));
vehicleApi.MapDelete("/{id:guid}/image", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    var vehicle = await db.Vehicles.FindAsync(id); if (vehicle is null) return Results.NotFound(new ApiError("Vozilo ne postoji."));
    vehicle.ImageContent = null; vehicle.ImageContentType = null; vehicle.ImageUpdatedUtc = null;
    db.AuditLogs.Add(Audit(p, "Uklonjena slika vozila", "Vehicle", id.ToString(), vehicle.Registration));
    await db.SaveChangesAsync(); return Results.NoContent();
}).RequireAuthorization(x => x.RequireRole(Roles.Administrator));

var reservationApi = app.MapGroup("/api/reservations").RequireAuthorization();
reservationApi.MapGet("/mine", async (ClaimsPrincipal p, AppDbContext db) => Results.Ok(await ReservationQuery(db).Where(x => x.UserId == p.UserId()).OrderByDescending(x => x.StartUtc).Select(x => new ReservationDto(x.Id, x.VehicleId, x.Vehicle.MakeModel, x.Vehicle.Registration, x.UserId, x.User.FullName, x.StartUtc, x.EndUtc, x.Purpose, x.Destination, x.DepartureLocation, x.ContactPhone, x.PassengerCount, x.Note, x.Status, x.CancellationReason, x.CreatedUtc)).ToListAsync()));
reservationApi.MapGet("/all", async (AppDbContext db) => Results.Ok(await ReservationQuery(db).OrderByDescending(x => x.StartUtc).Select(x => new ReservationDto(x.Id, x.VehicleId, x.Vehicle.MakeModel, x.Vehicle.Registration, x.UserId, x.User.FullName, x.StartUtc, x.EndUtc, x.Purpose, x.Destination, x.DepartureLocation, x.ContactPhone, x.PassengerCount, x.Note, x.Status, x.CancellationReason, x.CreatedUtc)).ToListAsync())).RequireAuthorization(x => x.RequireRole(Roles.Administrator));
reservationApi.MapPost("/", async (SaveReservationRequest request, ClaimsPrincipal p, BookingService service, AppDbContext db, CancellationToken ct) =>
{
    var item = await service.CreateAsync(p.UserId(), Normalize(request), p.DisplayName(), ct);
    await db.Entry(item).Reference(x => x.Vehicle).LoadAsync(ct); await db.Entry(item).Reference(x => x.User).LoadAsync(ct);
    return Results.Created($"/api/reservations/{item.Id}", MapReservation(item));
});
reservationApi.MapPut("/{id:guid}", async (Guid id, SaveReservationRequest request, ClaimsPrincipal p, BookingService service, AppDbContext db, CancellationToken ct) =>
{
    var item = await service.UpdateAsync(id, p.UserId(), p.IsInRole(Roles.Administrator), Normalize(request), p.DisplayName(), ct);
    await db.Entry(item).Reference(x => x.Vehicle).LoadAsync(ct); await db.Entry(item).Reference(x => x.User).LoadAsync(ct); return Results.Ok(MapReservation(item));
});
reservationApi.MapPost("/{id:guid}/cancel", async (Guid id, CancelReservationRequest request, ClaimsPrincipal p, BookingService service, CancellationToken ct) =>
{
    await service.CancelAsync(id, p.UserId(), p.IsInRole(Roles.Administrator), p.DisplayName(), request.Reason, ct); return Results.NoContent();
});

app.MapGet("/api/occupancy", async (DateTime fromUtc, DateTime toUtc, ClaimsPrincipal p, AppDbContext db) =>
{
    var from = AsUtc(fromUtc); var to = AsUtc(toUtc); BookingRules.ValidatePeriod(from, to, false); var admin = p.IsInRole(Roles.Administrator);
    var reservations = await db.Reservations.AsNoTracking().Where(x => x.Status == ReservationStatuses.Active && x.StartUtc < to && x.EndUtc > from)
        .Select(x => new OccupancyDto(x.VehicleId, x.Vehicle.MakeModel + " (" + x.Vehicle.Registration + ")", x.StartUtc, x.EndUtc, "Rezervacija", admin ? x.User.FullName + " — " + x.Purpose : x.User.FullName)).ToListAsync();
    var unavailable = await db.Unavailabilities.AsNoTracking().Where(x => x.StartUtc < to && x.EndUtc > from)
        .Select(x => new OccupancyDto(x.VehicleId, x.Vehicle.MakeModel + " (" + x.Vehicle.Registration + ")", x.StartUtc, x.EndUtc, "Nedostupno", admin ? x.Reason : null)).ToListAsync();
    return Results.Ok(reservations.Concat(unavailable).OrderBy(x => x.StartUtc));
}).RequireAuthorization();

var unavailableApi = app.MapGroup("/api/unavailability").RequireAuthorization(x => x.RequireRole(Roles.Administrator));
unavailableApi.MapGet("/", async (AppDbContext db) => Results.Ok(await db.Unavailabilities.AsNoTracking().OrderByDescending(x => x.StartUtc).Select(x => new UnavailabilityDto(x.Id, x.VehicleId, x.Vehicle.MakeModel + " (" + x.Vehicle.Registration + ")", x.StartUtc, x.EndUtc, x.Reason)).ToListAsync()));
unavailableApi.MapPost("/", async (SaveUnavailabilityRequest request, ClaimsPrincipal p, BookingService service, AppDbContext db, CancellationToken ct) =>
{
    var item = await service.AddUnavailabilityAsync(p.UserId(), p.DisplayName(), new SaveUnavailabilityRequest(request.VehicleId, AsUtc(request.StartUtc), AsUtc(request.EndUtc), request.Reason), ct);
    await db.Entry(item).Reference(x => x.Vehicle).LoadAsync(ct); return Results.Created($"/api/unavailability/{item.Id}", new UnavailabilityDto(item.Id, item.VehicleId, item.Vehicle.MakeModel, item.StartUtc, item.EndUtc, item.Reason));
});
unavailableApi.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal p, AppDbContext db) =>
{
    var item = await db.Unavailabilities.FindAsync(id); if (item is null) return Results.NotFound(new ApiError("Period ne postoji."));
    db.Unavailabilities.Remove(item); db.AuditLogs.Add(Audit(p, "Uklonjen period nedostupnosti", "VehicleUnavailability", id.ToString(), item.Reason)); await db.SaveChangesAsync(); return Results.NoContent();
});

var usersApi = app.MapGroup("/api/users").RequireAuthorization(x => x.RequireRole(Roles.Administrator));
usersApi.MapGet("/", async (AppDbContext db) => Results.Ok(await db.Users.AsNoTracking().OrderBy(x => x.FullName).Select(x => new UserDto(x.Id, x.FullName, x.Email, x.Role, x.IsActive, x.MustChangePassword, x.Phone)).ToListAsync()));
usersApi.MapPost("/", async (CreateUserRequest request, ClaimsPrincipal p, AppDbContext db, IPasswordHasher<AppUser> hasher) =>
{
    var error = ValidateUser(request.FullName, request.Email, request.Role, request.TemporaryPassword); if (error is not null) return Results.BadRequest(new ApiError(error));
    var email = request.Email.Trim().ToLowerInvariant(); if (await db.Users.AnyAsync(x => x.Email == email)) return Results.Conflict(new ApiError("E-mail adresa već postoji."));
    var user = new AppUser { FullName = request.FullName.Trim(), Email = email, PasswordHash = "", Role = request.Role, Phone = Clean(request.Phone) };
    user.PasswordHash = hasher.HashPassword(user, request.TemporaryPassword); db.Users.Add(user); db.AuditLogs.Add(Audit(p, "Dodat korisnik", "User", user.Id.ToString(), user.Email + "; " + user.Role)); await db.SaveChangesAsync(); return Results.Created($"/api/users/{user.Id}", MapUser(user));
});
usersApi.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest request, ClaimsPrincipal p, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.FullName) || !ValidRole(request.Role)) return Results.BadRequest(new ApiError("Ime i ispravna uloga su obavezni."));
    var user = await db.Users.FindAsync(id); if (user is null) return Results.NotFound(new ApiError("Korisnik ne postoji."));
    if (id == p.UserId() && (!request.IsActive || request.Role != Roles.Administrator)) return Results.BadRequest(new ApiError("Ne možete deaktivirati svoj nalog niti ukloniti sopstvenu administratorsku ulogu."));
    user.FullName = request.FullName.Trim(); user.Role = request.Role; user.IsActive = request.IsActive; user.Phone = Clean(request.Phone);
    db.AuditLogs.Add(Audit(p, "Izmijenjen korisnik", "User", id.ToString(), $"{user.Email}; {user.Role}; aktivan={user.IsActive}")); await db.SaveChangesAsync(); return Results.Ok(MapUser(user));
});
usersApi.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest request, ClaimsPrincipal p, AppDbContext db, IPasswordHasher<AppUser> hasher) =>
{
    if (!ValidPassword(request.NewTemporaryPassword)) return Results.BadRequest(new ApiError(PasswordPolicy.Message));
    var user = await db.Users.FindAsync(id); if (user is null) return Results.NotFound(new ApiError("Korisnik ne postoji."));
    user.PasswordHash = hasher.HashPassword(user, request.NewTemporaryPassword); user.MustChangePassword = true;
    db.AuditLogs.Add(Audit(p, "Resetovana lozinka", "User", id.ToString(), user.Email)); await db.SaveChangesAsync(); return Results.NoContent();
});

app.MapGet("/api/audit", async (int? take, AppDbContext db) => Results.Ok(await db.AuditLogs.AsNoTracking().OrderByDescending(x => x.TimestampUtc).Take(Math.Clamp(take ?? 250, 1, 1000)).Select(x => new AuditDto(x.Id, x.TimestampUtc, x.ActorName, x.Action, x.EntityType, x.EntityId, x.Details)).ToListAsync()))
    .RequireAuthorization(x => x.RequireRole(Roles.Administrator));

app.Run();

static IQueryable<Reservation> ReservationQuery(AppDbContext db) => db.Reservations.AsNoTracking().Include(x => x.Vehicle).Include(x => x.User);
static VehicleDto MapVehicle(Vehicle x) => new(x.Id, x.MakeModel, x.Registration, x.SeatCount, x.FuelType, x.PickupLocation, x.IsActive, x.Note,
    x.ImageContent != null, x.ImageUpdatedUtc, x.AssignedUserId, x.AssignedUser?.FullName);
static UserDto MapUser(AppUser x) => new(x.Id, x.FullName, x.Email, x.Role, x.IsActive, x.MustChangePassword, x.Phone);
static ReservationDto MapReservation(Reservation x) => new(x.Id, x.VehicleId, x.Vehicle.MakeModel, x.Vehicle.Registration, x.UserId, x.User.FullName, x.StartUtc, x.EndUtc, x.Purpose, x.Destination, x.DepartureLocation, x.ContactPhone, x.PassengerCount, x.Note, x.Status, x.CancellationReason, x.CreatedUtc);
static SaveReservationRequest Normalize(SaveReservationRequest x) => x with { StartUtc = AsUtc(x.StartUtc), EndUtc = AsUtc(x.EndUtc) };
static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
static string NormalizeRegistration(string value) => value.Trim().ToUpperInvariant().Replace(" ", "");
static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
static bool ValidRole(string role) => role is Roles.Administrator or Roles.Employee;
static bool ValidPassword(string password) => PasswordPolicy.IsValid(password);
static bool ValidVehicleImage(byte[] content, string contentType)
{
    var type = contentType.Trim().ToLowerInvariant();
    var jpeg = type == "image/jpeg" && content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF;
    var png = type == "image/png" && content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A;
    return jpeg || png;
}
static string? ValidateVehicle(SaveVehicleRequest x) => !DemoSafety.IsDemoRegistration(x.Registration) ? "Demo oznaka mora biti DEMO- i tri cifre." : string.IsNullOrWhiteSpace(x.MakeModel) || string.IsNullOrWhiteSpace(x.Registration) || string.IsNullOrWhiteSpace(x.FuelType) || string.IsNullOrWhiteSpace(x.PickupLocation) ? "Sva obavezna polja vozila moraju biti popunjena." : x.SeatCount is < 1 or > 100 ? "Broj sjedišta mora biti između 1 i 100." : null;
static string? ValidateUser(string name, string email, string role, string password) => !DemoSafety.IsDemoEmail(email) ? "Demo koristi samo adrese na example.com." : string.IsNullOrWhiteSpace(name) || !email.Contains('@') || !ValidRole(role) ? "Ime, ispravna e-mail adresa i uloga su obavezni." : !ValidPassword(password) ? PasswordPolicy.Message : null;
static AuditLog Audit(ClaimsPrincipal p, string action, string type, string id, string details) => new() { ActorUserId = p.UserId(), ActorName = p.DisplayName(), Action = action, EntityType = type, EntityId = id, Details = details };

public partial class Program { }

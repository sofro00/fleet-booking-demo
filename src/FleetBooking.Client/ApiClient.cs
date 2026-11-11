using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetBooking.Shared;

namespace FleetBooking.Client;

public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public LoginResponse? Session { get; private set; }
    public string? LastRestoreFailureMessage { get; private set; }
    public Uri ServerAddress => _http.BaseAddress!;
    public ApiClient(Uri baseAddress)
    {
        if (!DemoEndpoint.IsAllowed(baseAddress))
            throw new InvalidOperationException("Fleet Booking Demo uses only https://localhost:58443/.");
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task LoginAsync(string email, string password, bool rememberMe)
    {
        var response = await SendAsync(HttpMethod.Post, "api/auth/login", new LoginRequest(email, password, rememberMe), false);
        Session = await ReadAsync<LoginResponse>(response);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Session.Token);
        if (rememberMe)
        {
            SessionStore.Save(Session);
            SessionStore.SaveCredentials(email, password);
        }
        else SessionStore.ClearAll();
    }
    public async Task<bool> TryRestoreSessionAsync()
    {
        LastRestoreFailureMessage = null;
        var stored = SessionStore.Load(); if (stored is null) return false;
        Session = stored; _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stored.Token);
        try
        {
            var current = await GetAsync<CurrentUserDto>("api/auth/session");
            Session = new LoginResponse(stored.Token, current.UserId, current.FullName, current.Email, current.Role, current.MustChangePassword);
            SessionStore.Save(Session); return true;
        }
        catch (ApiException ex) when (ex.IsAuthenticationFailure)
        {
            Logout();
            LastRestoreFailureMessage = "Sačuvana sesija je istekla. Prijavite se ponovo.";
            return false;
        }
        catch (Exception ex)
        {
            Session = null;
            _http.DefaultRequestHeaders.Authorization = null;
            LastRestoreFailureMessage = $"Nalog je zapamćen, ali automatska prijava nije moguća jer server nije dostupan. Pokrenite API server i pokušajte ponovo. ({ex.Message})";
            return false;
        }
    }
    public void Logout()
    {
        SessionStore.ClearSession(); Session = null; _http.DefaultRequestHeaders.Authorization = null;
    }
    public async Task ChangePasswordAsync(ChangePasswordRequest x)
    {
        await NoContentAsync(HttpMethod.Post, "api/auth/change-password", x);
        var saved = SessionStore.LoadCredentials();
        if (saved is not null) SessionStore.SaveCredentials(Session?.Email ?? saved.Email, x.NewPassword);
    }
    public Task<List<VehicleDto>> GetVehiclesAsync(DateTime? from = null, DateTime? to = null, bool includeInactive = false, bool availableOnly = true)
    {
        var query = $"?includeInactive={includeInactive.ToString().ToLowerInvariant()}&availableOnly={availableOnly.ToString().ToLowerInvariant()}";
        if (from.HasValue && to.HasValue) query += $"&fromUtc={Uri.EscapeDataString(LocalTime.ToUtc(from.Value).ToString("O"))}&toUtc={Uri.EscapeDataString(LocalTime.ToUtc(to.Value).ToString("O"))}";
        return GetAsync<List<VehicleDto>>("api/vehicles" + query);
    }
    public Task<VehicleDto> AddVehicleAsync(SaveVehicleRequest x) => PostAsync<VehicleDto>("api/vehicles", x);
    public Task<VehicleDto> UpdateVehicleAsync(Guid id, SaveVehicleRequest x) => PutAsync<VehicleDto>($"api/vehicles/{id}", x);
    public Task SetVehicleStatusAsync(Guid id, bool active) => NoContentAsync(HttpMethod.Put, $"api/vehicles/{id}/status/{active.ToString().ToLowerInvariant()}");
    public Task AssignVehicleAsync(Guid id, Guid? userId) => NoContentAsync(HttpMethod.Put, $"api/vehicles/{id}/assignment", new AssignVehicleRequest(userId));
    public Task SaveVehicleImageAsync(Guid id, byte[] content, string contentType = "image/jpeg") => NoContentAsync(HttpMethod.Put, $"api/vehicles/{id}/image", new SaveVehicleImageRequest(contentType, Convert.ToBase64String(content)));
    public Task DeleteVehicleImageAsync(Guid id) => NoContentAsync(HttpMethod.Delete, $"api/vehicles/{id}/image");
    public async Task<byte[]> GetVehicleImageAsync(Guid id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/vehicles/{id}/image");
        return await response.Content.ReadAsByteArrayAsync();
    }
    public Task<List<ReservationDto>> GetMineAsync() => GetAsync<List<ReservationDto>>("api/reservations/mine");
    public Task<List<ReservationDto>> GetAllReservationsAsync() => GetAsync<List<ReservationDto>>("api/reservations/all");
    public Task<ReservationDto> AddReservationAsync(SaveReservationRequest x) => PostAsync<ReservationDto>("api/reservations", x);
    public Task<ReservationDto> UpdateReservationAsync(Guid id, SaveReservationRequest x) => PutAsync<ReservationDto>($"api/reservations/{id}", x);
    public Task CancelReservationAsync(Guid id, string reason) => NoContentAsync(HttpMethod.Post, $"api/reservations/{id}/cancel", new CancelReservationRequest(reason));
    public Task<List<OccupancyDto>> GetOccupancyAsync(DateTime from, DateTime to) => GetAsync<List<OccupancyDto>>($"api/occupancy?fromUtc={Uri.EscapeDataString(LocalTime.ToUtc(from).ToString("O"))}&toUtc={Uri.EscapeDataString(LocalTime.ToUtc(to).ToString("O"))}");
    public Task<List<UserDto>> GetUsersAsync() => GetAsync<List<UserDto>>("api/users");
    public Task<UserDto> AddUserAsync(CreateUserRequest x) => PostAsync<UserDto>("api/users", x);
    public Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest x) => PutAsync<UserDto>($"api/users/{id}", x);
    public Task ResetPasswordAsync(Guid id, string password) => NoContentAsync(HttpMethod.Post, $"api/users/{id}/reset-password", new ResetPasswordRequest(password));
    public Task<List<UnavailabilityDto>> GetUnavailabilityAsync() => GetAsync<List<UnavailabilityDto>>("api/unavailability");
    public Task<UnavailabilityDto> AddUnavailabilityAsync(SaveUnavailabilityRequest x) => PostAsync<UnavailabilityDto>("api/unavailability", x);
    public Task DeleteUnavailabilityAsync(Guid id) => NoContentAsync(HttpMethod.Delete, $"api/unavailability/{id}");
    public Task<List<AuditDto>> GetAuditAsync() => GetAsync<List<AuditDto>>("api/audit?take=500");
    public Task<BackupStatusDto> GetBackupStatusAsync() => GetAsync<BackupStatusDto>("api/system/backup");
    public Task<List<VehicleIssueDto>> GetVehicleIssuesAsync() => GetAsync<List<VehicleIssueDto>>("api/vehicle-issues");
    public Task<VehicleIssueDto> AddVehicleIssueAsync(CreateVehicleIssueRequest request) => PostAsync<VehicleIssueDto>("api/vehicle-issues", request);
    public Task<VehicleIssueDto> UpdateVehicleIssueAsync(Guid id, UpdateVehicleIssueRequest request) => PutAsync<VehicleIssueDto>($"api/vehicle-issues/{id}", request);
    public async Task<HealthDto> GetHealthAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync("api/health", timeout.Token);
        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<HealthDto>(_json, timeout.Token);
        if (health?.Status != "ok") throw new ApiException("Server nije spreman.");
        return health;
    }

    private async Task<T> GetAsync<T>(string url) => await ReadAsync<T>(await SendAsync(HttpMethod.Get, url));
    private async Task<T> PostAsync<T>(string url, object body) => await ReadAsync<T>(await SendAsync(HttpMethod.Post, url, body));
    private async Task<T> PutAsync<T>(string url, object body) => await ReadAsync<T>(await SendAsync(HttpMethod.Put, url, body));
    private async Task NoContentAsync(HttpMethod method, string url, object? body = null) => _ = await SendAsync(method, url, body);
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool authorized = true)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode) return response;
            var error = await response.Content.ReadFromJsonAsync<ApiError>(_json);
            if (response.StatusCode == HttpStatusCode.Unauthorized && authorized) throw new ApiException("Sesija je istekla. Prijavite se ponovo.", true);
            throw new ApiException(error?.Message ?? $"Server je vratio grešku {(int)response.StatusCode}.");
        }
        catch (ApiException) { throw; }
        catch (TaskCanceledException) { throw new ApiException("Server nije odgovorio na vrijeme. Rezultat nije sačuvan kao uspješan."); }
        catch (HttpRequestException) { throw new ApiException("Server nije dostupan. Provjerite mrežnu vezu; rezultat nije sačuvan kao uspješan."); }
    }
    private async Task<T> ReadAsync<T>(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<T>(_json) ?? throw new ApiException("Server je vratio prazan odgovor.");
}

public sealed class ApiException(string message, bool isAuthenticationFailure = false) : Exception(message)
{
    public bool IsAuthenticationFailure { get; } = isAuthenticationFailure;
}

public static class LocalTime
{
    public static DateTime Now => FromUtc(DateTime.UtcNow);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    public static DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
    public static string Show(DateTime utc) => FromUtc(utc).ToString("dd.MM.yyyy HH:mm");
}

public sealed record ReservationRow(ReservationDto Item)
{
    public string Vehicle => $"{Item.Vehicle} ({Item.Registration})"; public string Employee => Item.EmployeeName;
    public string Start => LocalTime.Show(Item.StartUtc); public string End => LocalTime.Show(Item.EndUtc);
    public string Purpose => Item.Purpose; public string Destination => Item.Destination; public string Status => Item.Status == ReservationStatuses.Active ? "Aktivna" : "Otkazana";
}
public sealed record OccupancyRow(OccupancyDto Item)
{
    public string Vehicle => Item.Vehicle;
    public string Start => LocalTime.Show(Item.StartUtc);
    public string End => LocalTime.Show(Item.EndUtc);
    public string Type => Item.Type;
    public string Description => Item.Description ?? "";
    public string ReservedBy => Item.Type == "Rezervacija"
        ? (Item.Description?.Split(" — ", 2, StringSplitOptions.None)[0] ?? "—")
        : "—";
}
public sealed record UnavailabilityRow(UnavailabilityDto Item) { public string Vehicle => Item.Vehicle; public string Start => LocalTime.Show(Item.StartUtc); public string End => LocalTime.Show(Item.EndUtc); public string Reason => Item.Reason; }
public sealed record AuditRow(AuditDto Item) { public string Time => LocalTime.Show(Item.TimestampUtc); public string Actor => Item.ActorName; public string Action => Item.Action; public string Entity => Item.EntityType; public string Details => Item.Details; }

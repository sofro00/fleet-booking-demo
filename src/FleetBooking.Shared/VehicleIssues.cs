namespace FleetBooking.Shared;

public static class VehicleIssueCategories
{
    public const string Fault = "Fault", Damage = "Damage", MissingEquipment = "MissingEquipment", Other = "Other";
    public static IReadOnlyList<KeyValuePair<string, string>> Options { get; } =
        [new(Fault, "Kvar"), new(Damage, "Oštećenje"), new(MissingEquipment, "Nedostaje oprema"), new(Other, "Ostalo")];
    public static bool IsValid(string? value) => Options.Any(x => x.Key == value);
    public static string Label(string value) => Options.FirstOrDefault(x => x.Key == value).Value ?? value;
}
public static class VehicleIssueStatuses
{
    public const string Open = "Open", InProgress = "InProgress", Resolved = "Resolved";
    public static IReadOnlyList<KeyValuePair<string, string>> Options { get; } =
        [new(Open, "Nova"), new(InProgress, "U toku"), new(Resolved, "Riješena")];
    public static bool IsValid(string? value) => Options.Any(x => x.Key == value);
    public static string Label(string value) => Options.FirstOrDefault(x => x.Key == value).Value ?? value;
}
public record VehicleIssueDto(Guid Id, Guid VehicleId, string Vehicle, string Registration, Guid ReporterUserId,
    string ReporterName, string Category, string Description, string Status, string? AdminNote,
    DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? ResolvedUtc, string? UpdatedByName, long Version);
public record CreateVehicleIssueRequest(Guid VehicleId, string Category, string Description);
public record UpdateVehicleIssueRequest(string Status, string? AdminNote, long Version);

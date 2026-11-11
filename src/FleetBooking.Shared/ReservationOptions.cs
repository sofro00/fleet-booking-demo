namespace FleetBooking.Shared;

public static class ReservationOptions
{
    public const string DefaultDeparture = "Podgorica";
    public static IReadOnlyList<string> Purposes { get; } = Array.AsReadOnly(new[]
    {
        "Terenski rad", "Prijem robe", "Obilazak radnje", "Dostava i prevoz robe",
        "Transfer robe između radnji", "IT podrška i intervencija", "Servis i održavanje",
        "Službeni sastanak", "Obuka zaposlenih", "Administrativni poslovi", "Ostalo"
    });

    // Ministry of Public Administration: gov.me/clanak/drzavna-uprava-i-lokalne-samouprave
    public static IReadOnlyList<string> Municipalities { get; } = Array.AsReadOnly(new[]
    {
        "Andrijevica", "Bar", "Berane", "Bijelo Polje", "Budva", "Cetinje", "Danilovgrad",
        "Gusinje", "Herceg Novi", "Kolašin", "Kotor", "Mojkovac", "Nikšić", "Petnjica",
        "Plav", "Pljevlja", "Plužine", "Podgorica", "Rožaje", "Šavnik", "Tivat", "Tuzi",
        "Ulcinj", "Zeta", "Žabljak"
    });

    // Existing free-text reservations must remain editable without losing their original details.
    public static IReadOnlyList<string> WithExisting(IReadOnlyList<string> options, string? existing) =>
        string.IsNullOrWhiteSpace(existing) || options.Contains(existing) ? options : options.Append(existing).ToArray();
}

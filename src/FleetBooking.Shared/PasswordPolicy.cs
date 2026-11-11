namespace FleetBooking.Shared;

public static class PasswordPolicy
{
    public const string Message = "Lozinka mora imati najmanje 8 znakova, veliko i malo slovo i poseban znak.";

    public static bool IsValid(string? password) =>
        password is { Length: >= 8 } && password.Any(char.IsUpper) && password.Any(char.IsLower) &&
        password.Any(ch => !char.IsLetterOrDigit(ch));
}

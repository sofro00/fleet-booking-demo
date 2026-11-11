namespace FleetBooking.Shared;

public static class DemoEndpoint
{
    public const int Port = 58443;

    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        uri.Port == Port && uri.AbsolutePath == "/" && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
}

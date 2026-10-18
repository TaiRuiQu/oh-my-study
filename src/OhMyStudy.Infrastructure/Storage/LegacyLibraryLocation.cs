namespace OhMyStudy.Infrastructure.Storage;

public static class LegacyLibraryLocation
{
    // Pure resolution only: do not create an empty directory and conceal the old data.
    // A release installer must still verify Tauri's actual installed identity in P9.
    public static string ResolveDefault(string roamingAppData)
        => Path.GetFullPath(Path.Combine(roamingAppData, "io.ohmystudy.desktop"));

    public static string DatabaseFile(string libraryDirectory)
        => Path.Combine(Path.GetFullPath(libraryDirectory), "study.sqlite3");
}

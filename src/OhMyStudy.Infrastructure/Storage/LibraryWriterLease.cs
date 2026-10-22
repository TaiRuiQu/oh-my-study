namespace OhMyStudy.Infrastructure.Storage;

/// <summary>Excludes cooperating native clients. The old client does not honor this lease.</summary>
public sealed class LibraryWriterLease : IDisposable
{
    private readonly FileStream stream;
    private LibraryWriterLease(FileStream stream) => this.stream = stream;

    public static LibraryWriterLease Acquire(string libraryDirectory)
    {
        var path = Path.GetFullPath(libraryDirectory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Library directories must not contain reparse points");
        try
        {
            return new(new FileStream(Path.Combine(path, ".native-writer.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException("The library is in use by another native client, or the write lock could not be acquired", exception);
        }
    }

    public void Dispose() => stream.Dispose();
}

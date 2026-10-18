namespace OhMyStudy.Infrastructure.Execution;

public static class RuntimePaths
{
    public static string CheckedDirectory(string path, bool create = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("需要绝对路径。", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current))
            {
                if (!create) throw new DirectoryNotFoundException(current);
                Directory.CreateDirectory(current);
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("任务目录不能使用链接或重定向目录。");
        }
        return full;
    }

    public static string CheckedFile(string path)
    {
        var full = Path.GetFullPath(path);
        CheckedDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full)) throw new FileNotFoundException("所需文件不存在。", full);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能使用链接文件。");
        return full;
    }
}

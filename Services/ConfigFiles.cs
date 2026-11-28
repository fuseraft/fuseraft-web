using System.Security.Cryptography;
using System.Text;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>A config file as the editor opened it: <c>Stamp</c> is null when the file doesn't exist yet.</summary>
public sealed record ConfigFileText(string Path, string Text, string? Stamp);

/// <summary>The file changed on disk after the editor opened it.</summary>
public sealed class ConfigFileConflictException(string path)
    : Exception($"{System.IO.Path.GetFileName(path)} changed on disk since you opened it.");

/// <summary>Reads and writes team files and markdown agent files for the config editor, inside the user's roots.</summary>
public sealed class ConfigFiles(WorkspaceAccess access)
{
    public const int MaxBytes = 1024 * 1024;

    private static readonly string[] Editable = [".yaml", ".yml", ".json", ".md"];

    public async Task<ConfigFileText> ReadAsync(string path)
    {
        var full = Checked(path);
        if (!File.Exists(full)) return new(full, "", null);
        var bytes = await File.ReadAllBytesAsync(full);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException($"{Path.GetFileName(full)} is too large to edit here.");
        return new(full, Encoding.UTF8.GetString(bytes), Stamp(bytes));
    }

    /// <summary>
    /// Writes <paramref name="text"/> when the file still matches <paramref name="expectedStamp"/> (null: it must not exist),
    /// unless <paramref name="overwrite"/>. Returns the new stamp.
    /// </summary>
    public async Task<string> SaveAsync(string path, string text, string? expectedStamp, bool overwrite = false)
    {
        var full = Checked(path);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException($"{Path.GetFileName(full)} is too large to save here.");

        if (!overwrite)
        {
            var current = File.Exists(full) ? Stamp(await File.ReadAllBytesAsync(full)) : null;
            if (current != expectedStamp) throw new ConfigFileConflictException(full);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        finally { File.Delete(temp); }
        return Stamp(bytes);
    }

    private string Checked(string path)
    {
        var full = access.Check(path);
        if (!Editable.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Only .yaml, .yml, .json and .md files can be edited here.");
        return full;
    }

    private static string Stamp(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

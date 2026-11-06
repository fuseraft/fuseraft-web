using System.Reflection;

namespace fuseraft.Web.Services;

/// <summary><c>fuseraft-web --version</c> and <c>--help</c>: answered without starting the server or touching its data directory.</summary>
public static class InfoCommand
{
    private const string Usage = """
        Usage:
          fuseraft-web [--urls <url>] [--<section>:<key>=<value> ...]   run the server
          fuseraft-web users <command>                                    manage accounts (fuseraft-web users for the list)
          fuseraft-web --version                                          print the version
          fuseraft-web --help                                             print this help

        The server reads appsettings.json, then the environment (Fuseraft__DataDirectory for
        Fuseraft:DataDirectory), then the command line. Docs: https://fuseraft.ai/fuseraft-web
        """;

    /// <summary>The version and commit, like <c>fuseraft --version</c> prints: <c>0.3.0+&lt;commit hash&gt;</c>.</summary>
    public static string Version { get; } =
        typeof(InfoCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Handles a flag anywhere in the server's arguments and returns an exit code, or null to run the server.</summary>
    public static int? TryRun(string[] args, TextWriter output)
    {
        if (args is ["help"] || args.Any(a => a is "--help" or "-h"))
        {
            output.WriteLine($"fuseraft-web {Version}");
            output.WriteLine();
            output.Write(Usage);
            output.WriteLine();
            return 0;
        }
        if (args.Any(a => a is "--version" or "-v"))
        {
            output.WriteLine(Version);
            return 0;
        }
        return null;
    }
}

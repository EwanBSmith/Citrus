using System.Diagnostics;

namespace Citrus.Engine;

/// <summary>Records the checkout used for a run; null values mean Git metadata was unavailable.</summary>
public sealed record GitRevision(string? Commit, bool? Dirty);

/// <summary>Builds trusted external projects using their SDK and MSBuild configuration.</summary>
public static class StrategyProject
{
    /// <summary>Builds the selected project and asks MSBuild for its actual output path.</summary>
    public static string Build(string project)
    {
        project = Path.GetFullPath(project);
        if (!File.Exists(project) || Path.GetExtension(project) != ".csproj")
            throw new FileNotFoundException("Select an existing .csproj strategy project.", project);
        var directory = Path.GetDirectoryName(project)!;
        Execute("dotnet", directory, ["build", project, "-c", "Release", "--nologo", "-v:q"]);
        var output = Execute("dotnet", directory,
            ["msbuild", project, "-nologo", "-property:Configuration=Release", "-getProperty:TargetPath"]).Trim();
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
            throw new InvalidDataException("The strategy project must target one framework and produce a managed DLL. MSBuild returned: " + output);
        return Path.GetFullPath(output);
    }

    /// <summary>Captures the repository revision and working-tree state, including untracked files and submodule changes.</summary>
    public static GitRevision Revision(string path)
    {
        try
        {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
            return new(Execute("git", directory, ["rev-parse", "HEAD"]).Trim(),
                Execute("git", directory, ["status", "--porcelain", "--untracked-files=normal"]).Trim().Length != 0);
        }
        catch { return new(null, null); }
    }

    /// <summary>Runs a build tool without shell interpolation, draining both pipes and bounding execution time.</summary>
    private static string Execute(string executable, string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + executable);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(executable + " exceeded five minutes.");
        }
        var text = output.GetAwaiter().GetResult();
        var error = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidDataException($"{executable} failed ({process.ExitCode}).\n{text}\n{error}");
        return text;
    }
}

using System.Diagnostics;

namespace SemanticSearch;

/// <summary>
/// Reads commit history from a git repository by shelling out to <c>git log</c>. Each commit
/// becomes one passage (subject + body), located by its short SHA.
/// </summary>
public static class GitSource
{
    private const char FieldSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';

    public static IEnumerable<Passage> Enumerate(string repository, int maxCommits)
    {
        string format = string.Join(FieldSeparator, "%h", "%an", "%ad", "%s", "%b") + RecordSeparator;
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("log");
        startInfo.ArgumentList.Add("--no-merges");
        startInfo.ArgumentList.Add($"--max-count={maxCommits}");
        startInfo.ArgumentList.Add("--date=short");
        startInfo.ArgumentList.Add($"--pretty=format:{format}");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'git'. Is it installed and on PATH?");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git log failed for '{repository}': {error.Trim()}");
        }

        foreach (string raw in output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string record = raw.Trim('\n', '\r');
            if (record.Length == 0)
            {
                continue;
            }

            string[] fields = record.Split(FieldSeparator);
            if (fields.Length < 5)
            {
                continue;
            }

            string sha = fields[0];
            string author = fields[1];
            string date = fields[2];
            string subject = fields[3];
            string body = fields[4].Trim();

            string text = body.Length == 0 ? subject : $"{subject}\n\n{body}";
            string title = $"{subject}  ({author}, {date})";
            yield return new Passage("git", sha, title, text);
        }
    }
}

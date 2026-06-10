using System.Diagnostics;

namespace Srndx;

/// <summary>
/// Walks a directory tree and yields text passages. Files are split into passages on blank-line
/// boundaries, with adjacent blocks merged until a soft character budget is reached so each
/// passage is large enough to embed meaningfully yet still pinpoints a line range.
/// </summary>
/// <remarks>
/// When the root lies inside a git work tree, file discovery defers to git so that
/// <c>.gitignore</c> rules (including nested ignores, negations, and global excludes) are honored;
/// otherwise it falls back to a manual walk that skips common build and tooling directories.
/// </remarks>
public static class FileSource
{
    private const int MaxPassageChars = 1200;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".adoc", ".org",
        ".cs", ".fs", ".vb", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".py", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".swift", ".scala", ".dart",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".m", ".mm",
        ".json", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".xml", ".csproj", ".props", ".targets",
        ".html", ".htm", ".css", ".scss", ".sql", ".sh", ".bash", ".zsh", ".ps1", ".psm1",
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".vscode", ".idea", "packages", "dist", "out", "target",
    };

    public static IEnumerable<Passage> Enumerate(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        foreach (string file in EnumerateFiles(fullRoot))
        {
            foreach (Passage passage in ReadPassages(fullRoot, file))
            {
                yield return passage;
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
        => TryEnumerateGitFiles(root) ?? EnumerateFilesByWalk(root);

    /// <summary>
    /// Yields passages for a single file when it is indexable (text extension, not ignored by git or
    /// located in a skipped directory). Returns nothing otherwise. Used to incrementally re-index a
    /// file that changed on disk.
    /// </summary>
    public static IEnumerable<Passage> EnumerateFile(string root, string file, bool checkGitIgnore = true)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullFile = Path.GetFullPath(file);
        if (!ShouldIndex(fullRoot, fullFile, checkGitIgnore) || !File.Exists(fullFile))
        {
            return [];
        }

        return ReadPassages(fullRoot, fullFile);
    }

    /// <summary>The forward-slash path of <paramref name="file" /> relative to <paramref name="root" />.</summary>
    public static string RelativePath(string root, string file)
        => Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(file)).Replace('\\', '/');

    /// <summary>Whether a file should be indexed: text extension, not in a skipped directory, not git-ignored.</summary>
    /// <param name="checkGitIgnore">
    /// When <see langword="false" />, the git-ignore check (which shells out to git) is skipped. Callers that
    /// already know a file is tracked - or that verify ignore status out of band - pass <see langword="false" />
    /// to keep git off the hot path.
    /// </param>
    public static bool ShouldIndex(string root, string file, bool checkGitIgnore = true)
    {
        if (!TextExtensions.Contains(Path.GetExtension(file)))
        {
            return false;
        }

        if (IsInSkippedDirectory(root, file))
        {
            return false;
        }

        return !checkGitIgnore || !IsGitIgnored(root, file);
    }

    /// <summary>Whether <paramref name="file" /> is excluded by git's ignore rules (false if not in a git work tree).</summary>
    public static bool IsPathGitIgnored(string root, string file)
        => IsGitIgnored(Path.GetFullPath(root), Path.GetFullPath(file));

    /// <summary>Whether <paramref name="file" /> lies under a directory that is always skipped (e.g. <c>bin</c>, <c>.git</c>).</summary>
    public static bool IsInSkippedDirectory(string root, string file)
    {
        string relative = RelativePath(root, file);
        foreach (string segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SkipDirectories.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGitIgnored(string root, string file)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("check-ignore");
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(file);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Close stdin so git never inherits/blocks on the parent's console handle.
            process.StandardInput.Close();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            // 0 = ignored, 1 = not ignored, 128 = not a git repo (treat as not ignored).
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists tracked and untracked-but-not-ignored files under <paramref name="root" /> via git, so
    /// <c>.gitignore</c> is honored. Returns <see langword="null" /> when the root is not inside a git
    /// work tree or git is unavailable, signaling the caller to fall back to a manual walk.
    /// </summary>
    private static List<string>? TryEnumerateGitFiles(string root)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");
        startInfo.ArgumentList.Add("--cached");
        startInfo.ArgumentList.Add("--others");
        startInfo.ArgumentList.Add("--exclude-standard");

        string output;
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }

        var files = new List<string>();
        foreach (string relative in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TextExtensions.Contains(Path.GetExtension(relative)))
            {
                files.Add(Path.GetFullPath(Path.Combine(root, relative)));
            }
        }

        return files;
    }

    private static IEnumerable<string> EnumerateFilesByWalk(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string subDir in subDirs)
            {
                if (!SkipDirectories.Contains(Path.GetFileName(subDir)))
                {
                    stack.Push(subDir);
                }
            }

            foreach (string file in files)
            {
                if (TextExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<Passage> ReadPassages(string root, string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        string name = Path.GetFileName(file);

        var buffer = new List<string>();
        int blockStart = 1;
        int chars = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool blank = string.IsNullOrWhiteSpace(line);

            if (buffer.Count == 0)
            {
                if (blank)
                {
                    continue;
                }

                blockStart = i + 1;
            }

            buffer.Add(line);
            chars += line.Length + 1;

            bool boundary = blank && chars >= MaxPassageChars / 2;
            if (boundary || chars >= MaxPassageChars)
            {
                Passage? passage = Build(relative, name, buffer, blockStart, i + 1);
                if (passage is not null)
                {
                    yield return passage.Value;
                }

                buffer.Clear();
                chars = 0;
            }
        }

        if (buffer.Count > 0)
        {
            Passage? passage = Build(relative, name, buffer, blockStart, lines.Length);
            if (passage is not null)
            {
                yield return passage.Value;
            }
        }
    }

    private static Passage? Build(string relative, string name, List<string> lines, int startLine, int endLine)
    {
        string text = string.Join('\n', lines).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return new Passage("file", $"{relative}:{startLine}-{endLine}", name, text);
    }
}

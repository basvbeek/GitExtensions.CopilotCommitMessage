using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitExtensions.CopilotCommitMessage;

internal static class GitHelper
{
    private const int MaxDiffCharacters = 100_000;
    private const int MaxRecentCommitCharacters = 12_000;
    private const int MaxHistoryScanCharacters = 64_000;
    private const int MaxFileContextCharacters = 12_000;
    private const int MaxTotalFileContextCharacters = 48_000;
    private const int MaxErrorCharacters = 16_000;

    internal static async Task<CommitDiff> GetCommitDiffAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        CommitDiff stagedDiff = await GetDiffAsync(workingDirectory, cached: true, cancellationToken).ConfigureAwait(false);
        CommitDiff selectedDiff = stagedDiff.HadChanges
            ? stagedDiff
            : await GetDiffAsync(workingDirectory, cached: false, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> untrackedCandidates = await GetUntrackedPathsAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> allowedUntrackedPaths = await FilterCopilotIgnoredPathsAsync(
            workingDirectory,
            untrackedCandidates,
            cancellationToken).ConfigureAwait(false);
        HashSet<string> changedPaths = new(selectedDiff.Paths, StringComparer.OrdinalIgnoreCase);
        List<string> untrackedPaths = new(allowedUntrackedPaths.Count);
        foreach (string path in allowedUntrackedPaths)
        {
            if (changedPaths.Add(path))
            {
                untrackedPaths.Add(path);
            }
        }

        return selectedDiff with
        {
            Paths = [.. selectedDiff.Paths, .. untrackedPaths],
            UntrackedPaths = untrackedPaths,
            ExcludedPathCount = selectedDiff.ExcludedPathCount + untrackedCandidates.Count - allowedUntrackedPaths.Count,
        };
    }

    private static async Task<CommitDiff> GetDiffAsync(
        string workingDirectory,
        bool cached,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> changedPaths = await GetChangedPathsAsync(workingDirectory, cached, cancellationToken).ConfigureAwait(false);
        if (changedPaths.Count == 0)
        {
            return new CommitDiff(string.Empty, false, cached ? CommitDiffSource.Staged : CommitDiffSource.TrackedWorkingTree, [], false, 0, []);
        }

        IReadOnlyList<string> allowedPaths = await FilterCopilotIgnoredPathsAsync(workingDirectory, changedPaths, cancellationToken).ConfigureAwait(false);
        if (allowedPaths.Count == 0)
        {
            return new CommitDiff(string.Empty, false, cached ? CommitDiffSource.Staged : CommitDiffSource.TrackedWorkingTree, [], true, changedPaths.Count, []);
        }

        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=true");
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("diff");
        if (cached)
        {
            startInfo.ArgumentList.Add("--cached");
        }

        startInfo.ArgumentList.Add("--no-color");
        startInfo.ArgumentList.Add("--no-ext-diff");
        startInfo.ArgumentList.Add("--no-textconv");
        startInfo.ArgumentList.Add("--unified=3");
        startInfo.ArgumentList.Add("--");
        foreach (string path in allowedPaths)
        {
            startInfo.ArgumentList.Add(":(literal)" + path);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start Git to read staged changes.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Could not start Git. Ensure Git is installed and available on PATH.", ex);
        }

        Task<BoundedText> outputTask = ReadBoundedAsync(process.StandardOutput, MaxDiffCharacters, true, process);
        Task<BoundedText> errorTask = ReadBoundedAsync(process.StandardError, MaxErrorCharacters, false, process);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("Reading changes from Git exceeded 30 seconds.");
        }

        BoundedText output = await outputTask.ConfigureAwait(false);
        BoundedText error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && !output.Truncated)
        {
            throw new InvalidOperationException("Git could not read changes: " + error.Text.Trim());
        }

        return new CommitDiff(
            output.Text,
            output.Truncated,
            cached ? CommitDiffSource.Staged : CommitDiffSource.TrackedWorkingTree,
            allowedPaths,
            true,
            changedPaths.Count - allowedPaths.Count,
            []);
    }

    internal static async Task<RepositoryContext> GetRepositoryContextAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("branch");
        startInfo.ArgumentList.Add("--show-current");

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start Git to read the current branch.");
        }

        Task<BoundedText> outputTask = ReadBoundedAsync(process.StandardOutput, 1_000, true, process);
        Task<BoundedText> errorTask = ReadBoundedAsync(process.StandardError, MaxErrorCharacters, false, process);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("Reading the current branch from Git exceeded 10 seconds.");
        }

        BoundedText output = await outputTask.ConfigureAwait(false);
        BoundedText error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || output.Truncated)
        {
            throw new InvalidOperationException("Git could not read the current branch: " + error.Text.Trim());
        }

        string repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        string repositoryName = new DirectoryInfo(repositoryPath).Name;
        string branchName = output.Text.Trim();
        if (branchName.Length == 0)
        {
            branchName = "detached HEAD";
        }

        return new RepositoryContext(repositoryName, branchName);
    }

    private static async Task<IReadOnlyList<string>> GetChangedPathsAsync(
        string workingDirectory,
        bool cached,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("diff");
        if (cached)
        {
            startInfo.ArgumentList.Add("--cached");
        }

        startInfo.ArgumentList.Add("--name-only");
        startInfo.ArgumentList.Add("-z");

        GitCommandResult result = await RunGitCommandAsync(
            startInfo,
            maxOutputCharacters: 200_000,
            timeoutDuration: TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Output.Truncated)
        {
            throw new InvalidOperationException("Git could not list changed files: " + result.Error.Trim());
        }

        return result.Output.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<IReadOnlyList<string>> GetUntrackedPathsAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("--others");
        startInfo.ArgumentList.Add("--exclude-standard");
        startInfo.ArgumentList.Add("--full-name");
        startInfo.ArgumentList.Add("-z");

        GitCommandResult result = await RunGitCommandAsync(
            startInfo,
            maxOutputCharacters: 200_000,
            timeoutDuration: TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Output.Truncated)
        {
            throw new InvalidOperationException("Git could not list untracked files: " + result.Error.Trim());
        }

        return result.Output.Text
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => !string.Equals(path, ".copilotignore", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> FilterCopilotIgnoredPathsAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return Array.Empty<string>();
        }

        string copilotIgnorePath = Path.Combine(workingDirectory, ".copilotignore");
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        if (File.Exists(copilotIgnorePath))
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.excludesFile=" + copilotIgnorePath);
        }

        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("check-ignore");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("-z");
        startInfo.ArgumentList.Add("--stdin");
        string input = string.Join('\0', paths) + '\0';
        GitCommandResult result = await RunGitCommandAsync(
            startInfo,
            maxOutputCharacters: 200_000,
            timeoutDuration: TimeSpan.FromSeconds(10),
            cancellationToken,
            input).ConfigureAwait(false);
        if (result.ExitCode > 1 || result.Output.Truncated)
        {
            throw new InvalidOperationException("Git could not apply Copilot file exclusions: " + result.Error.Trim());
        }

        HashSet<string> excludedPaths = new(
            result.Output.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        List<string> includedPaths = new(paths.Count);
        foreach (string path in paths)
        {
            if (!excludedPaths.Contains(path))
            {
                includedPaths.Add(path);
            }
        }

        return includedPaths;
    }

    internal static async Task<IReadOnlyList<FileContext>> GetFileContextsAsync(
        string workingDirectory,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> untrackedPaths,
        CancellationToken cancellationToken)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        HashSet<string> untrackedPathSet = new(untrackedPaths, StringComparer.OrdinalIgnoreCase);
        List<FileContext> contexts = new();
        int remainingCharacters = MaxTotalFileContextCharacters;

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingCharacters <= 0)
            {
                break;
            }

            string? fullPath = ResolveSafeWorkingTreeFilePath(root, path);
            if (fullPath is null || !File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                int fileLimit = Math.Min(MaxFileContextCharacters, remainingCharacters);
                StringBuilder content = new(Math.Min(fileLimit, 4_096));
                bool truncated = false;
                int lineNumber = 0;
                using StreamReader reader = new(fullPath, detectEncodingFromByteOrderMarks: true);
                while (content.Length < fileLimit)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Contains('\0'))
                    {
                        content.Clear();
                        break;
                    }

                    lineNumber++;
                    string numberedLine = lineNumber.ToString(CultureInfo.InvariantCulture) + ": " + line + "\n";
                    int remaining = fileLimit - content.Length;
                    content.Append(numberedLine.AsSpan(0, Math.Min(numberedLine.Length, remaining)));
                    if (numberedLine.Length > remaining)
                    {
                        truncated = true;
                        break;
                    }
                }

                bool isUntracked = untrackedPathSet.Contains(path);
                if (content.Length == 0 && !isUntracked)
                {
                    continue;
                }

                if (!truncated && reader.Peek() >= 0)
                {
                    truncated = true;
                }

                contexts.Add(new FileContext(path, content.ToString(), truncated, isUntracked));
                remainingCharacters -= content.Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return contexts;
    }

    private static string? ResolveSafeWorkingTreeFilePath(string repositoryRoot, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(repositoryRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        string currentPath = repositoryRoot;
        foreach (string segment in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.Exists(currentPath) || Directory.Exists(currentPath)) &&
                (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }
        }

        return fullPath;
    }

    internal static async Task<CommitHistory> GetCommitHistoryAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessStartInfo repositoryLogStartInfo = CreateGitStartInfo(workingDirectory);
        repositoryLogStartInfo.ArgumentList.Add("--no-pager");
        repositoryLogStartInfo.ArgumentList.Add("log");
        repositoryLogStartInfo.ArgumentList.Add("-5");
        repositoryLogStartInfo.ArgumentList.Add("--format=%s");
        GitCommandResult repositoryLog = await RunGitCommandAsync(
            repositoryLogStartInfo,
            MaxRecentCommitCharacters,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> repositorySubjects = repositoryLog.ExitCode == 0
            ? ReadSubjects(repositoryLog.Output.Text)
            : Array.Empty<string>();

        string? configuredAuthor = await GetConfiguredUserNameAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(configuredAuthor))
        {
            return new CommitHistory(repositorySubjects, Array.Empty<string>(), null);
        }

        ProcessStartInfo authorLogStartInfo = CreateGitStartInfo(workingDirectory);
        authorLogStartInfo.ArgumentList.Add("--no-pager");
        authorLogStartInfo.ArgumentList.Add("log");
        authorLogStartInfo.ArgumentList.Add("-200");
        authorLogStartInfo.ArgumentList.Add("--format=%an%x00%s");
        GitCommandResult authorLog = await RunGitCommandAsync(
            authorLogStartInfo,
            MaxHistoryScanCharacters,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        List<string> authorSubjects = new(5);
        if (authorLog.ExitCode == 0 || authorLog.Output.Truncated)
        {
            foreach (string record in authorLog.Output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = record.IndexOf('\0');
                if (separator <= 0 ||
                    !string.Equals(record[..separator], configuredAuthor, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string subject = record[(separator + 1)..].TrimEnd('\r');
                if (subject.Length > 0)
                {
                    authorSubjects.Add(subject);
                    if (authorSubjects.Count == 5)
                    {
                        break;
                    }
                }
            }
        }

        return new CommitHistory(repositorySubjects, authorSubjects, configuredAuthor);
    }

    private static IReadOnlyList<string> ReadSubjects(string output)
    {
        List<string> subjects = new(5);
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string subject = line.Trim();
            if (subject.Length > 0)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    private static async Task<string?> GetConfiguredUserNameAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        foreach (bool global in new[] { false, true })
        {
            ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
            if (global)
            {
                startInfo.Environment.Remove("GIT_CONFIG_GLOBAL");
            }

            startInfo.ArgumentList.Add("config");
            startInfo.ArgumentList.Add(global ? "--global" : "--local");
            startInfo.ArgumentList.Add("--get");
            startInfo.ArgumentList.Add("user.name");
            GitCommandResult result = await RunGitCommandAsync(
                startInfo,
                1_000,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            string name = result.Output.Text.Trim();
            if (result.ExitCode == 0 && name.Length > 0)
            {
                return name;
            }
        }

        return null;
    }

    private static async Task<GitCommandResult> RunGitCommandAsync(
        ProcessStartInfo startInfo,
        int maxOutputCharacters,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        startInfo.RedirectStandardInput = standardInput is not null;
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start Git.");
        }

        Task<BoundedText> outputTask = ReadBoundedAsync(process.StandardOutput, maxOutputCharacters, true, process);
        Task<BoundedText> errorTask = ReadBoundedAsync(process.StandardError, MaxErrorCharacters, false, process);
        using CancellationTokenSource timeout = new(timeoutDuration);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), linked.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("A Git command exceeded its time limit.");
        }

        BoundedText output = await outputTask.ConfigureAwait(false);
        BoundedText error = await errorTask.ConfigureAwait(false);
        return new GitCommandResult(output, error.Text, process.ExitCode);
    }

    internal sealed record CommitHistory(
        IReadOnlyList<string> RepositorySubjects,
        IReadOnlyList<string> AuthorSubjects,
        string? ConfiguredAuthorName);

    internal sealed record FileContext(string Path, string NumberedContent, bool Truncated, bool IsUntracked);

    internal sealed record CommitDiff(
        string Text,
        bool Truncated,
        CommitDiffSource Source,
        IReadOnlyList<string> Paths,
        bool HadChanges,
        int ExcludedPathCount,
        IReadOnlyList<string> UntrackedPaths);

    internal sealed record RepositoryContext(string RepositoryName, string BranchName);

    internal enum CommitDiffSource
    {
        Staged,
        TrackedWorkingTree,
    }

    private sealed record GitCommandResult(BoundedText Output, string Error, int ExitCode);

    private static ProcessStartInfo CreateGitStartInfo(string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, false),
            StandardErrorEncoding = new UTF8Encoding(false, false),
        };
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string tempDirectory = Path.GetTempPath();
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] = systemRoot;
        startInfo.Environment["WINDIR"] = systemRoot;
        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["TEMP"] = tempDirectory;
        startInfo.Environment["TMP"] = tempDirectory;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return startInfo;
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        StreamReader reader,
        int maxCharacters,
        bool terminateWhenFull,
        Process process)
    {
        char[] buffer = new char[4096];
        StringBuilder text = new(Math.Min(maxCharacters, 16_384));
        bool truncated = false;

        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int remaining = maxCharacters - text.Length;
            int toAppend = Math.Min(remaining, count);
            if (toAppend > 0)
            {
                text.Append(buffer, 0, toAppend);
            }

            if (toAppend < count)
            {
                truncated = true;
                if (terminateWhenFull)
                {
                    Kill(process);
                    break;
                }
            }
        }

        return new BoundedText(text.ToString(), truncated);
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new IOException("Git did not exit after it was terminated.");
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed record BoundedText(string Text, bool Truncated);
}
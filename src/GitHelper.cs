using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitExtensions.CopilotCommitMessage;

internal static class GitHelper
{
    private const int MaxDiffCharacters = 100_000;
    private const int MaxRecentCommitCharacters = 4_000;
    private const int MaxErrorCharacters = 16_000;

    internal static async Task<StagedDiff> GetStagedDiffAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=true");
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--cached");
        startInfo.ArgumentList.Add("--no-color");
        startInfo.ArgumentList.Add("--no-ext-diff");
        startInfo.ArgumentList.Add("--no-textconv");
        startInfo.ArgumentList.Add("--unified=3");

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

            throw new TimeoutException("Reading staged changes from Git exceeded 30 seconds.");
        }

        BoundedText output = await outputTask.ConfigureAwait(false);
        BoundedText error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && !output.Truncated)
        {
            throw new InvalidOperationException("Git could not read staged changes: " + error.Text.Trim());
        }

        return new StagedDiff(output.Text, output.Truncated);
    }

    internal static async Task<IReadOnlyList<string>> GetRecentCommitMessagesAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("log");
        startInfo.ArgumentList.Add("-2");
        startInfo.ArgumentList.Add("--format=%s");

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return Array.Empty<string>();
        }

        Task<BoundedText> outputTask = ReadBoundedAsync(process.StandardOutput, MaxRecentCommitCharacters, true, process);
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

            return Array.Empty<string>();
        }

        BoundedText output = await outputTask.ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || output.Truncated)
        {
            return Array.Empty<string>();
        }

        List<string> messages = new(2);
        foreach (string line in output.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string message = line.Trim();
            if (message.Length > 0)
            {
                messages.Add(message);
                if (messages.Count == 2)
                {
                    break;
                }
            }
        }

        return messages;
    }

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

    internal sealed record StagedDiff(string Text, bool Truncated);

    private sealed record BoundedText(string Text, bool Truncated);
}
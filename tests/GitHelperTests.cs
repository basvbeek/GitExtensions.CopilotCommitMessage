using System.Diagnostics;
using GitExtensions.CopilotCommitMessage;
using Xunit;

namespace GitExtensions.CopilotCommitMessage.Tests;

public sealed class GitHelperTests
{
    [Fact]
    public async Task GetStagedDiffAsync_ExcludesUnstagedEdits()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string file = Path.Combine(repository, "change.txt");

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(file, "staged line\n");
            RunGit(repository, "add", "--", "change.txt");
            await File.AppendAllTextAsync(file, "unstaged line\n");

            GitHelper.StagedDiff diff = await GitHelper.GetStagedDiffAsync(repository, CancellationToken.None);

            Assert.Contains("+staged line", diff.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("unstaged line", diff.Text, StringComparison.Ordinal);
            Assert.False(diff.Truncated);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetStagedDiffAsync_ReturnsEmptyForUnstagedFiles()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, "untracked.txt"), "not staged\n");

            GitHelper.StagedDiff diff = await GitHelper.GetStagedDiffAsync(repository, CancellationToken.None);

            Assert.Empty(diff.Text);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetStagedDiffAsync_TruncatesLargeOutputAtConfiguredLimit()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, "large.txt"), new string('x', 120_000));
            RunGit(repository, "add", "--", "large.txt");

            GitHelper.StagedDiff diff = await GitHelper.GetStagedDiffAsync(repository, CancellationToken.None);

            Assert.True(diff.Truncated);
            Assert.Equal(100_000, diff.Text.Length);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetRecentCommitMessagesAsync_ReturnsTheTwoNewestSubjects()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, "first.txt"), "first\n");
            RunGit(repository, "add", "--", "first.txt");
            RunGit(repository, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "First commit");

            await File.WriteAllTextAsync(Path.Combine(repository, "second.txt"), "second\n");
            RunGit(repository, "add", "--", "second.txt");
            RunGit(repository, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "Second commit");

            IReadOnlyList<string> messages = await GitHelper.GetRecentCommitMessagesAsync(repository, CancellationToken.None);

            Assert.Equal(new[] { "Second commit", "First commit" }, messages);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetRecentCommitMessagesAsync_ReturnsEmptyWhenRepositoryHasNoCommits()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");

            IReadOnlyList<string> messages = await GitHelper.GetRecentCommitMessagesAsync(repository, CancellationToken.None);

            Assert.Empty(messages);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    private static void DeleteRepository(string repository)
    {
        foreach (string file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(repository, recursive: true);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git for the test.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
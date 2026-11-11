using System.Diagnostics;
using GitExtensions.CopilotCommitMessage;
using Xunit;

namespace GitExtensions.CopilotCommitMessage.Tests;

public sealed class GitHelperTests
{
    [Fact]
    public async Task GetCommitDiffAsync_UsesStagedChangesWhenPresent()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string file = Path.Combine(repository, "change.txt");
        string untrackedFile = Path.Combine(repository, "new.txt");

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(file, "staged line\n");
            RunGit(repository, "add", "--", "change.txt");
            await File.AppendAllTextAsync(file, "unstaged line\n");
            await File.WriteAllTextAsync(untrackedFile, "untracked addition\n");

            GitHelper.CommitDiff diff = await GitHelper.GetCommitDiffAsync(repository, CancellationToken.None);

            Assert.Contains("+staged line", diff.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("unstaged line", diff.Text, StringComparison.Ordinal);
            Assert.False(diff.Truncated);
            Assert.Equal(GitHelper.CommitDiffSource.Staged, diff.Source);
            Assert.Equal(["new.txt"], diff.UntrackedPaths);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetCommitDiffAsync_IncludesUntrackedFilesWhenFallingBack()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, "untracked.txt"), "not staged\n");

            GitHelper.CommitDiff diff = await GitHelper.GetCommitDiffAsync(repository, CancellationToken.None);
            IReadOnlyList<GitHelper.FileContext> contexts = await GitHelper.GetFileContextsAsync(
                repository,
                diff.Paths,
                diff.UntrackedPaths,
                CancellationToken.None);

            Assert.Empty(diff.Text);
            Assert.Equal(GitHelper.CommitDiffSource.TrackedWorkingTree, diff.Source);
            Assert.Equal(["untracked.txt"], diff.UntrackedPaths);
            Assert.Contains("1: not staged", Assert.Single(contexts).NumberedContent, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetCommitDiffAsync_TruncatesLargeStagedOutputAtConfiguredLimit()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, "large.txt"), new string('x', 120_000));
            RunGit(repository, "add", "--", "large.txt");

            GitHelper.CommitDiff diff = await GitHelper.GetCommitDiffAsync(repository, CancellationToken.None);

            Assert.True(diff.Truncated);
            Assert.Equal(100_000, diff.Text.Length);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetCommitDiffAsync_UsesTrackedWorkingTreeChangesWhenNothingIsStaged()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string trackedFile = Path.Combine(repository, "tracked.txt");
        string untrackedFile = Path.Combine(repository, "untracked.txt");

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(trackedFile, "baseline\n");
            RunGit(repository, "add", "--", "tracked.txt");
            RunGit(repository, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "Initial commit");

            await File.AppendAllTextAsync(trackedFile, "tracked working-tree change\n");
            await File.WriteAllTextAsync(untrackedFile, "untracked secret\n");

            GitHelper.CommitDiff diff = await GitHelper.GetCommitDiffAsync(repository, CancellationToken.None);

            Assert.Contains("tracked working-tree change", diff.Text, StringComparison.Ordinal);
            Assert.Equal(["untracked.txt"], diff.UntrackedPaths);
            Assert.Equal(GitHelper.CommitDiffSource.TrackedWorkingTree, diff.Source);

            IReadOnlyList<GitHelper.FileContext> contexts = await GitHelper.GetFileContextsAsync(
                repository,
                diff.Paths,
                diff.UntrackedPaths,
                CancellationToken.None);
            Assert.Contains("untracked secret", Assert.Single(contexts, context => context.IsUntracked).NumberedContent, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetCommitDiffAsync_ExcludesCopilotIgnoredTrackedFiles()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string includedFile = Path.Combine(repository, "included.txt");
        string excludedFile = Path.Combine(repository, "private.txt");

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(includedFile, "included baseline\n");
            await File.WriteAllTextAsync(excludedFile, "private baseline\n");
            RunGit(repository, "add", "--", "included.txt", "private.txt");
            RunGit(repository, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "Initial commit");

            string excludedUntrackedFile = Path.Combine(repository, "private-untracked.txt");
            await File.WriteAllTextAsync(excludedUntrackedFile, "untracked private change\n");
            await File.WriteAllTextAsync(Path.Combine(repository, ".copilotignore"), "private*.txt\n");
            await File.AppendAllTextAsync(includedFile, "visible change\n");
            await File.AppendAllTextAsync(excludedFile, "private change\n");
            RunGit(repository, "add", "--", "included.txt", "private.txt");

            GitHelper.CommitDiff diff = await GitHelper.GetCommitDiffAsync(repository, CancellationToken.None);

            Assert.Contains("visible change", diff.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("private change", diff.Text, StringComparison.Ordinal);
            Assert.Contains("included.txt", diff.Paths);
            Assert.DoesNotContain("private.txt", diff.Paths);
            Assert.DoesNotContain("private-untracked.txt", diff.UntrackedPaths);
            Assert.Equal(2, diff.ExcludedPathCount);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetFileContextsAsync_ReturnsBoundedNumberedCurrentContents()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string file = Path.Combine(repository, "source.cs");

        try
        {
            RunGit(repository, "init", "--quiet");
            await File.WriteAllTextAsync(file, "first line\nsecond line\n");
            RunGit(repository, "add", "--", "source.cs");
            RunGit(repository, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "Initial commit");

            await File.AppendAllTextAsync(file, "staged change\n");
            RunGit(repository, "add", "--", "source.cs");
            await File.AppendAllTextAsync(file, "unstaged context\n");
            GitHelper.CommitDiff diff = await GitHelper.GetCommitDiffAsync(repository, CancellationToken.None);
            IReadOnlyList<GitHelper.FileContext> contexts = await GitHelper.GetFileContextsAsync(
                repository,
                diff.Paths,
                diff.UntrackedPaths,
                CancellationToken.None);

            GitHelper.FileContext context = Assert.Single(contexts);
            Assert.Equal("source.cs", context.Path);
            Assert.Contains("1: first line", context.NumberedContent, StringComparison.Ordinal);
            Assert.Contains("3: staged change", context.NumberedContent, StringComparison.Ordinal);
            Assert.Contains("4: unstaged context", context.NumberedContent, StringComparison.Ordinal);
            Assert.False(context.Truncated);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetRepositoryContextAsync_ReturnsRepositoryNameAndCurrentBranch()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "-c", "init.defaultBranch=main", "init", "--quiet");
            await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "content\n");
            RunGit(repository, "add", "--", "tracked.txt");
            RunGit(repository, "-c", "user.name=Test User", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "Initial commit");

            GitHelper.RepositoryContext context = await GitHelper.GetRepositoryContextAsync(repository, CancellationToken.None);

            Assert.Equal(Path.GetFileName(repository), context.RepositoryName);
            Assert.Equal("main", context.BranchName);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetCommitHistoryAsync_ReturnsRecentRepositoryAndConfiguredAuthorSubjects()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");
            RunGit(repository, "config", "user.name", "Alice");
            await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "content 0\n");
            for (int commitNumber = 1; commitNumber <= 6; commitNumber++)
            {
                await File.AppendAllTextAsync(Path.Combine(repository, "tracked.txt"), $"content {commitNumber}\n");
                RunGit(repository, "add", "--", "tracked.txt");
                string author = commitNumber % 2 == 1 ? "Alice" : "Bob";
                RunGit(repository, "-c", "user.name=" + author, "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "Commit " + commitNumber);
            }

            GitHelper.CommitHistory history = await GitHelper.GetCommitHistoryAsync(repository, CancellationToken.None);

            Assert.Equal(["Commit 6", "Commit 5", "Commit 4", "Commit 3", "Commit 2"], history.RepositorySubjects);
            Assert.Equal(["Commit 5", "Commit 3", "Commit 1"], history.AuthorSubjects);
            Assert.Equal("Alice", history.ConfiguredAuthorName);
        }
        finally
        {
            DeleteRepository(repository);
        }
    }

    [Fact]
    public async Task GetCommitHistoryAsync_ReturnsEmptyWhenRepositoryHasNoCommits()
    {
        string repository = Path.Combine(Path.GetTempPath(), "CopilotCommitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            RunGit(repository, "init", "--quiet");

            GitHelper.CommitHistory history = await GitHelper.GetCommitHistoryAsync(repository, CancellationToken.None);

            Assert.Empty(history.RepositorySubjects);
            Assert.Empty(history.AuthorSubjects);
            Assert.Null(history.ConfiguredAuthorName);
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
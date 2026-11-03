using GitExtensions.CopilotCommitMessage;
using GitHub.Copilot;
using Xunit;

namespace GitExtensions.CopilotCommitMessage.Tests;

public sealed class CopilotRuntimeIsolationTests
{
    [Fact]
    public async Task BundledRuntime_StartsWithoutAmbientAuthAndCreatesIsolatedSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "CopilotRuntimeIsolationTests", Guid.NewGuid().ToString("N"));
        string copilotHome = Path.Combine(root, "home");
        string configDirectory = Path.Combine(root, "config");
        string workingDirectory = Path.Combine(root, "work");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(workingDirectory);

        string pluginDirectory = Environment.GetEnvironmentVariable("COPILOT_TEST_PLUGIN_DIRECTORY") ?? AppContext.BaseDirectory;
        string runtimePath = CopilotIsolation.GetRuntimePath(pluginDirectory);
        CopilotClient client = new(CopilotIsolation.CreateClientOptions(copilotHome, workingDirectory, runtimePath));
        try
        {
            await client.StartAsync();
            GetAuthStatusResponse auth = await client.GetAuthStatusAsync();
            Assert.False(auth.IsAuthenticated);

            SessionConfig config = CopilotIsolation.CreateSessionConfig("runtime-isolation-smoke-test", workingDirectory);
            config.ConfigDirectory = configDirectory;
            CopilotSession session = await client.CreateSessionAsync(config);
            try
            {
                Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            await client.DisposeAsync();
            DeleteDirectory(root);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
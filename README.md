# Git Extensions Copilot Commit Message

A Git Extensions 7.x commit-dialog plugin that generates a commit message from staged changes with the official `GitHub.Copilot.SDK` .NET client. It targets .NET 10 Windows Desktop and uses the user's default Copilot home (`%USERPROFILE%\.copilot`, or `COPILOT_HOME` when set) by default. An option enables a separate plugin-specific Copilot home.

## Security Model

After the user clicks **Copilot message**, the plugin reads the selected diff, current contents of changed files, repository identity, branch, and up to five recent repository and configured-author commit subjects. File snapshots are the current working-tree versions and may therefore contain unstaged text even when the patch is staged. Non-ignored untracked files are included as proposed new-file context. Review all supplied data for secrets before using the feature; history is used only as a style reference.

If the index contains changes, its diff is used; otherwise the plugin falls back to the tracked working-tree diff, matching the tracked-file scope of `git commit -a`. Non-ignored untracked files are also supplied as proposed additions. Git ignore rules and a repository-root `.copilotignore` exclude matching paths from both the patch and file context. `.copilotignore` uses Git ignore syntax. Binary files, symlinks, and missing working-tree files do not receive source snapshots. Snapshots are line-numbered and capped at 12,000 characters per file and 48,000 characters total; the patch is capped at 100,000 characters.

Generated messages use a concise subject and separate bullets for materially distinct changes, including notable user-visible behavior and meaningful test coverage. Every output line is wrapped to a maximum of 50 characters, including the subject.

The client uses the SDK's signed-in-user authentication with the selected Copilot home and a separate empty working directory outside the repository. Although the client uses Copilot CLI mode to access the user's Windows Credential Manager sign-in, each session explicitly supplies an empty tool allowlist, empty tool/MCP/plugin/instruction/agent/hook collections, disables config discovery, custom instructions, Git operations, skills, hooks, session storage, and telemetry, and sends the patch, snapshots, and metadata as JSON-encoded text without file attachments. No repository path is passed to the runtime. The default model is `auto`; an explicitly configured model ID is checked against the account's available, enabled models before use.

This limits the runtime's application-level context and capabilities; it is not an operating-system sandbox. The runtime can make network requests to GitHub Copilot as required to generate a response. The separate-home option separates Copilot CLI configuration and state, but authentication still uses the Windows Credential Manager sign-in for the logged-in user.

## Sign In

By default, the plugin uses the Copilot home already used by Copilot CLI: `%USERPROFILE%\.copilot`, unless `COPILOT_HOME` is set. Sign in with `copilot login`; the SDK uses that signed-in user's Windows Credential Manager credentials. If `COPILOT_HOME` is set in your environment, leave it set while signing in; otherwise the CLI and plugin use `%USERPROFILE%\.copilot`. The plugin does not fall back to another profile if authentication is unavailable.

To use a separate Copilot configuration directory instead, enable **Use isolated Copilot home** under **Settings > Plugins > Copilot Commit Message**. Set `COPILOT_HOME` to the plugin's home and run `copilot login` so that home records the selected GitHub account:

```powershell
$env:COPILOT_HOME = Join-Path $env:LOCALAPPDATA 'GitExtensions\CopilotCommitMessage\CopilotHome'
copilot login
```

The plugin does not read, copy, display, or persist access tokens in Git Extensions settings. The isolated home separates CLI configuration and state; the Windows Credential Manager remains shared with the logged-in user.

## Build

Install Git Extensions 7.2.x, the .NET 10 SDK, and the official Copilot CLI once for the isolated sign-in step. Then build on Windows:

```powershell
dotnet build src\GitExtensions.CopilotCommitMessage.csproj -c Release
```

To create a NuGet package for Git Extensions Plugin Manager:

```powershell
dotnet pack src\GitExtensions.CopilotCommitMessage.csproj -c Release
```

The package is written to `src\bin\Release` and includes the plugin, its runtime dependencies, bundled Copilot runtime files, and third-party notices. It declares the `GitExtensions.Extensibility` dependency required for Plugin Manager discovery. Copilot runtime assets are architecture-specific; the package built on x64 includes `win-x64` assets only.

If Git Extensions is installed elsewhere, pass its directory:

```powershell
dotnet build src\GitExtensions.CopilotCommitMessage.csproj -c Release -p:GitExtensionsPath='D:\Apps\GitExtensions'
```

If Group Policy blocks `tar.exe` when MSBuild launches it, the SDK archive can be extracted from PowerShell and supplied through the SDK's `CopilotCliBinaryPath` override. Run a normal build once to download the archive; it will stop at the blocked extraction. Then, from the repository root, run:

```powershell
[xml]$pluginProject = Get-Content .\src\GitExtensions.CopilotCommitMessage.csproj
$sdkVersion = ($pluginProject.Project.ItemGroup.PackageReference | Where-Object Include -eq 'GitHub.Copilot.SDK').Version
$sdkPropsPath = Join-Path $env:USERPROFILE ".nuget\packages\github.copilot.sdk\$sdkVersion\build\GitHub.Copilot.SDK.props"
[xml]$sdkProps = Get-Content $sdkPropsPath
$cliVersion = $sdkProps.Project.PropertyGroup.CopilotCliVersion
$cache = Join-Path $PWD "src\obj\Release\net10.0-windows\copilot-cli\$cliVersion\win32-x64"
tar.exe -xzf "$cache\copilot.tgz" --strip-components=1 -C $cache
if ($LASTEXITCODE -ne 0) { throw "Copilot runtime extraction failed: $LASTEXITCODE" }

$stage = Join-Path $PWD 'src\obj\plugin-staging'
dotnet build .\GitExtensions.CopilotCommitMessage.sln -c Release `
	"-p:CopilotCliBinaryPath=$cache\prebuilds\win32-x64\copilot-runtime.exe" `
	"-p:GitExtensionsPluginsPath=$stage"
```

##  Install

Copy the contents of `src\bin\Release\net10.0-windows` into a new folder under Git Extensions' user plugins directory (`%LOCALAPPDATA%\GitExtensions\UserPlugins`). Include the `runtimes` directory and all SDK dependency DLLs. Do not copy the host's Git Extensions assemblies from the build environment. Restart Git Extensions and open the Commit dialog; the **Copilot message** button appears on its toolbar. The settings are under **Settings > Plugins > Copilot Commit Message**. **Use isolated Copilot home** is off by default; enable it to use the plugin-specific sign-in described above. The model dropdown defaults to `auto`; open it to load enabled models available to the selected Copilot home. **Commit message instructions** is optional commit-specific guidance; it can be edited or cleared. Repository-local `.copilotignore` patterns can exclude sensitive tracked and untracked files from the context.

## Tests

```powershell
dotnet test tests\GitExtensions.CopilotCommitMessage.Tests.csproj -c Release
```

Tests cover staged-first and tracked-file fallback diff retrieval, untracked additions, ignore filtering, bounded source context, repository and author history, 72-character output wrapping, Copilot-home selection, explicit SDK isolation settings, and model selection. A live Copilot sign-in and Git Extensions UI smoke test remain manual.

## License

This project is licensed under the MIT License; see [LICENSE.txt](LICENSE.txt). Third-party components have their own notices in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Third-Party Notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for notices and license terms for
GitExtensions.AICommitMessage, the Git Extensions Plugin Template, and the GitHub Copilot SDK.
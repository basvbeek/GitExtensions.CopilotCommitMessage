# Git Extensions Copilot Commit Message

A Git Extensions 7.x commit-dialog plugin that generates a commit message from staged changes with the official `GitHub.Copilot.SDK` .NET client. It targets .NET 10 Windows Desktop and requires a GitHub Copilot subscription signed into the plugin's isolated Copilot home.

## Security Model

The staged diff and the subjects of the two most recent commits are read only after the user clicks **Copilot message**. The plugin does not read unstaged or untracked file contents. Both the diff and commit subjects are sent to GitHub Copilot; review them for secrets before using the feature. Recent commit subjects are included only as style references and are not intended to be copied.

The client uses SDK `CopilotClientMode.Empty`, its own `COPILOT_HOME`, a separate empty working directory outside the repository, a cleared child-process environment, and an isolated configuration directory. The session explicitly supplies an empty tool allowlist, empty tool/MCP/plugin/instruction/agent/hook collections, disables config discovery, custom instructions, Git operations, skills, hooks, session storage, and telemetry, and sends the diff as JSON-encoded text without file attachments. No repository path is passed to the runtime. The default model is `auto`; an explicitly configured model ID is checked against the account's available, enabled models before use.

This limits the runtime's application-level context and capabilities; it is not an operating-system sandbox. The runtime can make network requests to GitHub Copilot as required to generate a response.

## Sign In

The plugin deliberately uses a dedicated Copilot home rather than GitHub CLI credentials or the user's default Copilot profile. Sign in once using the official Copilot CLI with the environment set to the same folder the plugin uses:

```powershell
$env:COPILOT_HOME = Join-Path $env:LOCALAPPDATA 'GitExtensions\CopilotCommitMessage\CopilotHome'
$env:COPILOT_DISABLE_KEYTAR = '1'
copilot login
```

The SDK uses file-based credentials under that home when keytar is disabled. The plugin does not read, copy, display, or persist access tokens in Git Extensions settings. If authentication is unavailable, the plugin shows the expected home path and does not fall back to another profile.

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

Copy the contents of `src\bin\Release\net10.0-windows` into a new folder under Git Extensions' user plugins directory (`%LOCALAPPDATA%\GitExtensions\UserPlugins`). Include the `runtimes` directory and all SDK dependency DLLs. Do not copy the host's Git Extensions assemblies from the build environment. Restart Git Extensions and open the Commit dialog; the **Copilot message** button appears on its toolbar. The settings are under **Settings > Plugins > Copilot Commit Message**. The model dropdown defaults to `auto`; open it to load the enabled models available to the account signed into the plugin's isolated Copilot home. **Commit message instructions** defaults to concise, purpose-focused guidance that follows recent repository style without copying commit-specific details; it can be edited or cleared. The fixed diff-safety and raw-output requirements remain in effect regardless of this setting.

## Tests

```powershell
dotnet test tests\GitExtensions.CopilotCommitMessage.Tests.csproj -c Release
```

Tests cover staged-only diff retrieval, explicit SDK isolation settings, and default/explicit model selection. A live Copilot sign-in and Git Extensions UI smoke test remain manual.

## License

This project is licensed under the MIT License; see [LICENSE.txt](LICENSE.txt). Third-party components have their own notices in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Third-Party Notices

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for notices and license terms for
GitExtensions.AICommitMessage and the GitHub Copilot SDK.
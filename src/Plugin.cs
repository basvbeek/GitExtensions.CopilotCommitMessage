using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;
using GitUIPluginInterfaces;
using GitHub.Copilot;

namespace GitExtensions.CopilotCommitMessage;

[Export(typeof(IGitPlugin))]
[Export(typeof(IGitPluginForRepository))]
[Export(typeof(IGitPluginForCommit))]
public sealed class Plugin : GitPluginBase, IGitPluginForRepository, IGitPluginForCommit
{
    private const string ButtonName = "copilotCommitMessageButton";
    private const string ButtonLabel = "Copilot message";
    private const int MaxMessageCharacters = 8_000;

    private ChoiceSetting _model = CreateModelSetting();
    private BoolSetting _useIsolatedCopilotHome = CreateUseIsolatedCopilotHomeSetting();
    private StringSetting _commitMessageInstructions = CreateCommitMessageInstructionsSetting();
    private IGitModule? _module;
    private bool _idleHooked;

    public Plugin()
        : base(hasSettings: true)
    {
        Name = "Copilot Commit Message";
        Description = "Generate a commit message from pending changes using GitHub Copilot.";
        Icon = PluginIcon.Create();
    }

    public override IEnumerable<ISetting> GetSettings()
    {
        ChoiceSetting modelSetting = CreateModelSetting();
        ModelComboBox modelControl = new();
        modelControl.Items.Add(CopilotModelSelection.AutoModelId);
        modelSetting.CustomControl = modelControl;
        ModelOptionState modelOptionState = new();
        modelControl.DropDown += async (_, _) => await LoadModelOptionsAsync(modelControl, modelSetting, modelOptionState);
        _model = modelSetting;

        _useIsolatedCopilotHome = CreateUseIsolatedCopilotHomeSetting();

        StringSetting instructionsSetting = CreateCommitMessageInstructionsSetting();
        instructionsSetting.CustomControl = new TextBox
        {
            AcceptsReturn = true,
            Height = 100,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
        };
        _commitMessageInstructions = instructionsSetting;

        yield return modelSetting;
        yield return _useIsolatedCopilotHome;
        yield return instructionsSetting;
    }

    public override void Register(IGitUICommands gitUiCommands)
    {
        base.Register(gitUiCommands);
        _module = gitUiCommands.Module;
        gitUiCommands.PreCommit += OnPreCommit;
        gitUiCommands.PostCommit += OnPostCommit;
    }

    public override void Unregister(IGitUICommands gitUiCommands)
    {
        gitUiCommands.PreCommit -= OnPreCommit;
        gitUiCommands.PostCommit -= OnPostCommit;
        UnhookIdle();
        base.Unregister(gitUiCommands);
    }

    public override bool Execute(GitUIEventArgs args)
    {
        MessageBox.Show("Open the Commit dialog to generate a message from pending changes.", Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void OnPreCommit(object? sender, GitUIEventArgs e) => HookIdle();

    private void OnPostCommit(object? sender, GitUIPostActionEventArgs e) => UnhookIdle();

    private void HookIdle()
    {
        if (_idleHooked)
        {
            return;
        }

        _idleHooked = true;
        Application.Idle += OnApplicationIdle;
    }

    private void UnhookIdle()
    {
        if (!_idleHooked)
        {
            return;
        }

        _idleHooked = false;
        Application.Idle -= OnApplicationIdle;
    }

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        Form? form = Application.OpenForms.Cast<Form>()
            .FirstOrDefault(candidate => candidate.GetType().Name == "FormCommit" && candidate.Visible && !candidate.IsDisposed);
        if (form is null)
        {
            return;
        }

        UnhookIdle();
        try
        {
            AddButton(form);
        }
        catch
        {
            // Skip injection when the host form layout changes.
        }
    }

    private void AddButton(Form form)
    {
        ToolStripItem? templatesItem = GetMember(form, "commitTemplatesToolStripMenuItem") as ToolStripItem;
        ToolStrip? toolbar = GetMember(form, "toolbarCommit") as ToolStrip;
        int insertIndex = -1;

        if (toolbar is not null && templatesItem is not null)
        {
            int index = toolbar.Items.IndexOf(templatesItem);
            if (index >= 0)
            {
                insertIndex = index + 1;
            }
        }

        toolbar ??= AllControls(form).OfType<ToolStrip>()
            .FirstOrDefault(candidate => templatesItem is not null && candidate.Items.Contains(templatesItem))
            ?? AllControls(form).OfType<ToolStrip>().FirstOrDefault();

        if (toolbar is null || toolbar.Items.Cast<ToolStripItem>().Any(item => item.Name == ButtonName))
        {
            return;
        }

        ToolStripButton button = new()
        {
            Name = ButtonName,
            Text = ButtonLabel,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Generate a message from staged or working-tree changes. The selected change context is sent only after you click.",
            Tag = new ButtonState(),
        };
        button.Click += async (_, _) => await OnGenerateClickedAsync(form, button);
        form.FormClosed += (_, _) => ((ButtonState)button.Tag!).Cancellation?.Cancel();

        if (insertIndex >= 0 && insertIndex <= toolbar.Items.Count)
        {
            toolbar.Items.Insert(insertIndex, button);
        }
        else
        {
            toolbar.Items.Add(button);
        }
    }

    private async Task OnGenerateClickedAsync(Form form, ToolStripButton button)
    {
        ButtonState state = (ButtonState)button.Tag!;
        if (state.Cancellation is not null)
        {
            button.Text = "Cancelling...";
            button.Enabled = false;
            state.Cancellation.Cancel();
            return;
        }

        string? workingDirectory = _module?.WorkingDir;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            MessageBox.Show(form, "The repository working directory is unavailable.", Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Control? messageControl = GetMember(form, "Message") as Control;
        string originalMessage = messageControl?.Text ?? string.Empty;
        using CancellationTokenSource cancellation = new();
        state.Cancellation = cancellation;
        button.Text = "Cancel generation";

        try
        {
            string message = await GenerateMessageAsync(
                workingDirectory,
                _model.ValueOrDefault(Settings),
                _commitMessageInstructions.ValueOrDefault(Settings),
                _useIsolatedCopilotHome.ValueOrDefault(Settings),
                cancellation.Token);
            if (!form.IsDisposed && !form.Disposing && !cancellation.IsCancellationRequested &&
                string.Equals(ReadCommitMessage(form), originalMessage, StringComparison.Ordinal))
            {
                SetCommitMessage(form, message);
            }
            else if (!cancellation.IsCancellationRequested &&
                !string.Equals(ReadCommitMessage(form), originalMessage, StringComparison.Ordinal))
            {
                MessageBox.Show(form, "The commit message changed while Copilot was working. Your edits were kept.", Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!form.IsDisposed && !form.Disposing)
            {
                MessageBox.Show(form, "Could not generate a commit message:\n\n" + ex.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            state.Cancellation = null;
            if (!form.IsDisposed && !button.IsDisposed)
            {
                button.Text = ButtonLabel;
                button.Enabled = true;
            }
        }
    }

    private static ChoiceSetting CreateModelSetting()
    {
        return new ChoiceSetting(
            "Model",
            new List<string> { CopilotModelSelection.AutoModelId },
            CopilotModelSelection.AutoModelId);
    }

    private static StringSetting CreateCommitMessageInstructionsSetting()
    {
        return new StringSetting("Commit message instructions", CopilotIsolation.DefaultCommitMessageInstructions);
    }

    private static BoolSetting CreateUseIsolatedCopilotHomeSetting()
    {
        return new BoolSetting("Use isolated Copilot home", false);
    }

    private static string ResolveCopilotHome(bool useIsolatedCopilotHome)
    {
        return CopilotIsolation.ResolveCopilotHome(
            useIsolatedCopilotHome,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("COPILOT_HOME"));
    }

    private async Task LoadModelOptionsAsync(ModelComboBox comboBox, ChoiceSetting modelSetting, ModelOptionState state)
    {
        if (state.Loaded || state.Loading)
        {
            return;
        }

        state.Loading = true;
        comboBox.IsLoading = true;
        try
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
            IReadOnlyList<string> modelChoices = await ListModelChoicesAsync(
                _useIsolatedCopilotHome.ValueOrDefault(Settings), cancellation.Token);
            if (comboBox.IsDisposed)
            {
                return;
            }

            string selectedModel = comboBox.SelectedItem?.ToString() ?? comboBox.Text;
            modelSetting.Values.Clear();
            foreach (string modelChoice in modelChoices)
            {
                modelSetting.Values.Add(modelChoice);
            }

            comboBox.BeginUpdate();
            try
            {
                comboBox.Items.Clear();
                comboBox.Items.AddRange(modelSetting.Values.Cast<object>().ToArray());
                comboBox.SelectedItem = modelSetting.Values.Contains(selectedModel)
                    ? selectedModel
                    : CopilotModelSelection.AutoModelId;
            }
            finally
            {
                comboBox.EndUpdate();
            }

            state.Loaded = true;
        }
        catch (Exception ex)
        {
            if (!comboBox.IsDisposed)
            {
                MessageBox.Show(comboBox, "Could not load available Copilot models:\n\n" + ex.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            state.Loading = false;
            if (!comboBox.IsDisposed)
            {
                comboBox.IsLoading = false;
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ListModelChoicesAsync(
        bool useIsolatedCopilotHome,
        CancellationToken cancellationToken)
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDirectory = Path.Combine(localData, "GitExtensions", "CopilotCommitMessage");
        string copilotHome = ResolveCopilotHome(useIsolatedCopilotHome);
        string workingDirectory = Path.Combine(appDirectory, "EmptyWorkspace");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(workingDirectory);

        string pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;
        string runtimePath = CopilotIsolation.GetRuntimePath(pluginDirectory);
        await using CopilotClient client = new(CopilotIsolation.CreateClientOptions(copilotHome, workingDirectory, runtimePath));
        await client.StartAsync(cancellationToken).ConfigureAwait(false);

        GetAuthStatusResponse auth = await client.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            throw new InvalidOperationException(
                "Copilot is not signed in for the selected home. Run `copilot login` with COPILOT_HOME set to this path:\n" + copilotHome);
        }

        IList<ModelInfo> models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return CopilotModelSelection.BuildModelChoices(models.Select(candidate => ((string?)candidate.Id, candidate.Policy?.State)));
    }

    private static async Task<string> GenerateMessageAsync(
        string repositoryDirectory,
        string? configuredModel,
        string? configuredInstructions,
        bool useIsolatedCopilotHome,
        CancellationToken cancellationToken)
    {
        GitHelper.CommitDiff commitDiff = await GitHelper.GetCommitDiffAsync(repositoryDirectory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(commitDiff.Text) && commitDiff.UntrackedPaths.Count == 0)
        {
            throw new InvalidOperationException("No eligible changes were found. Check the repository's .copilotignore and Git ignore rules.");
        }

        GitHelper.CommitHistory commitHistory = await GitHelper.GetCommitHistoryAsync(repositoryDirectory, cancellationToken).ConfigureAwait(false);
        GitHelper.RepositoryContext repositoryContext = await GitHelper.GetRepositoryContextAsync(repositoryDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GitHelper.FileContext> fileContexts = await GitHelper.GetFileContextsAsync(
            repositoryDirectory,
            commitDiff.Paths,
            commitDiff.UntrackedPaths,
            cancellationToken).ConfigureAwait(false);

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDirectory = Path.Combine(localData, "GitExtensions", "CopilotCommitMessage");
        string copilotHome = ResolveCopilotHome(useIsolatedCopilotHome);
        string configDirectory = Path.Combine(appDirectory, "Config");
        string workingDirectory = Path.Combine(appDirectory, "EmptyWorkspace");
        Directory.CreateDirectory(copilotHome);
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(workingDirectory);

        if (PathsOverlap(repositoryDirectory, copilotHome) ||
            PathsOverlap(repositoryDirectory, configDirectory) ||
            PathsOverlap(repositoryDirectory, workingDirectory))
        {
            throw new InvalidOperationException("The isolated Copilot runtime directories overlap the repository and cannot be used.");
        }

        string pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;
        string runtimePath = CopilotIsolation.GetRuntimePath(pluginDirectory);
        await using CopilotClient client = new(CopilotIsolation.CreateClientOptions(copilotHome, workingDirectory, runtimePath));
        await client.StartAsync(cancellationToken).ConfigureAwait(false);

        GetAuthStatusResponse auth = await client.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            throw new InvalidOperationException(
                "Copilot is not signed in for the selected home. Run `copilot login` with COPILOT_HOME set to this path:\n" + copilotHome);
        }

        string modelId;
        if (CopilotModelSelection.IsAutoModel(configuredModel))
        {
            modelId = CopilotModelSelection.AutoModelId;
        }
        else
        {
            IList<ModelInfo> models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            modelId = CopilotModelSelection.ResolveModelId(configuredModel, models.Select(candidate => ((string?)candidate.Id, candidate.Policy?.State)));
        }

        CopilotSession? session = null;
        try
        {
            SessionConfig config = CopilotIsolation.CreateSessionConfig(modelId, workingDirectory, configuredInstructions);
            config.ConfigDirectory = configDirectory;
            session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);

            string prompt = CopilotIsolation.CreateUserPrompt(
                commitDiff,
                commitHistory,
                repositoryContext,
                fileContexts);

            SessionEvent? response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            string? message = (response as AssistantMessageEvent)?.Data?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new InvalidOperationException("Copilot returned an empty commit message.");
            }

            if (message.Length > MaxMessageCharacters)
            {
                throw new InvalidOperationException("Copilot's response exceeded the 8,000-character commit message limit.");
            }

            return CommitMessageFormatter.WrapLines(message);
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
            {
                await session.AbortAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (TimeoutException)
        {
            if (session is not null)
            {
                await session.AbortAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await client.DeleteSessionAsync(session.SessionId).ConfigureAwait(false);
                }
                catch
                {
                }

                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        string fullFirst = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullSecond = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullFirst.StartsWith(fullSecond, StringComparison.OrdinalIgnoreCase) ||
            fullSecond.StartsWith(fullFirst, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadCommitMessage(Form form) => (GetMember(form, "Message") as Control)?.Text ?? string.Empty;

    private static void SetCommitMessage(Form form, string message)
    {
        MethodInfo? replace = form.GetType().GetMethod(
            "ReplaceMessage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: [typeof(string)],
            modifiers: null);
        if (replace is not null)
        {
            replace.Invoke(form, [message]);
            return;
        }

        if (GetMember(form, "Message") is Control messageControl)
        {
            messageControl.Text = message;
            messageControl.Focus();
        }
    }

    private static object? GetMember(Form form, string name)
    {
        FieldInfo? field = form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field?.GetValue(form);
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in AllControls(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class ButtonState
    {
        internal CancellationTokenSource? Cancellation { get; set; }
    }

    private sealed class ModelOptionState
    {
        internal bool Loaded { get; set; }
        internal bool Loading { get; set; }
    }
}
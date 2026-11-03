using System;
using System.Collections.Generic;
using System.Linq;

namespace GitExtensions.CopilotCommitMessage;

internal static class CopilotModelSelection
{
    internal const string AutoModelId = "auto";

    internal static bool IsAutoModel(string? configuredModel)
    {
        return string.IsNullOrWhiteSpace(configuredModel) ||
            string.Equals(configuredModel.Trim(), AutoModelId, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> BuildModelChoices(IEnumerable<(string? Id, string? PolicyState)> availableModels)
    {
        List<string> choices = [AutoModelId];
        choices.AddRange(availableModels
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) &&
                !string.Equals(candidate.PolicyState, "disabled", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.Id, AutoModelId, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(modelId => modelId, StringComparer.Ordinal));
        return choices;
    }

    internal static string ResolveModelId(string? configuredModel, IEnumerable<(string? Id, string? PolicyState)> availableModels)
    {
        string requestedModel = configuredModel?.Trim() ?? string.Empty;
        if (IsAutoModel(requestedModel))
        {
            return AutoModelId;
        }

        (string? Id, string? PolicyState) selectedModel = availableModels.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requestedModel, StringComparison.Ordinal));
        if (selectedModel.Id is null)
        {
            throw new InvalidOperationException($"The configured Copilot model '{requestedModel}' is not available for this account.");
        }

        if (string.Equals(selectedModel.PolicyState, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The configured Copilot model '{requestedModel}' is disabled for this account.");
        }

        return selectedModel.Id;
    }
}
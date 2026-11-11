using System;
using System.Collections.Generic;
using System.Text;

namespace GitExtensions.CopilotCommitMessage;

internal static class CommitMessageFormatter
{
    internal const int MaxLineLength = 50;

    internal static string WrapLines(string message)
    {
        string normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        List<string> lines = new();
        foreach (string line in normalized.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.Add(string.Empty);
                continue;
            }

            lines.AddRange(WrapLine(line));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> WrapLine(string line)
    {
        string trimmedLine = line.TrimEnd();
        if (trimmedLine.Length <= MaxLineLength)
        {
            return [trimmedLine];
        }

        int indentationLength = trimmedLine.Length - trimmedLine.TrimStart().Length;
        string indentation = trimmedLine[..indentationLength];
        string content = trimmedLine[indentationLength..];
        int bulletLength = GetBulletPrefixLength(content);
        string firstPrefix = indentation + content[..bulletLength];
        string continuationPrefix = bulletLength > 0
            ? new string(' ', firstPrefix.Length)
            : indentation;
        string remainingContent = content[bulletLength..];

        List<string> wrappedLines = new();
        string currentPrefix = firstPrefix;
        StringBuilder currentLine = new(currentPrefix);
        foreach (string initialWord in remainingContent.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string word = initialWord;
            int separatorLength = currentLine.Length > currentPrefix.Length ? 1 : 0;
            if (currentLine.Length + separatorLength + word.Length <= MaxLineLength)
            {
                if (separatorLength > 0)
                {
                    currentLine.Append(' ');
                }

                currentLine.Append(word);
                continue;
            }

            if (currentLine.Length > currentPrefix.Length)
            {
                wrappedLines.Add(currentLine.ToString());
            }

            currentPrefix = continuationPrefix;
            currentLine = new StringBuilder(currentPrefix);
            while (word.Length > MaxLineLength - currentLine.Length)
            {
                int available = MaxLineLength - currentLine.Length;
                if (available == 0)
                {
                    wrappedLines.Add(currentLine.ToString());
                    currentLine = new StringBuilder(continuationPrefix);
                    continue;
                }

                wrappedLines.Add(currentLine + word[..available]);
                word = word[available..];
                currentLine = new StringBuilder(continuationPrefix);
            }

            currentLine.Append(word);
        }

        if (currentLine.Length > currentPrefix.Length)
        {
            wrappedLines.Add(currentLine.ToString());
        }

        return wrappedLines;
    }

    private static int GetBulletPrefixLength(string line)
    {
        if (line.Length >= 2 && line[1] == ' ' && line[0] is '-' or '*' or '+')
        {
            return 2;
        }

        int digitCount = 0;
        while (digitCount < line.Length && char.IsDigit(line[digitCount]))
        {
            digitCount++;
        }

        if (digitCount > 0 && digitCount + 1 < line.Length && line[digitCount + 1] == ' ' && line[digitCount] is '.' or ')')
        {
            return digitCount + 2;
        }

        return 0;
    }
}
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ObsidianX.Client.Services;

/// <summary>
/// On first launch (or when the bundled rule version is newer than what's
/// already on disk), seed the user's Claude Code per-project memory dir
/// with ObsidianX's brain-save policy. This is what makes "ทำไมสมองไม่
/// บันทึกเอง" not a problem for fresh installs — the rule arrives with
/// the binary, no manual feedback round needed.
///
/// Target path: %USERPROFILE%/.claude/projects/&lt;vault-slug&gt;/memory/
///   feedback_brain_proactive_save.md   ← the rule itself
///   MEMORY.md                          ← index pointing at it
///
/// Slug rule (matches Claude Code's own scheme):
///   "G:\Obsidian"          → "G--Obsidian"
///   "C:\Users\xman\iot"    → "C--Users-xman-iot"
///   i.e. ':' and '\' both become '-'.
/// </summary>
internal static class ClaudeBrainRulesInstaller
{
    // Bump this when the rule body below changes — installer overwrites
    // older versions automatically while leaving newer/equal ones alone.
    private const string RuleVersion = "1.0";

    private const string FeedbackFileName = "feedback_brain_proactive_save.md";
    private const string IndexFileName = "MEMORY.md";

    private static readonly Regex VersionLineRx = new(
        @"^version:\s*(?<v>[\d.]+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static InstallResult EnsureInstalled(string vaultPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(vaultPath) || !Directory.Exists(vaultPath))
                return InstallResult.SkippedNoVault;

            var memoryDir = ComputeClaudeCodeMemoryDir(vaultPath);
            if (memoryDir == null) return InstallResult.SkippedNoUserProfile;

            var feedbackPath = Path.Combine(memoryDir, FeedbackFileName);
            var indexPath = Path.Combine(memoryDir, IndexFileName);

            // Decide: install fresh, upgrade, or skip?
            var action = DecideAction(feedbackPath);
            if (action == InstallAction.Skip) return InstallResult.AlreadyCurrent;

            Directory.CreateDirectory(memoryDir);
            File.WriteAllText(feedbackPath, BuildFeedbackContent(), Utf8NoBom);
            EnsureIndexEntry(indexPath);

            return action == InstallAction.Fresh
                ? InstallResult.InstalledFresh
                : InstallResult.Upgraded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Brain rules install failed: {ex.Message}");
            return InstallResult.Failed;
        }
    }

    private static InstallAction DecideAction(string feedbackPath)
    {
        if (!File.Exists(feedbackPath)) return InstallAction.Fresh;

        try
        {
            var existing = File.ReadAllText(feedbackPath);
            var m = VersionLineRx.Match(existing);

            // No version field at all → treat as user-customized (this is
            // also how pre-1.0 manual files look — e.g. xman's original
            // feedback file). Never overwrite without a version signal.
            if (!m.Success) return InstallAction.Skip;

            // Compare as Version — handles "1.0" vs "1.10" vs "2.0".
            if (!Version.TryParse(m.Groups["v"].Value, out var existingVer)) return InstallAction.Skip;
            if (!Version.TryParse(RuleVersion, out var bundledVer)) return InstallAction.Skip;

            return existingVer < bundledVer ? InstallAction.Upgrade : InstallAction.Skip;
        }
        catch
        {
            // Any read failure → safer to leave the file alone than corrupt it.
            return InstallAction.Skip;
        }
    }

    private static string? ComputeClaudeCodeMemoryDir(string vaultPath)
    {
        // Trim trailing separators so "G:\Obsidian\" doesn't become "G--Obsidian-".
        var trimmed = vaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var slug = trimmed
            .Replace(":", "-")
            .Replace("\\", "-")
            .Replace("/", "-");
        if (string.IsNullOrEmpty(slug)) return null;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return null;

        return Path.Combine(userProfile, ".claude", "projects", slug, "memory");
    }

    private static string BuildFeedbackContent() => $"""
---
name: Save to ObsidianX brain proactively, don't wait to be told
description: ObsidianX vault expects auto-save of non-trivial insights to the brain during work, not a final "should I save this?" prompt
type: project-default
installedBy: ObsidianX.Client first-run
version: {RuleVersion}
---
When working on this ObsidianX vault, save substantive findings to the brain *as they happen*, without asking — the brain is a living knowledge graph and every non-trivial answer should leave a trace.

**Rule of thumb (also stated in vault CLAUDE.md):** if you just spent more than 2 tool calls figuring something out and the answer is non-trivial, SAVE IT. Every good answer should leave a trace in the vault.

**How to apply:**
- Any debugging session that took more than 2 tool calls and produced a generalizable insight → call `brain_create_note` with a proper folder (e.g. `Programming/WPF`, `Debugging`, `AI`) and tags, **during** the session, not after being prompted.
- Small observations / one-liners → `brain_remember` to today's session journal.
- Search first with `brain_search` to avoid duplicating an existing note; if one exists, `brain_append_note` instead.
- Never announce "I searched for X" — the auto-journal already logs every MCP tool call.
- Pattern examples that warrant saving: WPF resource-dictionary gotchas, theme-propagation pitfalls, Windows shell COM patterns, multi-ICO generation, GPU-performance wins, mesh-rendering tricks, MCP launch logic.
- Patterns that do NOT warrant saving: boilerplate lookups, trivial one-file edits, generic "I added a button" changes.

Folder conventions: `Programming/<Tech>`, `Notes/Claude-Sessions`, `Debugging`, `AI`, `Blockchain_Web3`. Always include tags in frontmatter.
""";

    private static void EnsureIndexEntry(string indexPath)
    {
        const string entry =
            "- [Brain proactive-save expectation](feedback_brain_proactive_save.md) — save non-trivial ObsidianX insights to the brain during work, don't wait to be asked";

        if (File.Exists(indexPath))
        {
            var content = File.ReadAllText(indexPath);
            if (content.Contains(FeedbackFileName, StringComparison.Ordinal)) return;
            File.AppendAllText(indexPath, Environment.NewLine + entry + Environment.NewLine, Utf8NoBom);
        }
        else
        {
            File.WriteAllText(indexPath, $"# Memory Index{Environment.NewLine}{Environment.NewLine}{entry}{Environment.NewLine}", Utf8NoBom);
        }
    }

    private static UTF8Encoding Utf8NoBom { get; } = new(false);

    private enum InstallAction { Fresh, Upgrade, Skip }

    public enum InstallResult
    {
        AlreadyCurrent,
        InstalledFresh,
        Upgraded,
        SkippedNoVault,
        SkippedNoUserProfile,
        Failed
    }
}

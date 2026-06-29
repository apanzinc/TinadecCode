using System.Text.Json;
using System.Text.Json.Nodes;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

/// <summary>
/// 提示词片段版本化与效果追踪服务。
/// 每次修改生成版本快照，支持回滚；追踪正负信号计算效果分数；
/// 让提示词工程可被量化、可被 A/B 测试、可被 AI 动态优化。
/// </summary>
public sealed class PromptFragmentVersioningService(
    CoreStore store,
    EventHub events)
{
    /// <summary>
    /// 获取指定片段的所有版本历史。
    /// </summary>
    public IReadOnlyList<PromptFragmentVersionDto> ListVersions(string fragmentId)
    {
        return store.ListPromptFragmentVersions(fragmentId);
    }

    /// <summary>
    /// 创建片段的新版本。在更新内容时自动调用。
    /// </summary>
    public PromptFragmentVersionDto CreateVersion(
        string fragmentId,
        string content,
        IReadOnlyList<string> changedFields,
        string changeSummary)
    {
        var existingVersions = store.ListPromptFragmentVersions(fragmentId);
        var nextVersion = existingVersions.Count == 0 ? 1 : existingVersions.Max(v => v.Version) + 1;

        // 将旧版本标记为非活跃
        foreach (var old in existingVersions.Where(v => v.IsActive)) store.DeactivatePromptFragmentVersion(old.Id);

        var version = new PromptFragmentVersionDto(
            $"ver_{Guid.NewGuid():N}",
            fragmentId,
            nextVersion,
            content,
            changedFields,
            changeSummary,
            true,
            DateTimeOffset.UtcNow);

        store.SavePromptFragmentVersion(version);

        PublishVersionEvent("prompt.fragment.version_created", version);
        return version;
    }

    /// <summary>
    /// 回滚到指定版本。
    /// </summary>
    public PromptFragmentDto? RollbackToVersion(string fragmentId, int targetVersion)
    {
        var versions = store.ListPromptFragmentVersions(fragmentId);
        var target = versions.FirstOrDefault(v => v.Version == targetVersion);
        if (target is null) return null;

        var fragment = store.ListPromptFragments().FirstOrDefault(f => f.Id == fragmentId);
        if (fragment is null) return null;

        // 用旧版本内容更新片段，同时创建新的版本记录
        var updated = store.UpdatePromptFragment(fragmentId, new SavePromptFragmentRequest(
            fragment.Key,
            fragment.Title,
            fragment.Scope,
            fragment.TargetAgentId,
            fragment.Category,
            target.Content,
            fragment.Priority,
            fragment.Enabled));

        if (updated is null) return null;

        CreateVersion(fragmentId, target.Content, new[] { "content" }, $"Rolled back to version {targetVersion}");

        PublishVersionEvent("prompt.fragment.rolled_back", new PromptFragmentVersionDto(
            "", fragmentId, targetVersion, target.Content, Array.Empty<string>(),
            $"Rolled back to version {targetVersion}", true, DateTimeOffset.UtcNow));

        return updated;
    }

    /// <summary>
    /// 记录提示词片段的效果信号（正面/负面）。
    /// </summary>
    public PromptFragmentEffectivenessDto RecordSignal(PromptFragmentEffectivenessInput input)
    {
        store.RecordPromptFragmentSignal(input.FragmentId, input.Signal, input.RunId, input.SessionId, input.Note,
            input.Version);

        PublishSignalEvent(input);

        return GetEffectiveness(input.FragmentId);
    }

    /// <summary>
    /// 获取片段的效果统计。
    /// </summary>
    public PromptFragmentEffectivenessDto GetEffectiveness(string fragmentId)
    {
        var stats = store.GetPromptFragmentSignalStats(fragmentId);
        var versions = store.ListPromptFragmentVersions(fragmentId);
        var activeVersion = versions.FirstOrDefault(v => v.IsActive)?.Version ?? 0;

        var total = stats.PositiveCount + stats.NegativeCount;
        var score = total == 0 ? 0.5 : (double)stats.PositiveCount / total;

        return new PromptFragmentEffectivenessDto(
            fragmentId,
            activeVersion,
            total,
            stats.PositiveCount,
            stats.NegativeCount,
            Math.Round(score, 3),
            stats.LastEvaluatedAt,
            versions);
    }

    /// <summary>
    /// 批量获取所有片段的效果统计，按效果分数排序。
    /// </summary>
    public IReadOnlyList<PromptFragmentEffectivenessDto> ListEffectiveness()
    {
        var fragments = store.ListPromptFragments();
        return fragments
            .Select(f => GetEffectiveness(f.Id))
            .OrderByDescending(e => e.EffectivenessScore)
            .ToList();
    }

    /// <summary>
    /// A/B 对比两个版本的效果。
    /// </summary>
    public PromptFragmentAbTestResultDto CompareVersions(string fragmentId, int versionA, int versionB)
    {
        var versions = store.ListPromptFragmentVersions(fragmentId);
        var va = versions.FirstOrDefault(v => v.Version == versionA);
        var vb = versions.FirstOrDefault(v => v.Version == versionB);

        if (va is null || vb is null)
            return new PromptFragmentAbTestResultDto(fragmentId, versionA, versionB, null, null, 0, 0, 0.5,
                "One or both versions not found");

        var statsA = store.GetPromptFragmentVersionSignalStats(fragmentId, versionA);
        var statsB = store.GetPromptFragmentVersionSignalStats(fragmentId, versionB);

        var totalA = statsA.PositiveCount + statsA.NegativeCount;
        var totalB = statsB.PositiveCount + statsB.NegativeCount;
        var scoreA = totalA == 0 ? 0.5 : (double)statsA.PositiveCount / totalA;
        var scoreB = totalB == 0 ? 0.5 : (double)statsB.PositiveCount / totalB;

        var recommendation = (scoreA, scoreB) switch
        {
            _ when scoreA > scoreB + 0.1 =>
                $"Version {versionA} outperforms {versionB}. Consider keeping version {versionA}.",
            _ when scoreB > scoreA + 0.1 =>
                $"Version {versionB} outperforms {versionA}. Consider rolling back to version {versionB}.",
            _ => "Versions perform similarly. Consider collecting more signals before deciding."
        };

        return new PromptFragmentAbTestResultDto(
            fragmentId, versionA, versionB,
            va, vb,
            scoreA, scoreB,
            Math.Round(Math.Abs(scoreA - scoreB), 3),
            recommendation);
    }

    private void PublishVersionEvent(string type, PromptFragmentVersionDto version)
    {
        var envelope = store.AppendNewEvent(type, null, new JsonObject
        {
            ["fragment_id"] = version.FragmentId,
            ["version"] = version.Version,
            ["is_active"] = version.IsActive,
            ["change_summary"] = version.ChangeSummary
        }, ["prompt.fragment", "prompt.version"]);
        events.Publish(envelope);
    }

    private void PublishSignalEvent(PromptFragmentEffectivenessInput input)
    {
        var envelope = store.AppendNewEvent("prompt.fragment.signal_recorded", input.SessionId, new JsonObject
        {
            ["fragment_id"] = input.FragmentId,
            ["signal"] = input.Signal,
            ["run_id"] = input.RunId,
            ["note"] = input.Note
        }, ["prompt.fragment", "prompt.signal"]);
        events.Publish(envelope);
    }
}

/// <summary>
/// A/B 测试对比结果。
/// </summary>
public sealed record PromptFragmentAbTestResultDto(
    string FragmentId,
    int VersionA,
    int VersionB,
    PromptFragmentVersionDto? VersionADetails,
    PromptFragmentVersionDto? VersionBDetails,
    double ScoreA,
    double ScoreB,
    double ScoreDifference,
    string Recommendation);

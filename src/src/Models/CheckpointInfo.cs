namespace WinVM.Models;

public sealed class CheckpointInfo
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string SnapshotType { get; init; } = "";
    public DateTime? CreationTime { get; init; }
    public string ParentCheckpointName { get; init; } = "";
    /// <summary>Id of the parent checkpoint ("" for a root).</summary>
    public string ParentId { get; init; } = "";
    /// <summary>The VM's current state is based on this checkpoint (Hyper-V "Now" sits below it).</summary>
    public bool IsCurrent { get; init; }

    public string CreationText => CreationTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public string TypeText => SnapshotType switch
    {
        "" or null => "标准",
        "Standard" => "标准",
        "Recovery" => "恢复",
        "Planned" => "计划",
        "Missing" => "缺失",
        "Replica" => "副本",
        "AppConsistentReplica" => "应用一致副本",
        "SyncedReplica" => "同步副本",
        _ => SnapshotType
    };

    public string ParentText => string.IsNullOrWhiteSpace(ParentCheckpointName) ? "—" : ParentCheckpointName;
}

/// <summary>Node of the checkpoint tree: a checkpoint, or the 「当前位置」 marker (the VM's running state).</summary>
public sealed class CheckpointNode
{
    public CheckpointInfo? Checkpoint { get; init; }
    public bool IsNowMarker => Checkpoint == null;
    public List<CheckpointNode> Children { get; } = new();
    public string Name => Checkpoint?.Name ?? "当前位置";
    public string CreationText => Checkpoint?.CreationText ?? "";
    public string TypeText => Checkpoint?.TypeText ?? "";
    public bool IsCurrent => Checkpoint?.IsCurrent == true;

    /// <summary>Builds the parent/child tree (children by creation time) and places the 「当前位置」 marker under
    /// the current checkpoint, or at the root when the VM is not based on any checkpoint. Orphans become roots.</summary>
    public static List<CheckpointNode> Build(IEnumerable<CheckpointInfo> checkpoints)
    {
        var list = checkpoints.Where(c => !string.IsNullOrEmpty(c.Id)).GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(c => c.CreationTime ?? DateTime.MinValue).ToList();
        var nodes = list.ToDictionary(c => c.Id, c => new CheckpointNode { Checkpoint = c }, StringComparer.OrdinalIgnoreCase);
        var roots = new List<CheckpointNode>();
        foreach (var c in list)
        {
            var n = nodes[c.Id];
            if (!string.IsNullOrEmpty(c.ParentId) && nodes.TryGetValue(c.ParentId, out var parent)
                && !ReferenceEquals(parent, n) && !IsDescendant(parent, of: n))
                parent.Children.Add(n);
            else
                roots.Add(n);
        }
        if (list.Count > 0)
        {
            var current = list.FirstOrDefault(c => c.IsCurrent);
            var marker = new CheckpointNode();
            if (current != null) nodes[current.Id].Children.Add(marker);
            else roots.Add(marker);
        }
        return roots;
    }

    /// <summary>Is <paramref name="node"/> somewhere below <paramref name="of"/> (cycle guard for malformed data)?</summary>
    private static bool IsDescendant(CheckpointNode node, CheckpointNode of)
    {
        var stack = new Stack<CheckpointNode>(of.Children);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (ReferenceEquals(x, node)) return true;
            foreach (var ch in x.Children) stack.Push(ch);
        }
        return false;
    }

    public static IEnumerable<CheckpointNode> Flatten(IEnumerable<CheckpointNode> roots)
    {
        foreach (var r in roots)
        {
            yield return r;
            foreach (var c in Flatten(r.Children)) yield return c;
        }
    }
}

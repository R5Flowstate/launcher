namespace R5Flowstate.Content;

/// <summary>What Current/Total count, so the UI does not have to guess.</summary>
public enum ProgressUnit
{
    Unknown = 0,
    Bytes = 1,
    Items = 2,
}

/// <summary>One file currently being transferred.</summary>
public sealed class ActiveTransfer
{
    public string Path { get; init; } = string.Empty;
    public long Done { get; init; }
    public long Total { get; init; }

    public double Percent => Total > 0 ? Math.Min(100.0, 100.0 * Done / Total) : 0;
}

public sealed class ContentInstallProgress
{
    public ProgressUnit Unit { get; set; } = ProgressUnit.Unknown;

    public string Phase { get; set; } = string.Empty;

    public string Track { get; set; } = string.Empty;

    public long Current { get; set; }

    public long Total { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public int StepIndex { get; set; }

    public int StepCount { get; set; }

    public long JobCurrent { get; set; }

    public long JobTotal { get; set; }

    /// <summary>Files finished, out of files planned. Not bytes.</summary>
    public int ItemsDone { get; set; }

    public int ItemsTotal { get; set; }

    /// <summary>
    /// Snapshot of what is in flight right now. Several files download at once,
    /// so a single current-file line would only ever show one of them.
    /// </summary>
    public IReadOnlyList<ActiveTransfer>? Active { get; set; }
}

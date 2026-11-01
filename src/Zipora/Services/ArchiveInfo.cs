namespace Zipora.Services;

/// <summary>
/// 7-Zip 技术清单中描述整个压缩包的字段。
/// </summary>
public sealed record ArchiveInfo
{
    public string Format { get; init; } = "";
    public long PhysicalSize { get; init; } = -1;
    public long TotalPhysicalSize { get; init; } = -1;
    public string Method { get; init; } = "";
    public bool? Solid { get; init; }
    public int VolumeCount { get; init; } = 1;
}

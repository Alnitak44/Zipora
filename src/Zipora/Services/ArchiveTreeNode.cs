using System;
using System.Collections.ObjectModel;

namespace Zipora.Services;

/// <summary>压缩包目录树的一个节点。FullPath 始终使用 /，与源压缩格式无关。</summary>
public sealed class ArchiveTreeNode
{
    public ArchiveTreeNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<ArchiveTreeNode> Children { get; } = new();
}

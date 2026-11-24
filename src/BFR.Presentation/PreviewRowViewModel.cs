namespace BFR.Presentation;

using BFR.Core;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Một đoạn chữ trong tên, dùng để tô màu phần thêm/bị xóa (không chỉ dựa vào màu: có gạch ngang).</summary>
public readonly record struct DiffSegment(string Text, bool Highlight)
{
    /// <summary>Hiển thị trực quan khoảng trắng trong đoạn bị thay đổi/xóa bằng ký hiệu '·'.</summary>
    public string DisplayText => Highlight ? Text.Replace(' ', '·') : Text;
}

/// <summary>Một dòng của bảng xem trước.</summary>
public sealed class PreviewRowViewModel : ObservableObject
{
    private const int MaxLcsLength = 256;
    private readonly IStringProvider _strings;

    public PreviewRowViewModel(
        FileItem file,
        string oldName,
        string newName,
        PreviewStatus status,
        string? detail,
        bool isSelected = true,
        IStringProvider? strings = null,
        bool hasManualOverride = false)
    {
        _strings = strings ?? new KeyOnlyStringProvider();

        File = file;
        OldName = oldName;
        NewName = newName;
        Status = status;
        Detail = detail ?? string.Empty;
        IsSelected = isSelected;
        HasManualOverride = hasManualOverride;

        var diff = Diff(oldName, newName);
        OldSegments = diff.Old;
        NewSegments = diff.New;
    }

    public FileItem File { get; }

    public string OldName { get; }

    public string NewName { get; }

    public PreviewStatus Status { get; }

    public string Detail { get; }

    /// <summary>True nếu dòng này đang dùng tên thủ công do người dùng ghi đè (F2 / Import).</summary>
    public bool HasManualOverride { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _isSelected = true;

    public IReadOnlyList<DiffSegment> OldSegments { get; }

    public IReadOnlyList<DiffSegment> NewSegments { get; }

    public string FolderText => File.Directory;

    public string StatusText => Status switch
    {
        PreviewStatus.Ok => _strings.T("StatusOk"),
        PreviewStatus.Unchanged => _strings.T("StatusUnchanged"),
        PreviewStatus.Conflict => _strings.T("StatusConflict"),
        PreviewStatus.InvalidName => _strings.T("StatusInvalidName"),
        PreviewStatus.PathTooLong => _strings.T("StatusPathTooLong"),
        PreviewStatus.Locked => _strings.T("StatusLocked"),
        PreviewStatus.MissingData => _strings.T("StatusMissingData"),
        PreviewStatus.Protected => _strings.T("StatusProtected"),
        _ => Status.ToString()
    };

    /// <summary>Icon trạng thái (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
    public string StatusIcon => Status switch
    {
        PreviewStatus.Ok => "\uE73E",          // CheckMark
        PreviewStatus.Unchanged => "\uE738",   // Remove / Dash
        PreviewStatus.Conflict => "\uE7BA",    // Warning
        PreviewStatus.MissingData => "\uE7BA", // Warning
        PreviewStatus.InvalidName => "\uEA39", // ErrorBadge
        PreviewStatus.PathTooLong => "\uEA39", // ErrorBadge
        PreviewStatus.Protected => "\uEA39",   // ErrorBadge
        PreviewStatus.Locked => "\uE72E",      // Lock
        _ => "\uE7BA"
    };

    /// <summary>
    /// Tính diff chi tiết bằng LCS cho chuỗi <= 256 ký tự (fallback về prefix/suffix cho chuỗi dài hơn
    /// để bảo toàn tốc độ preview 100.000 tệp).
    /// </summary>
    public static (IReadOnlyList<DiffSegment> Old, IReadOnlyList<DiffSegment> New) Diff(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return (new[] { new DiffSegment(oldName, false) }, new[] { new DiffSegment(newName, false) });
        }

        var oldSpan = oldName.AsSpan();
        var newSpan = newName.AsSpan();

        int prefix = 0;
        int maxPrefix = Math.Min(oldSpan.Length, newSpan.Length);
        while (prefix < maxPrefix && oldSpan[prefix] == newSpan[prefix]) prefix++;

        int suffix = 0;
        int maxSuffix = Math.Min(oldSpan.Length - prefix, newSpan.Length - prefix);
        while (suffix < maxSuffix &&
               oldSpan[oldSpan.Length - 1 - suffix] == newSpan[newSpan.Length - 1 - suffix]) suffix++;

        var oldMidLen = oldSpan.Length - prefix - suffix;
        var newMidLen = newSpan.Length - prefix - suffix;

        if (oldMidLen == 0 || newMidLen == 0 || oldName.Length > MaxLcsLength || newName.Length > MaxLcsLength)
        {
            return BuildPrefixSuffixDiff(oldName, newName, prefix, suffix, oldMidLen, newMidLen);
        }

        var oldMid = oldSpan.Slice(prefix, oldMidLen);
        var newMid = newSpan.Slice(prefix, newMidLen);

        int rows = oldMidLen + 1;
        int cols = newMidLen + 1;
        var dp = new int[rows * cols];

        for (int i = oldMidLen - 1; i >= 0; i--)
        {
            int rowOffset = i * cols;
            int nextRowOffset = (i + 1) * cols;
            char oc = oldMid[i];
            for (int j = newMidLen - 1; j >= 0; j--)
            {
                dp[rowOffset + j] = oc == newMid[j]
                    ? dp[nextRowOffset + j + 1] + 1
                    : Math.Max(dp[nextRowOffset + j], dp[rowOffset + j + 1]);
            }
        }

        // Nếu không có ký tự chung nào ở phần giữa, giữ nguyên 1 khối highlight gọn gàng.
        if (dp[0] == 0)
        {
            return BuildPrefixSuffixDiff(oldName, newName, prefix, suffix, oldMidLen, newMidLen);
        }

        var oldSegments = new List<DiffSegment>();
        var newSegments = new List<DiffSegment>();

        if (prefix > 0)
        {
            AppendSegment(oldSegments, oldName[..prefix], false);
            AppendSegment(newSegments, newName[..prefix], false);
        }

        int oi = 0;
        int ni = 0;
        var oldBuf = new System.Text.StringBuilder();
        var newBuf = new System.Text.StringBuilder();
        bool oldCurHighlight = false;
        bool newCurHighlight = false;

        void FlushOld()
        {
            if (oldBuf.Length > 0)
            {
                AppendSegment(oldSegments, oldBuf.ToString(), oldCurHighlight);
                oldBuf.Clear();
            }
        }

        void FlushNew()
        {
            if (newBuf.Length > 0)
            {
                AppendSegment(newSegments, newBuf.ToString(), newCurHighlight);
                newBuf.Clear();
            }
        }

        void PushOld(char ch, bool highlight)
        {
            if (oldBuf.Length > 0 && oldCurHighlight != highlight) FlushOld();
            oldCurHighlight = highlight;
            oldBuf.Append(ch);
        }

        void PushNew(char ch, bool highlight)
        {
            if (newBuf.Length > 0 && newCurHighlight != highlight) FlushNew();
            newCurHighlight = highlight;
            newBuf.Append(ch);
        }

        while (oi < oldMidLen && ni < newMidLen)
        {
            if (oldMid[oi] == newMid[ni])
            {
                PushOld(oldMid[oi], false);
                PushNew(newMid[ni], false);
                oi++;
                ni++;
            }
            else if (dp[(oi + 1) * cols + ni] >= dp[oi * cols + (ni + 1)])
            {
                PushOld(oldMid[oi], true);
                oi++;
            }
            else
            {
                PushNew(newMid[ni], true);
                ni++;
            }
        }

        while (oi < oldMidLen)
        {
            PushOld(oldMid[oi++], true);
        }

        while (ni < newMidLen)
        {
            PushNew(newMid[ni++], true);
        }

        FlushOld();
        FlushNew();

        if (suffix > 0)
        {
            AppendSegment(oldSegments, oldName[(oldName.Length - suffix)..], false);
            AppendSegment(newSegments, newName[(newName.Length - suffix)..], false);
        }

        return (oldSegments, newSegments);
    }

    private static (IReadOnlyList<DiffSegment> Old, IReadOnlyList<DiffSegment> New) BuildPrefixSuffixDiff(
        string oldName, string newName, int prefix, int suffix, int oldMidLen, int newMidLen)
    {
        var oldSegments = new List<DiffSegment>();
        var newSegments = new List<DiffSegment>();

        if (prefix > 0)
        {
            oldSegments.Add(new DiffSegment(oldName[..prefix], false));
            newSegments.Add(new DiffSegment(newName[..prefix], false));
        }

        if (oldMidLen > 0) oldSegments.Add(new DiffSegment(oldName[prefix..(oldName.Length - suffix)], true));
        if (newMidLen > 0) newSegments.Add(new DiffSegment(newName[prefix..(newName.Length - suffix)], true));

        if (suffix > 0)
        {
            oldSegments.Add(new DiffSegment(oldName[(oldName.Length - suffix)..], false));
            newSegments.Add(new DiffSegment(newName[(newName.Length - suffix)..], false));
        }

        return (oldSegments, newSegments);
    }

    private static void AppendSegment(List<DiffSegment> list, string text, bool highlight)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (list.Count > 0 && list[^1].Highlight == highlight)
        {
            list[^1] = new DiffSegment(list[^1].Text + text, highlight);
        }
        else
        {
            list.Add(new DiffSegment(text, highlight));
        }
    }
}

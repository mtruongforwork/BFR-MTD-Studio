namespace BFR.Core.Tests.Support;

using System;
using System.IO;
using BFR.Core;

/// <summary>Mô phỏng tiến trình chết đột ngột (mất điện, kill): không nằm trong nhóm ngoại lệ mà bộ thực thi bắt, nên thoát thẳng khỏi Execute.</summary>
public sealed class SimulatedCrash : Exception
{
    public SimulatedCrash(string message) : base(message) { }
}

public enum FaultKind
{
    /// <summary>Lỗi IO tạm thời ở lần gọi thứ k, các lần sau bình thường (tệp bị khoá, ổ đầy thoáng qua).</summary>
    TransientIoError,
    /// <summary>Từ lần gọi thứ k trở đi MỌI lần ghi đều lỗi IO (ổ đầy, mất kết nối ổ mạng).</summary>
    PersistentIoError,
    /// <summary>Tiến trình chết TRƯỚC khi lần gọi thứ k có hiệu lực.</summary>
    CrashBefore,
    /// <summary>Tiến trình chết SAU khi lần gọi thứ k đã có hiệu lực (cửa sổ nguy hiểm: đã đổi tên nhưng chưa kịp ghi "done").</summary>
    CrashAfter
}

/// <summary>
/// Bọc <see cref="FakeFileSystem"/>, đếm mọi lần gọi GHI (kể cả ghi journal) và gây lỗi đúng ở lần thứ <c>FailAt</c>.
/// Sau khi "chết" (<see cref="FaultKind.CrashBefore"/>/<see cref="FaultKind.CrashAfter"/>) mọi lần gọi tiếp theo cũng ném
/// <see cref="SimulatedCrash"/>; giai đoạn khôi phục dùng thẳng <see cref="Inner"/> như một tiến trình mới.
/// </summary>
public sealed class FaultInjectingFileSystem : IFileSystem
{
    private readonly FaultKind _kind;
    private readonly int _failAt;
    private bool _dead;

    public FaultInjectingFileSystem(FakeFileSystem inner, FaultKind kind = FaultKind.TransientIoError, int failAt = int.MaxValue)
    {
        Inner = inner;
        _kind = kind;
        _failAt = failAt;
    }

    public FakeFileSystem Inner { get; }

    /// <summary>Số lần gọi ghi đã thấy (dùng ở lượt chạy sạch để biết cần thử bao nhiêu điểm lỗi).</summary>
    public int MutatingCalls { get; private set; }

    public bool Triggered { get; private set; }

    private void Mutate(string what, Action action)
    {
        if (_dead) throw new SimulatedCrash("tiến trình đã chết: " + what);

        MutatingCalls++;
        if (_kind == FaultKind.PersistentIoError && MutatingCalls >= _failAt)
        {
            Triggered = true;
            throw new IOException("Lỗi IO kéo dài giả lập ở " + what);
        }

        if (MutatingCalls != _failAt)
        {
            action();
            return;
        }

        Triggered = true;
        switch (_kind)
        {
            case FaultKind.TransientIoError:
                throw new IOException("Lỗi IO giả lập ở " + what);
            case FaultKind.CrashBefore:
                _dead = true;
                throw new SimulatedCrash("chết trước " + what);
            default:
                action();
                _dead = true;
                throw new SimulatedCrash("chết sau " + what);
        }
    }

    private T Read<T>(Func<T> read)
    {
        if (_dead) throw new SimulatedCrash("tiến trình đã chết");
        return read();
    }

    // --- Ghi ---
    public void EnsureDirectory(string path) => Mutate("EnsureDirectory", () => Inner.EnsureDirectory(path));
    public void Move(string from, string to, bool overwrite) => Mutate("Move", () => Inner.Move(from, to, overwrite));
    public void Copy(string from, string to, bool overwrite) => Mutate("Copy", () => Inner.Copy(from, to, overwrite));
    public void WriteAllText(string path, string content) => Mutate("WriteAllText", () => Inner.WriteAllText(path, content));
    public void AppendAllText(string path, string content) => Mutate("AppendAllText", () => Inner.AppendAllText(path, content));
    public void Delete(string path, bool recursive) => Mutate("Delete", () => Inner.Delete(path, recursive));
    public void SetTimes(string path, FileTimes times) => Mutate("SetTimes", () => Inner.SetTimes(path, times));
    public void SetAttributes(string path, FileAttributes attributes) => Mutate("SetAttributes", () => Inner.SetAttributes(path, attributes));
    public void WriteAllBytes(string path, byte[] content) => Mutate("WriteAllBytes", () => Inner.WriteAllBytes(path, content));

    public Stream OpenWrite(string path)
    {
        Stream? stream = null;
        Mutate("OpenWrite", () => stream = Inner.OpenWrite(path));
        return stream!;
    }

    // --- Đọc ---
    public bool FileExists(string path) => Read(() => Inner.FileExists(path));
    public bool DirectoryExists(string path) => Read(() => Inner.DirectoryExists(path));
    public string[] GetFiles(string directory, string pattern, bool recursive) => Read(() => Inner.GetFiles(directory, pattern, recursive));
    public string[] GetDirectories(string directory, bool recursive) => Read(() => Inner.GetDirectories(directory, recursive));
    public string ReadAllText(string path) => Read(() => Inner.ReadAllText(path));
    public long GetLength(string path) => Read(() => Inner.GetLength(path));
    public DateTime GetCreationTimeUtc(string path) => Read(() => Inner.GetCreationTimeUtc(path));
    public DateTime GetLastWriteTimeUtc(string path) => Read(() => Inner.GetLastWriteTimeUtc(path));
    public FileTimes GetTimes(string path) => Read(() => Inner.GetTimes(path));
    public FileAttributes GetAttributes(string path) => Read(() => Inner.GetAttributes(path));
    public bool IsDirectoryEmpty(string path) => Read(() => Inner.IsDirectoryEmpty(path));
    public bool IsLink(string path) => Read(() => Inner.IsLink(path));
    public Stream OpenRead(string path) => Read(() => Inner.OpenRead(path));
}

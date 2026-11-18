namespace BFR.Core;

using System;

/// <summary>
/// Phát hiện UI bị treo từ nhịp tim: luồng UI gọi <see cref="Beat"/> đều đặn, luồng giám sát gọi <see cref="Evaluate"/>.
/// Quá <c>threshold</c> mà không có nhịp thì coi là treo. Thuần logic (đồng hồ truyền vào) nên test được.
/// </summary>
public sealed class HangDetector
{
    public enum State
    {
        /// <summary>Đang phản hồi bình thường.</summary>
        Ok,
        /// <summary>Vừa phát hiện treo (chỉ trả về một lần mỗi đợt treo).</summary>
        Hung,
        /// <summary>Vẫn đang treo từ lần trước.</summary>
        StillHung,
        /// <summary>Vừa phản hồi lại sau một đợt treo (chỉ một lần).</summary>
        Recovered
    }

    private readonly object _gate = new();
    private readonly TimeSpan _threshold;
    private DateTime _lastBeat;
    private bool _hung;

    public HangDetector(TimeSpan threshold, DateTime utcNow)
    {
        if (threshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
        _lastBeat = utcNow;
    }

    /// <summary>Thời gian không có nhịp tại lần <see cref="Evaluate"/> gần nhất (hữu ích khi ghi log).</summary>
    public TimeSpan LastStall { get; private set; }

    public void Beat(DateTime utcNow)
    {
        lock (_gate) _lastBeat = utcNow;
    }

    public State Evaluate(DateTime utcNow)
    {
        lock (_gate)
        {
            LastStall = utcNow - _lastBeat;
            var stalled = LastStall >= _threshold;

            if (stalled && !_hung) { _hung = true; return State.Hung; }
            if (stalled) return State.StillHung;
            if (_hung) { _hung = false; return State.Recovered; }
            return State.Ok;
        }
    }
}

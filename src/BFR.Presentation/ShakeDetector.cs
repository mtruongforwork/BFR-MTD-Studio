namespace BFR.Presentation;

/// <summary>
/// Nhận diện "lắc ngang" từ chuỗi điểm chuột (x, thời gian ms).
/// Lắc = đủ số lần đổi chiều ngang trong cửa sổ thời gian, mỗi nhịp lắc đủ biên độ.
/// Logic thuần, không phụ thuộc Win32 nên test được.
/// </summary>
public sealed class ShakeDetector
{
    private readonly int _windowMs;
    private readonly int _minSwingPx;
    private readonly int _reversals;
    private readonly int _cooldownMs;
    private readonly int _minSampleGapMs;

    private readonly List<(int X, long T)> _samples = new();
    private long _lastTriggerT = long.MinValue / 2;

    /// <param name="minSampleGapMs">
    /// Mẫu đến dày hơn mức này bị bỏ. Hook chuột toàn cục xử lý ĐỒNG BỘ từng sự kiện: chuột 8 kHz gửi một sự kiện mỗi 125 µs, nên nếu mỗi mẫu
    /// tốn hàng trăm µs (quét lại cả cửa sổ 700 ms) thì sự kiện dồn ứ và chuột toàn hệ thống bị trễ khi đang kéo. Một nhịp lắc cần biên độ
    /// vài chục px trong hàng chục ms, nên lấy mẫu mỗi 4 ms không làm mất nhận diện.
    /// </param>
    public ShakeDetector(int windowMs = 700, int minSwingPx = 40, int reversals = 3, int cooldownMs = 1000, int minSampleGapMs = 4)
    {
        _minSampleGapMs = minSampleGapMs;
        _windowMs = windowMs;
        _minSwingPx = minSwingPx;
        _reversals = reversals;
        _cooldownMs = cooldownMs;
    }

    public void Reset() => _samples.Clear();

    /// <summary>Đưa một mẫu vào; trả true đúng lúc phát hiện lắc.</summary>
    public bool Feed(int x, long tMs)
    {
        // Bỏ mẫu quá dày (chỉ khi thời gian đi tới: thời gian đi lùi vẫn được nhận để không kẹt trạng thái).
        if (_samples.Count > 0)
        {
            var last = _samples[^1].T;
            if (tMs >= last && tMs - last < _minSampleGapMs) return false;
        }

        _samples.Add((x, tMs));
        var expired = 0;
        while (expired < _samples.Count && tMs - _samples[expired].T > _windowMs) expired++;
        if (expired > 0) _samples.RemoveRange(0, expired);

        if (tMs - _lastTriggerT < _cooldownMs) return false;
        if (CountReversals() < _reversals) return false;

        _lastTriggerT = tMs;
        _samples.Clear();
        return true;
    }

    private int CountReversals()
    {
        // Đếm điểm đảo chiều: mỗi lần quãng đường ngược chiều so với cực trị trước ≥ _minSwingPx.
        int count = 0, dir = 0, extreme = _samples[0].X;
        foreach (var (x, _) in _samples)
        {
            if (dir >= 0 && x - extreme <= -_minSwingPx)
            {
                if (dir != 0) count++;
                dir = -1; extreme = x;
            }
            else if (dir <= 0 && x - extreme >= _minSwingPx)
            {
                if (dir != 0) count++;
                dir = 1; extreme = x;
            }
            else if ((dir == 1 && x > extreme) || (dir == -1 && x < extreme))
            {
                extreme = x;
            }
        }
        return count;
    }
}

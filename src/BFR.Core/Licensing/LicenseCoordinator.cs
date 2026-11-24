namespace BFR.Core.Licensing;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Điều phối giữa trạng thái cục bộ và Microsoft Store IAP:
/// tự đồng bộ giấy phép khi khởi động và sau khi mua.
/// </summary>
public sealed class LicenseCoordinator
{
    private readonly LicenseService _license;
    private readonly IStoreLicenseProvider? _provider;

    public LicenseCoordinator(LicenseService license, IStoreLicenseProvider? provider = null)
    {
        _license = license;
        _provider = provider;
    }

    /// <summary>Thời gian tối đa chờ Store trả giấy phép khi khởi động. Quá hạn thì giữ trạng thái cục bộ (Free hoặc Pro đã ghi nhận).</summary>
    public static TimeSpan DefaultStoreTimeout { get; } = TimeSpan.FromSeconds(20);

    public LicenseService License => _license;

    public LicenseStatus Status => _license.Status;

    public IStoreLicenseProvider? Provider => _provider;

    /// <summary>
    /// Đồng bộ giấy phép: nếu Store báo đã mua Pro thì ghi nhận vào bản ghi cục bộ;
    /// nếu chưa mua thì giữ nguyên trạng thái (kèm dùng thử nếu đủ điều kiện).
    /// </summary>
    public async Task<LicenseStatus> RefreshAsync(CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (_provider is null || !_provider.IsAvailable)
        {
            return _license.Evaluate();
        }

        StoreLicenseSnapshot snapshot;
        try
        {
            // Store ngoại tuyến hoặc treo (WinRT không nhận CancellationToken): không chờ vô hạn.
            snapshot = await _provider.GetLicenseAsync(ct).WaitAsync(timeout ?? DefaultStoreTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Lỗi, hết thời gian chờ (TimeoutException) hoặc Store không trả lời: giữ trạng thái cục bộ.
            return _license.Evaluate();
        }

        if (snapshot.HasPro)
        {
            _license.RecordPurchase(LicenseService.ProProductId);
        }
        else if (string.IsNullOrWhiteSpace(snapshot.Error))
        {
            // Store xác nhận rõ ràng không có Pro (không phải lỗi mạng/Store):
            // thu hồi trạng thái mua đã cache cục bộ nếu trước đó được cấp qua Store (SEC-2).
            if (!_license.ActivatedByKey && !string.IsNullOrWhiteSpace(_license.Status.ProductId))
            {
                _license.ClearPurchase();
            }

            if (snapshot.IsTrialActive)
            {
                // Store trial: dùng thử 30 ngày cục bộ tương ứng với thời hạn Store.
                if (!_license.Status.IsTrial && _license.StartTrial()) { /* bắt đầu dùng thử */ }
            }
        }

        return _license.Evaluate();
    }

    /// <summary>
    /// Mua Pro. Trả về kết quả giao dịch; nếu thành công thì cấp đã được cập nhật.
    /// </summary>
    public async Task<PurchaseOutcome> PurchaseProAsync(CancellationToken ct = default)
    {
        if (_provider is null || !_provider.IsAvailable)
        {
            return new PurchaseOutcome(PurchaseStatus.NotSupported, CoreText.Get("CoreStoreNotSupported", "Mua Pro chỉ khả dụng trong bản cài từ Microsoft Store."));
        }

        PurchaseOutcome outcome;
        try
        {
            outcome = await _provider.PurchaseAsync(LicenseService.ProProductId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PurchaseOutcome(PurchaseStatus.Failed, ex.Message);
        }

        if (outcome.IsSuccess)
        {
            _license.RecordPurchase(LicenseService.ProProductId);
        }

        return outcome;
    }
}

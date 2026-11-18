namespace BFR.Store;

using BFR.Core.Licensing;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

/// <summary>
/// Cài đặt Microsoft Store IAP bằng Windows Runtime (Windows.Services.Store).
///
/// Yêu cầu ứng dụng có package identity (gói MSIX). Ứng dụng chạy ngoài Store
/// (bản portable) sẽ nhận <c>IsAvailable = false</c> và chỉ dùng được tính năng Free.
/// </summary>
public sealed class MicrosoftStoreLicenseProvider : IStoreLicenseProvider
{
    private readonly string _productId;
    private readonly Func<IntPtr>? _ownerWindow;
    private StoreContext? _context;
    private bool _ownerInitialized;

    /// <param name="ownerWindow">
    /// Trả về HWND cửa sổ chính (gọi trên luồng giao diện). Ứng dụng desktop (WPF/WinForms) PHẢI gắn cửa sổ chủ cho StoreContext
    /// (IInitializeWithWindow) trước khi gọi API hiện hộp thoại như RequestPurchaseAsync, nếu không lời gọi lỗi hoặc hộp thoại mua không hiện.
    /// </param>
    public MicrosoftStoreLicenseProvider(string productId = LicenseService.ProProductId, Func<IntPtr>? ownerWindow = null)
    {
        _productId = productId;
        _ownerWindow = ownerWindow;
    }

    /// <summary>Gắn cửa sổ chủ một lần. Lỗi ở đây không được chặn việc mua: ghi nhận rồi thử gọi tiếp.</summary>
    private void EnsureOwnerWindow()
    {
        if (_ownerInitialized || _context is null || _ownerWindow is null) return;
        try
        {
            var hwnd = _ownerWindow();
            if (hwnd == IntPtr.Zero) return;
            StoreWindow.Initialize(_context, hwnd);
            _ownerInitialized = true;
        }
        catch (Exception)
        {
            // Không gắn được (không có cửa sổ, không phải luồng giao diện): vẫn thử RequestPurchaseAsync.
        }
    }

    /// <summary>True nếu môi trường hiện tại có thể dùng Store IAP.</summary>
    public bool IsAvailable
    {
        get
        {
            if (_context is not null) return true;

            try
            {
                _context = StoreContext.GetDefault();
                return _context is not null;
            }
            catch (Exception)
            {
                // 0x80073D54: ứng dụng không có package identity (chạy ngoài Store).
                _context = null;
                return false;
            }
        }
    }

    public async Task<StoreLicenseSnapshot> GetLicenseAsync(CancellationToken ct = default)
    {
        if (!IsAvailable || _context is null)
        {
            return new StoreLicenseSnapshot { Error = BFR.Core.CoreText.Get("CoreStoreNoIdentity", "Không có package identity: không đọc được giấy phép Store.") };
        }

        try
        {
            var appLicense = await _context.GetAppLicenseAsync();

            StoreLicense? product = null;
            if (appLicense.AddOnLicenses is not null &&
                appLicense.AddOnLicenses.TryGetValue(_productId, out var found))
            {
                product = found;
            }

            var isActive = product is not null && product.IsActive;

            var trialActive = appLicense.IsTrial && appLicense.IsTrialOwnedByThisUser;

            return new StoreLicenseSnapshot
            {
                IsProActive = isActive,
                // API 19041 không expose cờ grace period cho add-on; coi như không có.
                InGracePeriod = false,
                ExpirationDate = product?.ExpirationDate,
                IsTrialActive = trialActive,
                TrialExpirationDate = trialActive ? appLicense.ExpirationDate : null
            };
        }
        catch (Exception ex)
        {
            return new StoreLicenseSnapshot { Error = ex.Message };
        }
    }

    public async Task<PurchaseOutcome> PurchaseAsync(string productId, CancellationToken ct = default)
    {
        if (!IsAvailable || _context is null)
        {
            return new PurchaseOutcome(PurchaseStatus.NotSupported, BFR.Core.CoreText.Get("CoreStoreNotSupported", "Mua Pro chỉ khả dụng trong bản cài từ Microsoft Store."));
        }

        try
        {
            EnsureOwnerWindow();   // phải chạy trước await đầu tiên: còn ở luồng giao diện
            var result = await _context.RequestPurchaseAsync(productId);
            var status = result.Status;

            return status switch
            {
                StorePurchaseStatus.Succeeded => new PurchaseOutcome(PurchaseStatus.Completed),
                StorePurchaseStatus.AlreadyPurchased => new PurchaseOutcome(PurchaseStatus.Completed),
                StorePurchaseStatus.NotPurchased => new PurchaseOutcome(PurchaseStatus.NotPurchased),
                StorePurchaseStatus.NetworkError => new PurchaseOutcome(PurchaseStatus.StoreNotAvailable),
                StorePurchaseStatus.ServerError => new PurchaseOutcome(PurchaseStatus.Failed, BFR.Core.CoreText.Get("CoreStoreServerError", "Lỗi máy chủ Store.")),
                _ => new PurchaseOutcome(PurchaseStatus.Failed, status.ToString())
            };
        }
        catch (Exception ex)
        {
            return new PurchaseOutcome(PurchaseStatus.Failed, ex.Message);
        }
    }
}

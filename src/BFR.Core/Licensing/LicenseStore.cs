namespace BFR.Core.Licensing;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Lưu/đọc trạng thái giấy phép cục bộ với ghi nguyên tử (tệp .bak) và mã hoá Windows DPAPI
/// (CurrentUser + entropy riêng của BFR). Tệp JSON plaintext từ phiên bản cũ được tự động đọc
/// và migrate sang định dạng mã hoá ở lần tải đầu tiên.
/// </summary>
public sealed class LicenseStore
{
    public const string DpapiHeader = "BFRDPAPI1:";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("BFR.LicenseStore.v2.2.DPAPI.CurrentUser");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IFileSystem _fs;
    private readonly string _path;

    public LicenseStore(IFileSystem fs, string? path = null)
    {
        _fs = fs;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchFileRenamer", "license.json");
    }

    public string FilePath => _path;

    public LicenseRecord Load()
    {
        try
        {
            var pathToRead = _path;
            if (!_fs.FileExists(pathToRead))
            {
                // Khôi phục sau sự cố giữa hai bước Move trong Save(): license.json đã chuyển sang .bak nhưng .tmp chưa kịp thay vào.
                var backup = _path + ".bak";
                if (!_fs.FileExists(backup)) return new LicenseRecord();
                pathToRead = backup;
            }

            var raw = _fs.ReadAllText(pathToRead);
            if (string.IsNullOrWhiteSpace(raw)) return new LicenseRecord();

            if (raw.StartsWith(DpapiHeader, StringComparison.Ordinal))
            {
                var payload = raw[DpapiHeader.Length..].Trim();
                var cipher = Convert.FromBase64String(payload);
                var plainBytes = DpapiUnprotect(cipher);
                var json = Encoding.UTF8.GetString(plainBytes);
                var record = JsonSerializer.Deserialize<LicenseRecord>(json, JsonOptions) ?? new LicenseRecord();
                if (!string.Equals(pathToRead, _path, StringComparison.Ordinal))
                {
                    try { Save(record); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
                }
                return record;
            }

            // Tệp plaintext cũ (v2.1 trở về trước): đọc và tự động migrate sang DPAPI.
            var legacy = JsonSerializer.Deserialize<LicenseRecord>(raw, JsonOptions);
            if (legacy is null) return new LicenseRecord();

            if (OperatingSystem.IsWindows())
            {
                try { Save(legacy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    // Bỏ qua lỗi ghi migrate nếu thư mục chỉ đọc.
                }
            }

            return legacy;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or CryptographicException or UnauthorizedAccessException)
        {
            return new LicenseRecord();
        }
    }

    public void Save(LicenseRecord record)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) _fs.EnsureDirectory(dir);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        var content = OperatingSystem.IsWindows()
            ? DpapiHeader + Convert.ToBase64String(DpapiProtect(Encoding.UTF8.GetBytes(json)))
            : json;

        var temp = _path + ".tmp";
        var backup = _path + ".bak";
        _fs.WriteAllText(temp, content);
        if (_fs.FileExists(_path)) _fs.Move(_path, backup, overwrite: true);
        _fs.Move(temp, _path, overwrite: true);
    }

    // -----------------------------------------------------------------
    // Windows DPAPI (CurrentUser + entropy) qua crypt32.dll — không cần thêm NuGet.
    // -----------------------------------------------------------------

    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] DpapiProtect(byte[] plain)
    {
        var dataIn = AllocBlob(plain);
        var entropy = AllocBlob(DpapiEntropy);
        var dataOut = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out dataOut))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            FreeHGlobalBlob(dataIn);
            FreeHGlobalBlob(entropy);
            if (dataOut.pbData != IntPtr.Zero) LocalFree(dataOut.pbData);
        }
    }

    private static byte[] DpapiUnprotect(byte[] cipher)
    {
        var dataIn = AllocBlob(cipher);
        var entropy = AllocBlob(DpapiEntropy);
        var dataOut = default(DataBlob);
        try
        {
            if (!CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out dataOut))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            FreeHGlobalBlob(dataIn);
            FreeHGlobalBlob(entropy);
            if (dataOut.pbData != IntPtr.Zero) LocalFree(dataOut.pbData);
        }
    }

    private static DataBlob AllocBlob(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = ptr };
    }

    private static void FreeHGlobalBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
    }
}

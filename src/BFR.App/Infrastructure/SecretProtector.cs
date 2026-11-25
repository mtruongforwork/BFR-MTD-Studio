namespace BFR.App.Infrastructure;

using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Encrypts and decrypts local user secrets (such as optional AI OCR API keys)
/// using native Windows DPAPI (<c>crypt32.dll</c> CurrentUser scope) with zero external dependencies.
/// </summary>
public static class SecretProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Encrypts <paramref name="plainText"/> for the current Windows user and returns a Base64 string.
    /// Returns <see cref="string.Empty"/> when <paramref name="plainText"/> is null or empty.
    /// </summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();

        try
        {
            inBlob.cbData = plainBytes.Length;
            inBlob.pbData = Marshal.AllocHGlobal(plainBytes.Length);
            Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);

            if (!CryptProtectData(
                    ref inBlob,
                    szDataDescr: null,
                    pOptionalEntropy: IntPtr.Zero,
                    pvReserved: IntPtr.Zero,
                    pPromptStruct: IntPtr.Zero,
                    dwFlags: CRYPTPROTECT_UI_FORBIDDEN,
                    out outBlob))
            {
                return string.Empty;
            }

            if (outBlob.cbData <= 0 || outBlob.pbData == IntPtr.Zero)
                return string.Empty;

            var protectedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, protectedBytes, 0, outBlob.cbData);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            Array.Clear(plainBytes, 0, plainBytes.Length);
            if (inBlob.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }

    /// <summary>
    /// Decrypts a Base64 DPAPI blob produced by <see cref="Protect"/> for the current Windows user.
    /// Returns <see cref="string.Empty"/> safely if the input is null, empty, invalid Base64, or corrupted.
    /// </summary>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
            return string.Empty;

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedBase64.Trim());
        }
        catch
        {
            return string.Empty;
        }

        if (protectedBytes.Length == 0)
            return string.Empty;

        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();

        try
        {
            inBlob.cbData = protectedBytes.Length;
            inBlob.pbData = Marshal.AllocHGlobal(protectedBytes.Length);
            Marshal.Copy(protectedBytes, 0, inBlob.pbData, protectedBytes.Length);

            if (!CryptUnprotectData(
                    ref inBlob,
                    ppszDataDescr: IntPtr.Zero,
                    pOptionalEntropy: IntPtr.Zero,
                    pvReserved: IntPtr.Zero,
                    pPromptStruct: IntPtr.Zero,
                    dwFlags: CRYPTPROTECT_UI_FORBIDDEN,
                    out outBlob))
            {
                return string.Empty;
            }

            if (outBlob.cbData <= 0 || outBlob.pbData == IntPtr.Zero)
                return string.Empty;

            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }
}

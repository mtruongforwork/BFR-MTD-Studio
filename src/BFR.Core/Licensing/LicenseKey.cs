namespace BFR.Core.Licensing;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Mã kích hoạt Pro cho bản portable:
/// - Định dạng mới (v2.2+): <c>BFR2-&lt;base64url(payload)&gt;.&lt;base64url(ECDSA-P256-SHA256)&gt;</c>.
///   Chỉ nhúng khoá công khai trong <c>BFR.Core.dll</c>; khoá bí mật nằm ngoài ứng dụng.
/// - Định dạng cũ (tương thích ngược): <c>BFR-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</c> (checksum FNV-1a).
/// </summary>
public static class LicenseKey
{
    /// <summary>Bảng chữ cái dùng cho mã định dạng cũ (loại bỏ I/O/0/1 để tránh nhầm lẫn).</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Số nhóm payload (mỗi nhóm 5 ký tự) cho định dạng cũ.</summary>
    public const int PayloadGroups = 4;

    public const int GroupLength = 5;
    public const string Prefix = "BFR";
    public const string SignedPrefix = "BFR2-";

    // Khoá công khai ECDSA P-256 (SubjectPublicKeyInfo DER). KHÔNG có khoá bí mật trong BFR.Core.
    private static readonly byte[] EmbeddedPublicKeySpki = Convert.FromBase64String(
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEzyGvNC3sdby7+QK65CpqHIrdTbcJ6tyEIT9v+Fg7xx4UIhze1iZjifUfaL5gXqopJqfD7gf908N0we0gYbNp9A==");

    /// <summary>Lý do mã bị từ chối (dùng làm khoá chuỗi thông báo trong UI).</summary>
    public enum KeyError
    {
        None = 0,
        Empty,
        BadFormat,
        BadChecksum
    }

    /// <summary>Chuẩn hoá: bỏ khoảng trắng; giữ nguyên base64url cho mã BFR2-, chuẩn hoá hoa/gạch ngang cho mã cũ.</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        var trimmed = key.Trim();
        if (trimmed.StartsWith(SignedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return SignedPrefix + trimmed[SignedPrefix.Length..].Replace(" ", string.Empty);
        }

        var raw = trimmed.Replace("-", string.Empty)
            .Replace(" ", string.Empty)
            .ToUpperInvariant();

        if (raw.StartsWith(Prefix, StringComparison.Ordinal))
        {
            raw = raw[Prefix.Length..];
        }

        var builder = new StringBuilder(Prefix.Length + (PayloadGroups + 1) * (GroupLength + 1));
        builder.Append(Prefix);

        for (int i = 0; i < raw.Length; i++)
        {
            if (i % GroupLength == 0) builder.Append('-');
            builder.Append(raw[i]);
        }

        return builder.ToString();
    }

    /// <summary>Kiểm tra định dạng và chữ ký (BFR2-) hoặc checksum tương thích ngược (BFR-).</summary>
    public static KeyError Validate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return KeyError.Empty;

        var trimmed = key.Trim();
        if (trimmed.StartsWith(SignedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(EmbeddedPublicKeySpki, out _);
            return ValidateSigned(trimmed, ecdsa);
        }

        return ValidateLegacy(trimmed);
    }

    internal static KeyError ValidateSigned(string? key, ECDsa publicKey)
    {
        if (string.IsNullOrWhiteSpace(key)) return KeyError.Empty;

        var normalized = Normalize(key);
        if (!normalized.StartsWith(SignedPrefix, StringComparison.Ordinal)) return KeyError.BadFormat;

        var body = normalized[SignedPrefix.Length..];
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot == body.Length - 1 || body.IndexOf('.', dot + 1) >= 0)
            return KeyError.BadFormat;

        var payloadPart = body[..dot];
        var sigPart = body[(dot + 1)..];

        if (!TryDecodeBase64Url(payloadPart, out var payloadBytes) || payloadBytes.Length == 0 || payloadBytes.Length > 256)
            return KeyError.BadFormat;

        if (!TryDecodeBase64Url(sigPart, out var sigBytes) || sigBytes.Length == 0 || sigBytes.Length > 128)
            return KeyError.BadFormat;

        try
        {
            return publicKey.VerifyData(payloadBytes, sigBytes, HashAlgorithmName.SHA256)
                ? KeyError.None
                : KeyError.BadChecksum;
        }
        catch (CryptographicException)
        {
            return KeyError.BadChecksum;
        }
    }

    private static KeyError ValidateLegacy(string key)
    {
        var groups = Normalize(key).Split('-', StringSplitOptions.RemoveEmptyEntries);

        // BFR + 4 nhóm payload + 1 nhóm checksum.
        if (groups.Length != PayloadGroups + 2) return KeyError.BadFormat;
        if (!string.Equals(groups[0], Prefix, StringComparison.Ordinal)) return KeyError.BadFormat;

        foreach (var group in groups.Skip(1))
        {
            if (group.Length != GroupLength) return KeyError.BadFormat;
            if (group.Any(c => Alphabet.IndexOf(c) < 0)) return KeyError.BadFormat;
        }

        var payload = string.Concat(groups.Skip(1).Take(PayloadGroups));
        var checksum = groups[^1];
        return string.Equals(Checksum(payload), checksum, StringComparison.Ordinal)
            ? KeyError.None
            : KeyError.BadChecksum;
    }

    public static bool IsValid(string? key) => Validate(key) == KeyError.None;

    /// <summary>
    /// Nội bộ cho kiểm thử tương thích ngược: sinh mã định dạng cũ từ seed.
    /// Đã gỡ khỏi API công khai của BFR.Core.dll (SEC-2).
    /// </summary>
    internal static string Generate(string seed)
    {
        var payload = new StringBuilder();
        var bytes = Encoding.UTF8.GetBytes(seed ?? string.Empty);

        for (int i = 0; i < PayloadGroups * GroupLength; i++)
        {
            var index = (bytes.Length == 0 ? 0 : bytes[i % bytes.Length]) + i * 7;
            payload.Append(Alphabet[Math.Abs(index) % Alphabet.Length]);
        }

        var text = payload.ToString();
        return $"{Prefix}-{Chunk(text, 0)}-{Chunk(text, 1)}-{Chunk(text, 2)}-{Chunk(text, 3)}-{Checksum(text)}";
    }

    internal static string EncodeBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeBase64Url(string input, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(input)) return false;

        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: return false;
        }

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Chunk(string value, int index) =>
        value.Substring(index * GroupLength, GroupLength);

    private static string Checksum(string payload)
    {
        uint hash = 2166136261u;
        foreach (var c in payload)
        {
            hash = (hash ^ (uint)Alphabet.IndexOf(c)) * 16777619u;
        }

        var builder = new StringBuilder(GroupLength);
        for (int i = 0; i < GroupLength; i++)
        {
            builder.Insert(0, Alphabet[(int)(hash % Alphabet.Length)]);
            hash /= (uint)Alphabet.Length;
        }

        return builder.ToString();
    }

    /// <summary>Định dạng hiển thị cho người dùng (không phụ thuộc định dạng nhập).</summary>
    public static string ForDisplay(string? key) => Normalize(key);
}

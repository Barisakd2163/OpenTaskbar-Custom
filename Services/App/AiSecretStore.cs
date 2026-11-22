using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace OpenTaskbar.Services;

/// <summary>
/// Per-user Windows DPAPI secret store for AI provider API keys. Secrets live as opaque encrypted blobs
/// under <c>%LOCALAPPDATA%\OpenTaskbar\secrets\&lt;key&gt;.bin</c> and never enter <c>settings.json</c> or backups.
/// </summary>
internal static class AiSecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] EntropyBytes = "OpenTaskbar.AiSecretStore.v1"u8.ToArray();

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "secrets");

    internal static string Root { get; set; } = DefaultRoot;

    internal static string SecretPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Secret key is required.", nameof(key));

        var trimmed = key.Trim();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }
        return Path.Combine(Root, builder + ".bin");
    }

    public static bool HasSecret(string key) => Get(key) is not null;

    internal static DateTime GetLastWriteTimeUtc(string key)
    {
        try
        {
            var path = SecretPath(key);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SecurityException)
        {
            return DateTime.MinValue;
        }
    }

    public static string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        try
        {
            var path = SecretPath(key);
            if (!File.Exists(path)) return null;
            var encrypted = File.ReadAllBytes(path);
            if (encrypted.Length is 0 or > (1 << 16)) return null;
            var decrypted = Unprotect(encrypted);
            if (decrypted is null || decrypted.Length == 0) return null;
            var text = Encoding.UTF8.GetString(decrypted).Trim();
            Array.Clear(decrypted, 0, decrypted.Length);
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SecurityException or Win32Exception)
        {
            return null;
        }
    }

    public static void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Secret key is required.", nameof(key));

        var path = SecretPath(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value.Trim());
        try
        {
            var encrypted = Protect(plainBytes);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var tmpPath = path + ".tmp";
            File.WriteAllBytes(tmpPath, encrypted);
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            Array.Clear(plainBytes, 0, plainBytes.Length);
        }
    }

    private static byte[] Protect(byte[] plaintext)
    {
        var inHandle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(EntropyBytes, GCHandleType.Pinned);
        DataBlob dataOut = default;
        try
        {
            var dataIn = new DataBlob
            {
                cbData = plaintext.Length,
                pbData = inHandle.AddrOfPinnedObject()
            };
            var entropy = new DataBlob
            {
                cbData = EntropyBytes.Length,
                pbData = entropyHandle.AddrOfPinnedObject()
            };
            if (!CryptProtectData(ref dataIn, null, ref entropy, 0, 0, CryptProtectUiForbidden, out dataOut)
                || dataOut.pbData == 0 || dataOut.cbData <= 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (dataOut.pbData != 0) LocalFree(dataOut.pbData);
            if (entropyHandle.IsAllocated) entropyHandle.Free();
            if (inHandle.IsAllocated) inHandle.Free();
        }
    }

    private static byte[]? Unprotect(byte[] ciphertext)
    {
        var inHandle = GCHandle.Alloc(ciphertext, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(EntropyBytes, GCHandleType.Pinned);
        DataBlob dataOut = default;
        try
        {
            var dataIn = new DataBlob
            {
                cbData = ciphertext.Length,
                pbData = inHandle.AddrOfPinnedObject()
            };
            var entropy = new DataBlob
            {
                cbData = EntropyBytes.Length,
                pbData = entropyHandle.AddrOfPinnedObject()
            };
            if (!CryptUnprotectData(ref dataIn, 0, ref entropy, 0, 0, CryptProtectUiForbidden, out dataOut)
                || dataOut.pbData == 0 || dataOut.cbData < 0)
            {
                return null;
            }
            var result = new byte[dataOut.cbData];
            if (result.Length > 0)
                Marshal.Copy(dataOut.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (dataOut.pbData != 0) LocalFree(dataOut.pbData);
            if (entropyHandle.IsAllocated) entropyHandle.Free();
            if (inHandle.IsAllocated) inHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public nint pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        nint ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint hMem);
}

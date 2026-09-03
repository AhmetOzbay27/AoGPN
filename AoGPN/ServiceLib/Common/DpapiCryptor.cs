using System.ComponentModel;
using System.Text;

namespace ServiceLib.Common;

// ─────────────────────────────────────────────────────────────────────────
// DpapiCryptor — WireGuard istemci özel anahtarları için disk şifreleme.
//
// Windows: crypt32 CryptProtectData/CryptUnprotectData (Geçerli Kullanıcı
// kapsamı, UI istemi kapalı). Şifreli blob diske base64 olarak yazılır.
// Yalnızca o Windows kullanıcısı (aynı makine) çözebilir → anahtar, veri
// tabanı dosyasını kopyalayan başka bir kullanıcı/makineye sızmaz.
//
// Windows dışı (Linux/macOS debug GUI): DPAPI yoktur. Veri DÜZ base64 ile
// saklanır ve günlüğe bir uyarı yazılır — GPN akışı Windows'a özgüdür, bu
// yol yalnızca uygulamanın diğer platformlarda derlenmesini/çalışmasını
// bozmamak için bir geri düşüştür.
// ─────────────────────────────────────────────────────────────────────────

public static class DpapiCryptor
{
    private const string Tag = "Dpapi";

    private const uint CrptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Düz metni şifrele. Windows'ta DPAPI; başka platformda düz base64 (geri düşüş).
    /// Boş girdi boş döner.
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (plainText.IsNullOrEmpty())
        {
            return string.Empty;
        }

        if (!Utils.IsWindows())
        {
            Logging.SaveLog($"[{Tag}] DPAPI kullanılamıyor (Windows dışı) — private key düz base64 saklanıyor.");
            return Utils.Base64Encode(plainText);
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = Protect(plainBytes);
        return Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// Şifreli base64'i çöz. Windows'ta DPAPI; boş/çözülemez → boş döner.
    /// </summary>
    public static string Decrypt(string cipherBase64)
    {
        if (cipherBase64.IsNullOrEmpty())
        {
            return string.Empty;
        }

        if (!Utils.IsWindows())
        {
            return Utils.Base64Decode(cipherBase64);
        }

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(cipherBase64);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Şifreli anahtar kaydı bozuk (base64): {ex.Message}");
            return string.Empty;
        }

        var plainBytes = Unprotect(cipherBytes);
        return plainBytes is null ? string.Empty : Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] Protect(byte[] data)
    {
        var input = default(DATA_BLOB);
        var output = default(DATA_BLOB);
        try
        {
            input.cbData = (uint)data.Length;
            input.pbData = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, input.pbData, data.Length);

            if (!CryptProtectData(ref input, "AoGPN private key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CrptProtectUiForbidden, ref output))
            {
                throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "CryptProtectData başarısız");
            }

            var result = new byte[output.cbData];
            System.Runtime.InteropServices.Marshal.Copy(output.pbData, result, 0, (int)output.cbData);
            return result;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(input.pbData);
            }

            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        var input = default(DATA_BLOB);
        var output = default(DATA_BLOB);
        try
        {
            input.cbData = (uint)data.Length;
            input.pbData = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, input.pbData, data.Length);

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CrptProtectUiForbidden, ref output))
            {
                Logging.SaveLog($"[{Tag}] CryptUnprotectData başarısız: anahtar kaydı bu kullanıcı/makine için geçersiz.");
                return null;
            }

            var result = new byte[output.cbData];
            System.Runtime.InteropServices.Marshal.Copy(output.pbData, result, 0, (int)output.cbData);
            return result;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(input.pbData);
            }

            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }
}
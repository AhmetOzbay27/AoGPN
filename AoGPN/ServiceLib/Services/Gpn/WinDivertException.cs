namespace ServiceLib.Services;

/// <summary>
/// WinDivert.caller'dan yaşayan bir hata. <see cref="NativeError"/> Win32 hata
/// kodunu taşır (ör. sürücü kurulu değilse 2/ERROR_FILE_NOT_FOUND).
/// </summary>
public sealed class WinDivertException : Exception
{
    public WinDivertException(string message, int nativeError)
        : base($"{message} (Win32 hata kodu: {nativeError})")
    {
        NativeError = nativeError;
    }

    public WinDivertException(string message, int nativeError, Exception inner)
        : base($"{message} (Win32 hata kodu: {nativeError})", inner)
    {
        NativeError = nativeError;
    }

    /// <summary>Son başarısız WinDivert çağrısının Win32 hata kodu.</summary>
    public int NativeError { get; }
}
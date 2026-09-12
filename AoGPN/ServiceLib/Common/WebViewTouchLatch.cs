namespace ServiceLib.Common;

/// <summary>
/// WebView2 denetleyicisi kapandığında <c>CoreWebView2</c> üyelerine dokunmayı
/// KALICI olarak durduran kilit.
///
/// Neden gerekli: denetleyici kapandıktan sonra <c>WebView.CoreWebView2</c>
/// nesnesi <c>null</c> OLMAZ — yalnızca üyeleri
/// <c>"CoreWebView2 members cannot be accessed after the WebView2 controller was
/// closed."</c> iletisiyle <see cref="InvalidOperationException"/> atar. Bu yüzden
/// "null değilse kullanılabilir" biçimindeki korumalar sessizce yanlış kalır ve
/// 2 saniyelik yaşam döngüsü poll'u gibi sıcak bir döngü her turda aynı hatayı
/// yeniden üretir. Bildirilen hata ayıklama günlüğünde bu, 44 ayrı blok hâlinde
/// 80 "CoreWebView2 members cannot be accessed..." satırıydı.
///
/// Kilit tek yönlüdür: bir kez yıkım hatası gözlenirse bir daha açılmaz. Denetleyici
/// geri gelirse uygulama zaten baştan kurar (yeniden örnekleme), dolayısıyla
/// kilidi açmak için bir yol gerekmez.
/// </summary>
public sealed class WebViewTouchLatch
{
    private int _closed;

    /// <summary>Dokunmaya izin var mı? (henüz yıkım görülmediyse açık)</summary>
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>
    /// Yıkım hatası gözlendi: kilidi kalıcı olarak kapatır.
    /// </summary>
    /// <returns>Bu çağrı kilidi kapatan İLK çağrıysa <c>true</c> — yani hata ilk kez mi görüldü?</returns>
    public bool CloseOnDestruction() => Interlocked.Exchange(ref _closed, 1) == 0;

    /// <summary>
    /// Dokunmadan önceki birleşik kapı: kilit açık, kapanış başlamamış, denetleyici
    /// hazır ve nesne var olmalı. Çağrı yerlerinin tek satırda aynı soruyu sormasını
    /// sağlar (kural tek yerde yaşar, dağılmaz).
    /// </summary>
    public bool ShouldTouch(bool isClosing, bool isHostReady, bool hasController)
        => IsOpen && !isClosing && isHostReady && hasController;
}

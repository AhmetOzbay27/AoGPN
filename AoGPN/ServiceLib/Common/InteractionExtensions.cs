namespace ServiceLib.Common;

/// <summary>
/// Bildirim amaçlı (fire-and-forget) <see cref="Interaction{TInput,TOutput}"/>
/// çağrılarını GÜVENLİ hâle getirir.
///
/// Sorun: ReactiveUI'da <c>Interaction</c> bir istek/yanıt sözleşmesidir ve
/// kayıtlı bir handler YOKSA <c>Handle</c> bilerek
/// <c>UnhandledInteractionException</c> atar. Oysa uygulamada bu etkileşimlerin
/// bir kısmı bildirimdir ("başlık çubuğu ikonunu tazele", "pencereyi gizle");
/// görünüm yüklenmediyse (WebView2 yerleşiminde legacy görünümler aktive
/// olmuyor) dinleyici olmaması NORMALDİR. Bu durumda her çağrıda istisna atmak
/// iki maliyet doğurur:
///
///   1. Uygulama hatası gibi görünür: Visual Studio, yakalanan istisnalar için
///      bile "ilk şans istisnası" satırı yazar. Bildirilen 22 MB'lık hata
///      ayıklama günlüğünde bu türden 33.933 satır vardı ve dosyayı okunamaz
///      hâle getiriyordu.
///   2. Sıcak döngülerde (2 sn'lik yaşam döngüsü poll'u) her turda istisna
///      nesnesi + yığın izi üretir; bu, atılan/yakalanan istisna başına gerçek
///      bir ayırma ve yığın yürüyüşü maliyetidir.
///
/// Çözüm: çağrıdan ÖNCE "dinleyici var mı?" sorusunu sorup yoksa hiç
/// çağırmamak. ReactiveUI 23.2'de bunun için public API yoktur (yalnızca
/// <c>RegisterHandler</c> ve <c>Handle</c> açıktır; handler listesi özel
/// <c>_handlers</c> alanındadır), bu yüzden alan bir kez yansımayla çözülüp
/// önbelleğe alınır. Yansıma başarısız olursa (ReactiveUI alanı yeniden
/// adlandırırsa) <see cref="HasHandler{TInput,TOutput}"/> ihtiyatlı biçimde
/// <c>true</c> döner: eski davranış korunur, sessizce hiçbir şey bozulmaz —
/// yalnızca gürültü geri gelir. Bu bağımlılık
/// <c>InteractionExtensionsTests</c> ile kilitlenmiştir: API değişirse test
/// kırılır ve haber verir.
/// </summary>
public static class InteractionExtensions
{
    private const string HandlersFieldName = "_handlers";

    private static readonly ConcurrentDictionary<Type, FieldInfo?> FieldCache = new();

    /// <summary>
    /// Etkileşime kayıtlı en az bir handler var mı? Yanıtlanamayacak bir çağrıyı
    /// hiç yapmamak için kullanılır.
    /// </summary>
    /// <remarks>
    /// Algılama başarısız olursa <c>true</c> döner (ihtiyatlı taraf): çağıran
    /// normal <c>Handle</c> yolunu izler ve davranış değişmez.
    /// </remarks>
    public static bool HasHandler<TInput, TOutput>(this Interaction<TInput, TOutput> interaction)
    {
        if (interaction is null)
        {
            return false;
        }

        try
        {
            var field = FieldCache.GetOrAdd(interaction.GetType(), static type => FindHandlersField(type));
            if (field?.GetValue(interaction) is System.Collections.ICollection handlers)
            {
                return handlers.Count > 0;
            }
        }
        catch (Exception)
        {
            // Yansıma kısıtlanmış olabilir (AOT/trim): ihtiyatlı tarafa düş.
        }

        return true;
    }

    /// <summary>
    /// Bildirim amaçlı çağrı: kayıtlı handler yoksa hiçbir şey yapmaz ve
    /// <c>false</c> döner. Var olan handler'ı olan bir etkileşimde davranış
    /// <see cref="Interaction{TInput,TOutput}.Handle"/> ile birebir aynıdır.
    /// </summary>
    /// <returns>Handler bulunup çalıştırıldıysa <c>true</c>.</returns>
    public static async Task<bool> TryHandleAsync<TInput, TOutput>(
        this Interaction<TInput, TOutput> interaction,
        TInput input)
    {
        if (!interaction.HasHandler())
        {
            return false;
        }

        await interaction.Handle(input);
        return true;
    }

    /// <summary>
    /// Yanıtı gereken bildirim çağrıları için: handler yoksa
    /// <c>(false, default)</c> döner — istisna ATMAZ.
    ///
    /// Ayrı bir AD taşır (aşırı yükleme değil): aksi halde iki argümanlı
    /// çağrılar "hangi overload?" sorusuna isteğe bağlı parametre kurallarıyla
    /// yanıt verirdi ve hangi anahtar sözcüğün seçildiği koda bakınca
    /// anlaşılmazdı.
    /// </summary>
    /// <example>
    /// <code>
    /// var (handled, password) = await PasswordInputInteraction.TryHandleResultAsync(Unit.Default);
    /// if (!handled) { /* istem yok: geçişi iptal et */ }
    /// </code>
    /// </example>
    public static async Task<(bool Handled, TOutput? Output)> TryHandleResultAsync<TInput, TOutput>(
        this Interaction<TInput, TOutput> interaction,
        TInput input)
    {
        if (!interaction.HasHandler())
        {
            return (false, default);
        }

        var output = await interaction.Handle(input);
        return (true, output);
    }

    private static FieldInfo? FindHandlersField(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(HandlersFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }
}

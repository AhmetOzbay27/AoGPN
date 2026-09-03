using Xunit;

namespace ServiceLib.Tests;

/// <summary>
/// WireGuardServerCatalog ve GpnServerEditViewModel, uygulamanın tekil SQLite
/// veritabanını (SQLiteHelper.Instance) kullanır. xUnit bunları paralel koşarsa
/// bir sınıf diğerinin satırlarını silebilir; aynı collection'a koymak bunu engeller.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCatalogCollection
{
    public const string Name = "SqliteCatalog";
}

/// <summary>
/// GpnSessionLog küresel statik bir dosya yolunu mutasyona uğratır
/// (SetPathForTesting). Diğer sınıflar DiagLog aynası üzerinden aynı statik
/// duruma paralel yazarsa yol yarışı oluşur — bu koleksiyon testleri paralel
/// fazdan SONRA sıralı çalıştırır.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpnSessionLogCollection
{
    public const string Name = "GpnSessionLogSerial";
}

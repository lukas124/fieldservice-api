namespace FieldService.Api.Infrastructure;

/// <summary>Sekcja <c>Auth</c>: logowanie i tokeny.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>PIN akceptowany dla każdego poprawnego e-maila.</summary>
    public string Pin { get; set; } = "1234";

    /// <summary>Czas życia access tokenu w sekundach (domyślnie 10 min; np. 60 do demo odświeżania po 401).</summary>
    public int AccessTokenLifetimeSeconds { get; set; } = 600;

    /// <summary>Czas życia refresh tokenu w dniach.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    /// <summary>Klucz podpisu JWT (min. 32 znaki). Pusty = wygenerowany i zapisany w katalogu danych.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "FieldService.Api";
    public string Audience { get; set; } = "FieldService.App";
}

/// <summary>Sekcja <c>Storage</c>: katalog danych na dysku.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Katalog danych (klucz podpisu JWT). Ścieżka względna liczona od katalogu aplikacji.</summary>
    public string DataDirectory { get; set; } = "data";
}

/// <summary>Sekcja <c>Demo</c>: dane demonstracyjne tworzone przy logowaniu.</summary>
public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Strefa czasowa „dnia pracy” technika (wizyty na dziś).</summary>
    public string TimeZone { get; set; } = "Europe/Warsaw";

    /// <summary>Liczba wizyt w historii nowego technika.</summary>
    public int HistoryVisits { get; set; } = 20;

    /// <summary>Konto z dużą historią (Blok 14: płynna lista, cache miniatur).</summary>
    public string PerformanceEmail { get; set; } = "wydajnosc@szkolenie.pl";

    /// <summary>Liczba wizyt w historii konta <see cref="PerformanceEmail"/>.</summary>
    public int PerformanceHistoryVisits { get; set; } = 2000;

    /// <summary>Konta brygady: wspólna wizyta na dziś (konflikt dwóch telefonów). Lista tylko z appsettings.json –
    /// binder konfiguracji dopisuje elementy do tablicy, więc wartość domyślna tutaj podwoiłaby e-maile.</summary>
    public string[] BrigadeEmails { get; set; } = [];

    /// <summary>Opóźnienie list (katalog części, wizyty na dziś) w ms — do pomiarów cache; 0 = bez opóźnienia.</summary>
    public int ListDelayMilliseconds { get; set; }
}

/// <summary>Sekcja <c>Inventory</c>: magazyn części.</summary>
public sealed class InventoryOptions
{
    public const string Section = "Inventory";

    /// <summary>Po ilu minutach rezerwacja wygasa (sztuki wracają do puli).</summary>
    public int ReservationMinutes { get; set; } = 30;
}

/// <summary>Sekcja <c>Admin</c>.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>Klucz dla <c>POST /api/admin/reset</c> (nagłówek <c>X-Admin-Key</c>). Pusty = reset wyłączony.</summary>
    public string? Key { get; set; }
}

/// <summary>Sekcja <c>Dispatcher</c>.</summary>
public sealed class DispatcherOptions
{
    public const string Section = "Dispatcher";

    /// <summary>Opcjonalny klucz wymagany w nagłówku <c>X-Dispatcher-Key</c>. Pusty = endpointy dyspozytora otwarte.</summary>
    public string? Key { get; set; }
}

/// <summary>Sekcja <c>RateLimiting</c> (limity na adres IP, okno 1 minuta).</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    public bool Enabled { get; set; } = true;
    public int PermitsPerMinute { get; set; } = 300;
    public int AuthPermitsPerMinute { get; set; } = 30;
    public int DispatcherPermitsPerMinute { get; set; } = 60;
}

/// <summary>Sekcja <c>Hosting</c>: adres nasłuchu (gdy nie podano <c>--urls</c>/<c>ASPNETCORE_URLS</c>).</summary>
public sealed class HostingOptions
{
    public const string Section = "Hosting";

    public int Port { get; set; } = 5090;

    /// <summary>true = nasłuch na 0.0.0.0 (sieć sali), false = tylko localhost.</summary>
    public bool ListenOnAllInterfaces { get; set; }
}

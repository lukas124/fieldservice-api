namespace FieldService.Api.Domain;

public enum WorkOrderStatus { Open, Closed }

/// <summary>Zlecenie — sprawa klienta (model Zlecenia). Zamyka się, gdy wizyta zakończy się wynikiem Fixed.</summary>
public sealed class WorkOrder
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string FaultDescription { get; set; } = "";
    public DateTimeOffset SlaDueAt { get; set; }
    public WorkOrderStatus Status { get; set; }
    public List<Visit> Visits { get; set; } = [];
}

/// <summary>Technik = jeden adres e-mail (małe litery).</summary>
public sealed class Technician
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>Część w aucie technika (model odczytu z modelu Magazyn).</summary>
public sealed class VanStockItem
{
    public string TechnicianEmail { get; set; } = "";
    public string PartId { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>Refresh token zapisany jako skrót SHA-256 (sam token zna tylko aplikacja).</summary>
public sealed class RefreshToken
{
    public string Hash { get; set; } = "";
    public string TechnicianEmail { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Zapamiętana odpowiedź na komendę z nagłówkiem <c>Idempotency-Key</c> (idempotentny odbiorca):
/// powtórka tego samego żądania dostaje tę samą odpowiedź, bez drugiego wykonania komendy.
/// </summary>
public sealed class IdempotencyRecord
{
    public string TechnicianEmail { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>Skrót metody, ścieżki i treści — ten sam klucz z inną treścią to błąd klienta (422).</summary>
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string ResponseJson { get; set; } = "";
    public string? ETag { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

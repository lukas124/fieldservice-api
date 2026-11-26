using FieldService.Api.Domain;

namespace FieldService.Api.Contracts;

// ---------- auth ----------

public sealed class LoginRequest
{
    /// <example>jan.kowalski@firma.pl</example>
    public string? Email { get; set; }

    /// <example>1234</example>
    public string? Pin { get; set; }
}

public sealed class RefreshRequest
{
    public string? RefreshToken { get; set; }
}

public sealed class TokenResponse
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    /// <summary>Ważność access tokenu w sekundach.</summary>
    public required int ExpiresIn { get; init; }
    public required string TechnicianName { get; init; }
}

// ---------- wizyty ----------

public sealed class VisitSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid WorkOrderId { get; init; }
    public required string WorkOrderNumber { get; init; }
    public required VisitStatus Status { get; init; }
    public required DateTimeOffset PlannedStart { get; init; }
    public required DateTimeOffset PlannedEnd { get; init; }
    public required string CustomerName { get; init; }
    public required string Address { get; init; }
    public required string DeviceName { get; init; }
    public required string FaultDescription { get; init; }
    /// <summary>Wizyta brygady (dwóch techników).</summary>
    public required bool IsBrigade { get; init; }
    public required string ThumbnailUrl { get; init; }
    /// <summary>Wersja wizyty — wysyłana w <c>If-Match</c> przy komendach.</summary>
    public required int Version { get; init; }
    /// <summary>Szacowany przyjazd technika: zaplanowana – początek okna, rozpoczęta – czas rozpoczęcia, zakończona
    /// i anulowana – brak. Nie zależy od chwili zapytania (ETag listy się nie zmienia).</summary>
    public DateTimeOffset? EstimatedArrival { get; init; }
}

public sealed class CustomerDto
{
    public required string Name { get; init; }
    public required string Phone { get; init; }
    public required string Street { get; init; }
    public required string City { get; init; }
}

public sealed class ActivityDto
{
    public required string Code { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed class UsedPartDto
{
    public required string PartId { get; init; }
    public required string PartName { get; init; }
    public required int Quantity { get; init; }
}

public sealed class SignatureDto
{
    public required string SignerName { get; init; }
    public required DateTimeOffset SignedAt { get; init; }
    /// <summary>Wersja zakresu prac, pod którą klient się podpisał.</summary>
    public required int ScopeVersion { get; init; }
    /// <summary>false = zakres zmienił się po podpisie (podpis nieważny, potrzebny nowy).</summary>
    public required bool IsValid { get; init; }
}

public sealed class VisitDetailsDto
{
    public required Guid Id { get; init; }
    public required Guid WorkOrderId { get; init; }
    public required string WorkOrderNumber { get; init; }
    public required VisitStatus Status { get; init; }
    public required DateTimeOffset PlannedStart { get; init; }
    public required DateTimeOffset PlannedEnd { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required CustomerDto Customer { get; init; }
    public required string DeviceName { get; init; }
    public required string FaultDescription { get; init; }
    public required DateTimeOffset SlaDueAt { get; init; }
    public required IReadOnlyList<string> TechnicianNames { get; init; }
    /// <summary>E-maile przypisanych techników (brygada = dwa) – aplikacja sprawdza regułę „rozpoczynam tylko swoją wizytę”.</summary>
    public required IReadOnlyList<string> TechnicianEmails { get; init; }
    public required IReadOnlyList<ActivityDto> Activities { get; init; }
    public required IReadOnlyList<UsedPartDto> Parts { get; init; }
    public required int ScopeVersion { get; init; }
    public SignatureDto? Signature { get; init; }
    public VisitOutcomeType? Outcome { get; init; }
    public string? OutcomeReason { get; init; }
    public string? CancelReason { get; init; }
    public required string ThumbnailUrl { get; init; }
    public required string ImageUrl { get; init; }
    /// <summary>Wersja wizyty — wysyłana w <c>If-Match</c> przy komendach (to samo co nagłówek <c>ETag</c>).</summary>
    public required int Version { get; init; }
}

public sealed class VisitHistoryItemDto
{
    public required Guid Id { get; init; }
    public required string WorkOrderNumber { get; init; }
    public required VisitStatus Status { get; init; }
    public required DateTimeOffset PlannedStart { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string CustomerName { get; init; }
    public required string City { get; init; }
    public required string DeviceName { get; init; }
    public VisitOutcomeType? Outcome { get; init; }
    public required string ThumbnailUrl { get; init; }
}

public sealed class PageDto<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
}

public sealed class StartVisitRequest
{
    /// <summary>Kiedy technik rozpoczął wizytę (czas z telefonu; brak = teraz).</summary>
    public DateTimeOffset? StartedAt { get; set; }
}

public sealed class RecordActivityRequest
{
    /// <example>DIAG</example>
    public string Code { get; set; } = "";
    /// <example>Diagnostyka układu chłodniczego</example>
    public string Description { get; set; } = "";
    public DateTimeOffset? RecordedAt { get; set; }
}

public sealed class UsePartRequest
{
    /// <example>FILTR-SIATKOWY-22</example>
    public string PartId { get; set; } = "";
    /// <example>1</example>
    public int Quantity { get; set; }
}

public sealed class SignVisitRequest
{
    /// <example>Anna Wiśniewska</example>
    public string SignerName { get; set; } = "";
    public DateTimeOffset? SignedAt { get; set; }
}

public sealed class CompleteVisitRequest
{
    public VisitOutcomeType Outcome { get; set; }
    /// <summary>Wymagany dla NeedsFollowUp i NotFixed.</summary>
    public string? Reason { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

// ---------- zlecenia ----------

public sealed class WorkOrderSummaryDto
{
    public required Guid Id { get; init; }
    public required string Number { get; init; }
    public required WorkOrderStatus Status { get; init; }
    public required string CustomerName { get; init; }
    public required string Address { get; init; }
    public required string DeviceName { get; init; }
    public required string FaultDescription { get; init; }
    public required DateTimeOffset SlaDueAt { get; init; }
    public required int VisitCount { get; init; }
}

// ---------- części i magazyn ----------

public sealed class PartDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required decimal UnitPrice { get; init; }
    public required string ImageUrl { get; init; }
    public required string ThumbnailUrl { get; init; }
}

public sealed class WarehouseDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class StockLevelDto
{
    public required string PartId { get; init; }
    public required string WarehouseId { get; init; }
    public required string WarehouseName { get; init; }
    public required int OnHand { get; init; }
    public required int Reserved { get; init; }
    public required int Available { get; init; }
}

public sealed class VanStockItemDto
{
    public required string PartId { get; init; }
    public required string PartName { get; init; }
    public required int Quantity { get; init; }
    public required string ThumbnailUrl { get; init; }
}

public sealed class ReservePartRequest
{
    /// <example>KOMPRESOR-R32</example>
    public string PartId { get; set; } = "";
    /// <example>MAG-01</example>
    public string WarehouseId { get; set; } = "";
    public Guid VisitId { get; set; }
    /// <example>1</example>
    public int Quantity { get; set; } = 1;
}

public sealed class ReservationDto
{
    public required Guid Id { get; init; }
    public required string PartId { get; init; }
    public required string WarehouseId { get; init; }
    public required Guid VisitId { get; init; }
    public required int Quantity { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

// ---------- dyspozytor ----------

public sealed class CancelVisitRequest
{
    /// <example>Klient odwołał wizytę telefonicznie</example>
    public string Reason { get; set; } = "";
}

public sealed class RescheduleVisitRequest
{
    public DateTimeOffset PlannedStart { get; set; }
    public DateTimeOffset PlannedEnd { get; set; }
}

public sealed class CreateVisitRequest
{
    /// <example>jan.kowalski@firma.pl</example>
    public string TechnicianEmail { get; set; } = "";
    /// <summary>Drugi technik brygady (opcjonalnie).</summary>
    public string? SecondTechnicianEmail { get; set; }
    /// <summary>Brak = za godzinę, okno 90 min.</summary>
    public DateTimeOffset? PlannedStart { get; set; }
    /// <example>Pompa ciepła nie grzeje, błąd F.022</example>
    public string? FaultDescription { get; set; }
}

public sealed class SetStockRequest
{
    /// <example>5</example>
    public int OnHand { get; set; }
}

public sealed class DispatcherVisitDto
{
    public required Guid Id { get; init; }
    public required string WorkOrderNumber { get; init; }
    public required VisitStatus Status { get; init; }
    public required DateTimeOffset PlannedStart { get; init; }
    public required IReadOnlyList<string> TechnicianEmails { get; init; }
    public required string CustomerName { get; init; }
    public required int Version { get; init; }
}

// ---------- admin i health ----------

public sealed class ResetResult
{
    public required int RemovedTechnicians { get; init; }
    public required int RemovedVisits { get; init; }
    public required string Message { get; init; }
}

public sealed class HealthDto
{
    public required string Status { get; init; }
    public required bool DatabaseConnected { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset ServerTime { get; init; }
    public required string PathBase { get; init; }
    public required int AccessTokenLifetimeSeconds { get; init; }
    /// <summary>Czy API wysyła telemetrię (OTEL_EXPORTER_OTLP_ENDPOINT).</summary>
    public required bool TelemetryExport { get; init; }
    public required string Environment { get; init; }
}

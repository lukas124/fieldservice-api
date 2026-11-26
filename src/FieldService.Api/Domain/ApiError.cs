namespace FieldService.Api.Domain;

/// <summary>Odmowa reguły biznesowej: status HTTP, stabilny kod (<c>Visit.VersionConflict</c>) i opis dla człowieka.</summary>
public sealed record ApiError(int Status, string Code, string Title, string Detail);

public static class VisitErrors
{
    public static ApiError NotFound(Guid id) =>
        new(404, "Visit.NotFound", "Nie ma takiej wizyty", $"Wizyta {id} nie istnieje albo nie jest przypisana do Ciebie.");

    public static readonly ApiError PreconditionRequired =
        new(428, "Visit.PreconditionRequired", "Brak wersji wizyty",
            "Komenda wymaga nagłówka If-Match (albo X-If-Match) z wersją wizyty, którą widzi aplikacja (np. If-Match: \"3\").");

    public static ApiError VersionConflict(int expected, int current) =>
        new(409, "Visit.VersionConflict", "Wizyta zmieniła się na serwerze",
            $"Aplikacja widziała wersję {expected}, a na serwerze jest wersja {current}. Pobierz wizytę i rozstrzygnij konflikt.");

    public static ApiError InvalidTransition(VisitStatus from, string action) =>
        new(409, "Visit.InvalidTransition", "Niedozwolona zmiana statusu", $"Nie można wykonać „{action}” dla wizyty w statusie {from}.");

    public static ApiError NotInProgress(VisitStatus status) =>
        new(409, "Visit.NotInProgress", "Wizyta nie jest w trakcie", $"Zakres prac zmienia się tylko w wizycie InProgress (teraz: {status}).");

    public static readonly ApiError AlreadyCompleted =
        new(409, "Visit.AlreadyCompleted", "Wizyta jest już zakończona", "Zakończonej wizyty nie można anulować ani zmienić.");

    public static readonly ApiError EmptyScope =
        new(422, "Visit.EmptyScope", "Pusty zakres prac", "Klient podpisuje zakres prac — dodaj co najmniej jedną czynność albo część.");

    public static readonly ApiError SignatureRequired =
        new(422, "Visit.SignatureRequired", "Brak ważnego podpisu",
            "Wynik Fixed wymaga podpisu klienta dla bieżącego zakresu prac (zmiana zakresu unieważnia podpis).");

    public static readonly ApiError ReasonRequired =
        new(422, "Visit.ReasonRequired", "Brak powodu", "Wynik NeedsFollowUp i NotFixed wymaga powodu.");

    public static ApiError PartNotInScope(string partId) =>
        new(422, "Visit.PartNotInScope", "Części nie ma w zakresie", $"Część {partId} nie jest w zakresie prac tej wizyty.");

    public static readonly ApiError InvalidQuantity =
        new(422, "Visit.InvalidQuantity", "Nieprawidłowa ilość", "Ilość musi być większa od zera.");

    public static ApiError Validation(string detail) =>
        new(422, "Visit.Validation", "Nieprawidłowe dane", detail);
}

public static class InventoryErrors
{
    public static ApiError PartNotFound(string partId) =>
        new(404, "Inventory.PartNotFound", "Nie ma takiej części", $"Część {partId} nie istnieje w katalogu.");

    public static ApiError StockNotFound(string partId, string warehouseId) =>
        new(404, "Inventory.StockNotFound", "Brak części w magazynie", $"Magazyn {warehouseId} nie prowadzi części {partId}.");

    public static ApiError OutOfStock(string partId, int available) =>
        new(409, "Inventory.OutOfStock", "Brak dostępnych sztuk",
            $"Dostępnych sztuk części {partId}: {available}. Ktoś mógł właśnie zarezerwować ostatnią.");

    public static readonly ApiError InvalidQuantity =
        new(422, "Inventory.InvalidQuantity", "Nieprawidłowa ilość", "Ilość musi być od 1 do 100.");

    public static ApiError ReservationNotFound(Guid id) =>
        new(404, "Inventory.ReservationNotFound", "Nie ma takiej rezerwacji", $"Rezerwacja {id} nie istnieje, wygasła albo nie jest Twoja.");
}

public static class IdempotencyErrors
{
    public static readonly ApiError KeyReused =
        new(422, "Idempotency.KeyReused", "Klucz użyty z inną treścią",
            "Ten Idempotency-Key był już użyty dla innego żądania. Każda nowa komenda dostaje nowy klucz.");

    public static readonly ApiError InProgress =
        new(409, "Idempotency.InProgress", "Żądanie jest właśnie przetwarzane", "To samo żądanie (ten sam klucz) jest zapisywane równolegle. Ponów za chwilę.");
}

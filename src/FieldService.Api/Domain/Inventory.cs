namespace FieldService.Api.Domain;

/// <summary>Część w katalogu (CRUD — poza agregatem <see cref="PartStock"/>). Id = kod części, np. <c>KOMPRESOR-R32</c>.</summary>
public sealed class Part
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal UnitPrice { get; set; }
}

public sealed class Warehouse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Reservation
{
    public Guid Id { get; set; }
    public Guid VisitId { get; set; }
    public string TechnicianEmail { get; set; } = "";
    public int Quantity { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Zapas części — agregat modelu Magazyn: jedna część w jednym magazynie (nie cały magazyn).
/// Reguła: suma aktywnych (niewygasłych) rezerwacji ≤ <see cref="OnHand"/>. Rezerwacje wygasają po czasie (TTL),
/// bez zadania w tle. <see cref="Version"/> chroni regułę przy równoczesnych rezerwacjach (ostatnia sztuka).
/// </summary>
public sealed class PartStock
{
    public string PartId { get; set; } = "";
    public string WarehouseId { get; set; } = "";
    public int OnHand { get; set; }
    public List<Reservation> Reservations { get; set; } = [];
    public int Version { get; set; }

    public int Reserved(DateTimeOffset now) => Reservations.Where(r => r.ExpiresAt > now).Sum(r => r.Quantity);

    public int Available(DateTimeOffset now) => Math.Max(0, OnHand - Reserved(now));

    public (Reservation? Reservation, ApiError? Error) Reserve(Guid visitId, string technicianEmail, int quantity, DateTimeOffset now, TimeSpan ttl)
    {
        if (quantity is < 1 or > 100) return (null, InventoryErrors.InvalidQuantity);
        Reservations.RemoveAll(r => r.ExpiresAt <= now);
        var available = Available(now);
        if (quantity > available) return (null, InventoryErrors.OutOfStock(PartId, available));

        var reservation = new Reservation
        {
            Id = Guid.NewGuid(), VisitId = visitId, TechnicianEmail = technicianEmail, Quantity = quantity, ExpiresAt = now + ttl
        };
        Reservations.Add(reservation);
        Version++;
        return (reservation, null);
    }

    public bool Release(Guid reservationId, string technicianEmail)
    {
        if (Reservations.RemoveAll(r => r.Id == reservationId && r.TechnicianEmail == technicianEmail) == 0) return false;
        Version++;
        return true;
    }

    /// <summary>Stan po inwentaryzacji albo dostawie (dyspozytor).</summary>
    public void SetOnHand(int onHand)
    {
        OnHand = Math.Max(0, onHand);
        Version++;
    }
}

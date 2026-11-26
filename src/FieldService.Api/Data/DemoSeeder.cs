using FieldService.Api.Auth;
using FieldService.Api.Domain;
using FieldService.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Data;

/// <summary>
/// Dane demonstracyjne. Katalog części, magazyny i zapasy są wspólne (przy starcie, gdy baza jest pusta).
/// Dzień pracy technika powstaje przy logowaniu, gdy technik nie ma jeszcze wizyt na dziś: 4 wizyty (pierwsza
/// rozpoczęta), zlecenia, auto technika i historia. Konta brygady dzielą jedną wizytę na dziś, a ostatnią sztukę
/// <see cref="ReservedPartId"/> w magazynie centralnym trzyma rezerwacja drugiego konta brygady (konflikt pewny).
/// Dane są fikcyjne (klienci, adresy, telefony).
/// </summary>
public sealed class DemoSeeder(FieldServiceDbContext db, DemoClock clock, IOptions<DemoOptions> options)
{
    public const string CentralWarehouse = "MAG-01";
    public const string ReservedPartId = "KOMPRESOR-R32";

    private readonly DemoOptions _options = options.Value;

    private static readonly (string Id, string Name, string Description, decimal Price)[] Catalog =
    [
        ("KOMPRESOR-R32", "Sprężarka rotacyjna R32", "Sprężarka do jednostek zewnętrznych 6–10 kW, czynnik R32.", 2890.00m),
        ("FILTR-SIATKOWY-22", "Filtr siatkowy 22 mm", "Filtr na powrocie instalacji, przyłącze 22 mm.", 45.00m),
        ("CZUJNIK-NTC-10K", "Czujnik temperatury NTC 10k", "Czujnik zanurzeniowy, przewód 2 m.", 38.50m),
        ("ZAWOR-4D", "Zawór czterodrogowy", "Zawór odwracający obieg (grzanie/chłodzenie) z cewką 230 V.", 410.00m),
        ("WENTYLATOR-EC-450", "Wentylator EC 450 mm", "Wentylator jednostki zewnętrznej z silnikiem EC.", 980.00m),
        ("PLYTA-STER-AW7", "Płyta sterująca jednostki zewnętrznej", "Moduł sterowania inwertera, seria AW7.", 1650.00m),
        ("USZCZELKI-ZESTAW", "Zestaw uszczelek serwisowych", "O-ringi i podkładki do przyłączy hydraulicznych.", 29.90m),
        ("CZYNNIK-R32-1KG", "Czynnik chłodniczy R32 (1 kg)", "Butla serwisowa z czynnikiem R32.", 120.00m),
        ("KONDENSATOR-35UF", "Kondensator rozruchowy 35 µF", "Kondensator silnika sprężarki, 450 V.", 54.00m),
        ("PRESOSTAT-HP", "Presostat wysokiego ciśnienia", "Zabezpieczenie sprężarki przed nadmiernym ciśnieniem.", 175.00m),
        ("POMPA-OBIEG-25-60", "Pompa obiegowa 25-60", "Elektroniczna pompa obiegu grzewczego.", 690.00m),
        ("NACZYNIE-WZB-18L", "Naczynie wzbiorcze 18 l", "Naczynie przeponowe do instalacji grzewczej.", 210.00m)
    ];

    private static readonly (string Name, string Street, string City)[] Customers =
    [
        ("Anna Wiśniewska", "ul. Lipowa 12", "Piaseczno"),
        ("Piotr Zieliński", "ul. Klonowa 4/2", "Warszawa"),
        ("Katarzyna Lewandowska", "ul. Polna 31", "Konstancin-Jeziorna"),
        ("Tomasz Kamiński", "ul. Słoneczna 7", "Józefosław"),
        ("Magdalena Wójcik", "ul. Ogrodowa 18", "Łomianki"),
        ("Marek Szymański", "ul. Brzozowa 9", "Ząbki"),
        ("Ewa Dąbrowska", "ul. Kwiatowa 22", "Marki"),
        ("Paweł Kozłowski", "ul. Leśna 3", "Otwock")
    ];

    private static readonly (string Device, string Fault)[] Faults =
    [
        ("Pompa ciepła powietrze-woda 8 kW", "Pompa nie grzeje, na sterowniku błąd F.022 (niskie ciśnienie wody)."),
        ("Klimatyzator ścienny 3,5 kW", "Kapie woda z jednostki wewnętrznej, słaby nadmuch."),
        ("Pompa ciepła split 12 kW", "Głośna praca jednostki zewnętrznej, wyłącza się po kilku minutach."),
        ("Klimatyzator kasetonowy 7 kW", "Nie chłodzi, sprężarka nie startuje."),
        ("Pompa ciepła powietrze-woda 6 kW", "Przegląd okresowy po sezonie grzewczym."),
        ("Pompa ciepła gruntowa 10 kW", "Za niska temperatura c.w.u., grzałka pracuje cały czas.")
    ];

    // ---------- wspólne: katalog, magazyny, zapasy ----------

    public void EnsureCatalog()
    {
        if (db.Parts.Any()) return;

        db.Parts.AddRange(Catalog.Select(p => new Part { Id = p.Id, Name = p.Name, Description = p.Description, UnitPrice = p.Price }));
        db.Warehouses.AddRange(
            new Warehouse { Id = CentralWarehouse, Name = "Magazyn centralny – Warszawa" },
            new Warehouse { Id = "MAG-02", Name = "Magazyn – Kraków" });

        var random = new Random(42);
        foreach (var (id, _, _, _) in Catalog)
        {
            var central = new PartStock { PartId = id, WarehouseId = CentralWarehouse, OnHand = random.Next(10, 26) };
            var krakow = new PartStock { PartId = id, WarehouseId = "MAG-02", OnHand = random.Next(4, 13) };
            if (id == ReservedPartId)
            {
                // ostatnia sztuka zarezerwowana przez drugie konto brygady — każda rezerwacja dostaje 409 Inventory.OutOfStock
                central.OnHand = 1;
                central.Reservations.Add(new Reservation
                {
                    Id = Guid.NewGuid(),
                    VisitId = Guid.Empty,
                    TechnicianEmail = Technicians.Normalize(_options.BrigadeEmails.Last()),
                    Quantity = 1,
                    ExpiresAt = clock.Now.AddYears(10)
                });
                krakow.OnHand = 0;
            }
            db.PartStocks.AddRange(central, krakow);
        }
        db.SaveChanges();
    }

    // ---------- dzień pracy technika ----------

    /// <summary>Tworzy dzień pracy, jeśli technik nie ma wizyt na dziś. Zwraca true, gdy coś utworzono.</summary>
    public bool EnsureDemoDay(string email)
    {
        var key = Technicians.Normalize(email);
        var today = clock.Today;
        var (from, to) = clock.DayRange(today);
        // tylko własne wizyty (wspólna wizyta brygady mogła powstać przy logowaniu drugiego technika)
        if (db.Visits.Any(v => v.TechnicianEmails.Contains(key) && v.TechnicianEmails.Count == 1 && v.PlannedStart >= from && v.PlannedStart < to))
            return false;

        var random = new Random(StableSeed(key + today.ToString("O")));
        var isFirstDay = !db.Visits.Any(v => v.TechnicianEmails.Contains(key));
        var brigade = _options.BrigadeEmails.Select(Technicians.Normalize).ToArray();
        var inBrigade = brigade.Contains(key);

        // 4 wizyty: 8:00 (rozpoczęta), 10:00, 12:30 (u brygady wspólna), 14:30
        AddVisit(random, [key], clock.At(today, 8), clock.At(today, 9, 30), started: true);
        AddVisit(random, [key], clock.At(today, 10), clock.At(today, 11, 30));
        if (!inBrigade) AddVisit(random, [key], clock.At(today, 12, 30), clock.At(today, 14));
        else if (!db.Visits.Any(v => v.TechnicianEmails.Contains(brigade[0]) && v.TechnicianEmails.Contains(brigade[1])
                                     && v.PlannedStart >= from && v.PlannedStart < to))
            AddVisit(random, brigade.ToList(), clock.At(today, 12, 30), clock.At(today, 14));
        AddVisit(random, [key], clock.At(today, 14, 30), clock.At(today, 16));

        if (isFirstDay)
        {
            AddVanStock(random, key);
            var history = key == Technicians.Normalize(_options.PerformanceEmail) ? _options.PerformanceHistoryVisits : _options.HistoryVisits;
            AddHistory(random, key, today, history);
        }

        db.SaveChanges();
        db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>Nowa wizyta (dyspozytor): nowe zlecenie i wizyta w statusie Planned.</summary>
    public Visit CreateVisit(IReadOnlyList<string> emails, DateTimeOffset start, DateTimeOffset end, string? fault)
    {
        var visit = AddVisit(new Random(), emails.Select(Technicians.Normalize).ToList(), start, end);
        if (!string.IsNullOrWhiteSpace(fault)) visit.WorkOrder.FaultDescription = fault.Trim();
        db.SaveChanges();
        return visit;
    }

    private Visit AddVisit(Random random, List<string> emails, DateTimeOffset start, DateTimeOffset end, bool started = false)
    {
        var workOrder = NewWorkOrder(random, start);
        var visit = new Visit
        {
            Id = Guid.NewGuid(),
            WorkOrder = workOrder,
            TechnicianEmails = emails,
            Status = VisitStatus.Planned,
            PlannedStart = start,
            PlannedEnd = end,
            Version = 1,
            UpdatedAt = clock.Now
        };
        visit.ImageKey = $"dev-{visit.Id:N}";
        if (started)
        {
            visit.Status = VisitStatus.InProgress;
            visit.StartedAt = start.AddMinutes(5);
            visit.Activities.Add(new VisitActivity { Code = "DIAG", Description = "Diagnostyka urządzenia", RecordedAt = start.AddMinutes(15) });
            visit.ScopeVersion = 1;
        }
        db.Visits.Add(visit);
        return visit;
    }

    private WorkOrder NewWorkOrder(Random random, DateTimeOffset start)
    {
        var customer = Customers[random.Next(Customers.Length)];
        var (device, fault) = Faults[random.Next(Faults.Length)];
        return new WorkOrder
        {
            Id = Guid.NewGuid(),
            Number = $"ZL-{start:yyMM}-{random.Next(1000, 10000)}",
            CustomerName = customer.Name,
            CustomerPhone = $"+48 000 000 {random.Next(100, 1000)}",
            Street = customer.Street,
            City = customer.City,
            DeviceName = device,
            FaultDescription = fault,
            SlaDueAt = start.AddDays(2),
            Status = WorkOrderStatus.Open
        };
    }

    private void AddVanStock(Random random, string key)
    {
        foreach (var (id, _, _, _) in Catalog.Where(p => p.Id != ReservedPartId).OrderBy(_ => random.Next()).Take(6))
            db.VanStock.Add(new VanStockItem { TechnicianEmail = key, PartId = id, Quantity = random.Next(1, 6) });
    }

    private void AddHistory(Random random, string key, DateOnly today, int count)
    {
        var slots = new[] { (8, 0), (10, 0), (12, 30), (14, 30) };
        for (var i = 0; i < count; i++)
        {
            var day = today.AddDays(-1 - i / slots.Length);
            var (hour, minute) = slots[i % slots.Length];
            var start = clock.At(day, hour, minute);
            var visit = AddVisit(random, [key], start, start.AddMinutes(90));

            visit.Status = VisitStatus.Completed;
            visit.StartedAt = start.AddMinutes(random.Next(0, 20));
            visit.Activities.Add(new VisitActivity { Code = "DIAG", Description = "Diagnostyka urządzenia", RecordedAt = visit.StartedAt.Value.AddMinutes(10) });
            var part = Catalog[random.Next(1, Catalog.Length)];
            if (random.Next(3) > 0) visit.Parts.Add(new VisitPart { PartId = part.Id, Quantity = random.Next(1, 3) });
            visit.ScopeVersion = visit.Parts.Count + 1;
            visit.CompletedAt = start.AddMinutes(random.Next(60, 120));
            visit.Signature = new VisitSignature { SignerName = visit.WorkOrder.CustomerName, SignedAt = visit.CompletedAt.Value.AddMinutes(-5), ScopeVersion = visit.ScopeVersion };

            var roll = random.Next(10);
            visit.Outcome = roll < 8 ? VisitOutcomeType.Fixed : roll < 9 ? VisitOutcomeType.NeedsFollowUp : VisitOutcomeType.NotFixed;
            visit.OutcomeReason = visit.Outcome switch
            {
                VisitOutcomeType.NeedsFollowUp => "Brak części w aucie — potrzebna kolejna wizyta.",
                VisitOutcomeType.NotFixed => "Klient nie zgodził się na wymianę sprężarki.",
                _ => null
            };
            if (visit.Outcome == VisitOutcomeType.Fixed) visit.WorkOrder.Status = WorkOrderStatus.Closed;
        }
    }

    private static int StableSeed(string value) => BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

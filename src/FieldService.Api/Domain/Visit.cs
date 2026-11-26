namespace FieldService.Api.Domain;

public enum VisitStatus { Planned, InProgress, Completed, Cancelled }

public enum VisitOutcomeType { Fixed, NeedsFollowUp, NotFixed }

public sealed class VisitActivity
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class VisitPart
{
    public string PartId { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>Podpis klienta pod konkretną wersją zakresu prac.</summary>
public sealed class VisitSignature
{
    public string SignerName { get; set; } = "";
    public DateTimeOffset SignedAt { get; set; }
    public int ScopeVersion { get; set; }
}

/// <summary>
/// Wizyta — źródło prawdy na serwerze (aplikacja ma własną replikę modelu). Reguły R1–R7 jak w aplikacji;
/// serwer jest ostatecznym arbitrem: wersja (<see cref="Version"/>) rozstrzyga konflikty telefonów i dyspozytora.
/// Metody zwracają null (zgoda) albo <see cref="ApiError"/> (odmowa) i niczego nie zmieniają przy odmowie.
/// </summary>
public sealed class Visit
{
    public Guid Id { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;

    /// <summary>Technik albo brygada (dwa adresy e-mail, małe litery).</summary>
    public List<string> TechnicianEmails { get; set; } = [];

    public VisitStatus Status { get; set; }
    public DateTimeOffset PlannedStart { get; set; }
    public DateTimeOffset PlannedEnd { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<VisitActivity> Activities { get; set; } = [];
    public List<VisitPart> Parts { get; set; } = [];

    /// <summary>Rośnie przy każdej zmianie zakresu prac; podpis jest ważny tylko dla wersji, pod którą go złożono.</summary>
    public int ScopeVersion { get; set; }

    public VisitSignature? Signature { get; set; }
    public VisitOutcomeType? Outcome { get; set; }
    public string? OutcomeReason { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>Klucz obrazka (miniatura urządzenia) w <c>api/images/{key}</c>.</summary>
    public string ImageKey { get; set; } = "";

    /// <summary>Wersja do blokowania optymistycznego (<c>ETag</c> / <c>If-Match</c>).</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasValidSignature => Signature is not null && Signature.ScopeVersion == ScopeVersion;

    // R1
    public ApiError? Start(DateTimeOffset at)
    {
        if (Status != VisitStatus.Planned) return VisitErrors.InvalidTransition(Status, "Rozpocznij");
        Status = VisitStatus.InProgress;
        StartedAt = at;
        return null;
    }

    // R2 + R3
    public ApiError? RecordActivity(string code, string description, DateTimeOffset at)
    {
        if (Status != VisitStatus.InProgress) return VisitErrors.NotInProgress(Status);
        if (string.IsNullOrWhiteSpace(code) || code.Length > 20) return VisitErrors.Validation("Kod czynności: 1–20 znaków.");
        if (description.Length > 200) return VisitErrors.Validation("Opis czynności: maks. 200 znaków.");
        Activities.Add(new VisitActivity { Code = code.Trim(), Description = description.Trim(), RecordedAt = at });
        ScopeChanged();
        return null;
    }

    // R2 + R3
    public ApiError? UsePart(string partId, int quantity)
    {
        if (Status != VisitStatus.InProgress) return VisitErrors.NotInProgress(Status);
        if (quantity <= 0) return VisitErrors.InvalidQuantity;
        var used = Parts.FirstOrDefault(p => p.PartId == partId);
        if (used is null) Parts.Add(new VisitPart { PartId = partId, Quantity = quantity });
        else used.Quantity += quantity;
        ScopeChanged();
        return null;
    }

    // R7 + R3
    public ApiError? RemoveUsedPart(string partId)
    {
        if (Status != VisitStatus.InProgress) return VisitErrors.NotInProgress(Status);
        if (Parts.RemoveAll(p => p.PartId == partId) == 0) return VisitErrors.PartNotInScope(partId);
        ScopeChanged();
        return null;
    }

    // R4
    public ApiError? SignByCustomer(string signerName, DateTimeOffset at)
    {
        if (Status != VisitStatus.InProgress) return VisitErrors.NotInProgress(Status);
        if (Activities.Count == 0 && Parts.Count == 0) return VisitErrors.EmptyScope;
        if (string.IsNullOrWhiteSpace(signerName) || signerName.Length > 100)
            return VisitErrors.Validation("Imię i nazwisko podpisującego: 1–100 znaków.");
        Signature = new VisitSignature { SignerName = signerName.Trim(), SignedAt = at, ScopeVersion = ScopeVersion };
        return null;
    }

    // R5
    public ApiError? Complete(VisitOutcomeType outcome, string? reason, DateTimeOffset at)
    {
        if (Status == VisitStatus.Completed) return VisitErrors.AlreadyCompleted;
        if (Status != VisitStatus.InProgress) return VisitErrors.InvalidTransition(Status, "Zakończ");
        if (outcome == VisitOutcomeType.Fixed && !HasValidSignature) return VisitErrors.SignatureRequired;
        if (outcome != VisitOutcomeType.Fixed && string.IsNullOrWhiteSpace(reason)) return VisitErrors.ReasonRequired;
        Status = VisitStatus.Completed;
        Outcome = outcome;
        OutcomeReason = outcome == VisitOutcomeType.Fixed ? null : reason!.Trim();
        CompletedAt = at;
        return null;
    }

    // R6 (dyspozytor)
    public ApiError? Cancel(string reason, DateTimeOffset at)
    {
        if (Status == VisitStatus.Completed) return VisitErrors.AlreadyCompleted;
        if (Status == VisitStatus.Cancelled) return VisitErrors.InvalidTransition(Status, "Anuluj");
        Status = VisitStatus.Cancelled;
        CancelReason = reason;
        CompletedAt = at;
        return null;
    }

    /// <summary>Przeniesienie terminu (dyspozytor) — tylko wizyta jeszcze nierozpoczęta.</summary>
    public ApiError? Reschedule(DateTimeOffset start, DateTimeOffset end)
    {
        if (Status != VisitStatus.Planned) return VisitErrors.InvalidTransition(Status, "Przenieś");
        if (end <= start) return VisitErrors.Validation("Koniec okna musi być po początku.");
        PlannedStart = start;
        PlannedEnd = end;
        return null;
    }

    // R3: zmiana zakresu unieważnia podpis (podpis wskazuje starszą wersję zakresu)
    private void ScopeChanged() => ScopeVersion++;
}

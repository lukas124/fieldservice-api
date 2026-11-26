using FieldService.Api.Contracts;
using FieldService.Api.Domain;

namespace FieldService.Api.Infrastructure;

public static class Mappers
{
    public static VisitSummaryDto ToSummary(this Visit v, HttpRequest request) => new()
    {
        Id = v.Id,
        WorkOrderId = v.WorkOrderId,
        WorkOrderNumber = v.WorkOrder.Number,
        Status = v.Status,
        PlannedStart = v.PlannedStart,
        PlannedEnd = v.PlannedEnd,
        CustomerName = v.WorkOrder.CustomerName,
        Address = $"{v.WorkOrder.Street}, {v.WorkOrder.City}",
        DeviceName = v.WorkOrder.DeviceName,
        FaultDescription = v.WorkOrder.FaultDescription,
        IsBrigade = v.TechnicianEmails.Count > 1,
        ThumbnailUrl = Urls.Image(request, v.ImageKey, thumbnail: true),
        Version = v.Version,
        EstimatedArrival = v.Status switch
        {
            VisitStatus.Planned => v.PlannedStart,
            VisitStatus.InProgress => v.StartedAt,
            _ => null
        }
    };

    public static VisitDetailsDto ToDetails(this Visit v, HttpRequest request, IReadOnlyDictionary<string, string> partNames,
        IReadOnlyDictionary<string, string> technicianNames) => new()
    {
        Id = v.Id,
        WorkOrderId = v.WorkOrderId,
        WorkOrderNumber = v.WorkOrder.Number,
        Status = v.Status,
        PlannedStart = v.PlannedStart,
        PlannedEnd = v.PlannedEnd,
        StartedAt = v.StartedAt,
        CompletedAt = v.CompletedAt,
        Customer = new CustomerDto
        {
            Name = v.WorkOrder.CustomerName, Phone = v.WorkOrder.CustomerPhone, Street = v.WorkOrder.Street, City = v.WorkOrder.City
        },
        DeviceName = v.WorkOrder.DeviceName,
        FaultDescription = v.WorkOrder.FaultDescription,
        SlaDueAt = v.WorkOrder.SlaDueAt,
        TechnicianNames = v.TechnicianEmails.Select(e => technicianNames.GetValueOrDefault(e) ?? e.Split('@')[0]).ToList(),
        TechnicianEmails = v.TechnicianEmails,
        Activities = v.Activities.Select(a => new ActivityDto { Code = a.Code, Description = a.Description, RecordedAt = a.RecordedAt }).ToList(),
        Parts = v.Parts.Select(p => new UsedPartDto { PartId = p.PartId, PartName = partNames.GetValueOrDefault(p.PartId) ?? p.PartId, Quantity = p.Quantity }).ToList(),
        ScopeVersion = v.ScopeVersion,
        Signature = v.Signature is null ? null : new SignatureDto
        {
            SignerName = v.Signature.SignerName, SignedAt = v.Signature.SignedAt, ScopeVersion = v.Signature.ScopeVersion, IsValid = v.HasValidSignature
        },
        Outcome = v.Outcome,
        OutcomeReason = v.OutcomeReason,
        CancelReason = v.CancelReason,
        ThumbnailUrl = Urls.Image(request, v.ImageKey, thumbnail: true),
        ImageUrl = Urls.Image(request, v.ImageKey, thumbnail: false),
        Version = v.Version
    };

    public static VisitHistoryItemDto ToHistoryItem(this Visit v, HttpRequest request) => new()
    {
        Id = v.Id,
        WorkOrderNumber = v.WorkOrder.Number,
        Status = v.Status,
        PlannedStart = v.PlannedStart,
        CompletedAt = v.CompletedAt,
        CustomerName = v.WorkOrder.CustomerName,
        City = v.WorkOrder.City,
        DeviceName = v.WorkOrder.DeviceName,
        Outcome = v.Outcome,
        ThumbnailUrl = Urls.Image(request, v.ImageKey, thumbnail: true)
    };

    public static PartDto ToDto(this Part p, HttpRequest request) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        UnitPrice = p.UnitPrice,
        ImageUrl = Urls.Image(request, PartImageKey(p.Id), thumbnail: false),
        ThumbnailUrl = Urls.Image(request, PartImageKey(p.Id), thumbnail: true)
    };

    public static ReservationDto ToDto(this Reservation r, PartStock stock) => new()
    {
        Id = r.Id, PartId = stock.PartId, WarehouseId = stock.WarehouseId, VisitId = r.VisitId, Quantity = r.Quantity, ExpiresAt = r.ExpiresAt
    };

    public static string PartImageKey(string partId) => "part-" + partId.ToLowerInvariant();
}

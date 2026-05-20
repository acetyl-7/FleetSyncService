namespace FleetSyncService.Models;
using System;

public class NotificationSqlModel
{
    public Guid Id { get; set; }
    public int FleetcomDriverId { get; set; }
    public string? FirebaseUid { get; set; }
    public DateTime? Date { get; set; }
    public string? Body { get; set; }
}

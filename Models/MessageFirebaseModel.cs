namespace FleetSyncService.Models;
using System;

public class MessageFirebaseModel
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty; // Firebase UID
    public string Status { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }
    public bool NeedsSqlSync { get; set; }
    public string? SqlNotificationId { get; set; }
}

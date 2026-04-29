namespace FleetSyncService.Models;

public class TaskModel
{
    public string Id { get; set; } = string.Empty;
    public string SqlId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? StatusDate { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public bool NeedsSqlSync { get; set; }
    public int? FleetcomTaskTypeId { get; set; }
}

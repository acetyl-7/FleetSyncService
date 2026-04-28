namespace FleetSyncService.Models;

public class TaskSqlModel
{
    public Guid Id { get; set; }
    public int? FleetcomTaskOrder { get; set; }
    public int? FleetcomTaskId { get; set; }
    public int? FleetcomDriverId { get; set; }
    public int? FleetcomTractorId { get; set; }
    public int? FleetcomTrailerId { get; set; }
    public int? FleetcomTaskTypeId { get; set; }
    public string TractorPlate { get; set; } = string.Empty;
    public string TrailerPlate { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public DateTime? Date { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastUpdated { get; set; }
    public bool Deleted { get; set; }
}

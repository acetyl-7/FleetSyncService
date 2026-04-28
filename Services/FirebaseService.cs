using FleetSyncService.Config;
using FleetSyncService.Models;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;

namespace FleetSyncService.Services;

public interface IFirebaseService
{
    Task<FirestoreDb> GetDatabaseAsync();
    Task UpsertDriverAsync(DriverSqlModel sqlDriver);
    Task UpsertTaskAsync(TaskSqlModel sqlTask);
    void StartTasksListener(Func<Guid, string, DateTime, Task> onTaskUpdatedCallback, ILogger logger);
    Task<IEnumerable<DriverFirebaseModel>> GetAuthorizedDriversAsync();
    Task<IEnumerable<DriverFirebaseModel>> GetAllFirebaseUsersAsync();
    Task UpdateDriverSqlIdAsync(string uid, string generatedSqlId);
    Task UpdateDriverIdAsync(string uid, string driverId);
}

public class FirebaseService : IFirebaseService
{
    private readonly FirebaseConfig _config;
    private readonly FirestoreDb _db;

    public FirebaseService(IOptions<FirebaseConfig> config)
    {
        _config = config.Value;

        if (string.IsNullOrEmpty(_config.ProjectId))
        {
            throw new InvalidOperationException("Firebase ProjectId is not configured in appsettings.json.");
        }

        // Initialize Firestore with credentials
        if (!string.IsNullOrEmpty(_config.CredentialsFilePath) && System.IO.File.Exists(_config.CredentialsFilePath))
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", _config.CredentialsFilePath);
        }
        else if (System.IO.File.Exists("service-account.json"))
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "service-account.json");
        }

        _db = FirestoreDb.Create(_config.ProjectId);
    }

    public Task<FirestoreDb> GetDatabaseAsync() => Task.FromResult(_db);

    public async Task UpsertDriverAsync(DriverSqlModel sqlDriver)
    {
        DocumentReference docRef = _db.Collection("users").Document(sqlDriver.Id.ToString());

        var data = new Dictionary<string, object>
        {
            { "sqlId", sqlDriver.Id.ToString() },
            { "nickname", sqlDriver.Alcunha ?? "" },
            { "email", sqlDriver.Email ?? "" },
            { "firebaseUid", sqlDriver.FirebaseUid ?? "" },
            { "phone", sqlDriver.Telemovel ?? "" },
            { "role", "Driver" }
        };

        bool hasEmail = !string.IsNullOrWhiteSpace(sqlDriver.Email);
        if (hasEmail)
        {
            data["isAuthorized"] = true;
        }

        var snapshot = await docRef.GetSnapshotAsync();
        if (!snapshot.Exists)
        {
            if (!hasEmail)
            {
                data.Add("isAuthorized", false);
            }
        }

        await docRef.SetAsync(data, SetOptions.MergeAll);
    }

    public async Task UpsertTaskAsync(TaskSqlModel sqlTask)
    {
        // O Tradutor Inverso: Converte o número do SQL para a palavra do Firebase
        string firebaseStatus = sqlTask.Status?.ToString() switch
        {
            "1" => "pending",
            "2" => "in_progress",
            "3" => "completed",
            _ => "pending" // Default
        };

        var taskData = new Dictionary<string, object?>
        {
            { "sqlId", sqlTask.Id.ToString() },
            { "status", firebaseStatus },
            { "tractorPlate", sqlTask.TractorPlate ?? "" },
            { "trailerPlate", sqlTask.TrailerPlate ?? "" },
            { "city", sqlTask.City ?? "" },
            { "address", sqlTask.Address ?? "" },
            { "country", sqlTask.Country ?? "" },
            { "driverId", sqlTask.FleetcomDriverId?.ToString() ?? "" },
            { "fleetcomTaskOrder", sqlTask.FleetcomTaskOrder },
            { "fleetcomTaskId", sqlTask.FleetcomTaskId },
            { "fleetcomTractorId", sqlTask.FleetcomTractorId },
            { "fleetcomTrailerId", sqlTask.FleetcomTrailerId },
            { "fleetcomTaskTypeId", sqlTask.FleetcomTaskTypeId },
            { "date", sqlTask.Date.HasValue ? Timestamp.FromDateTime(sqlTask.Date.Value.ToUniversalTime()) : null }
        };

        // Remove valores nulos para não sujar o Firebase
        var cleanData = taskData.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);

        await _db.Collection("tasks").Document(sqlTask.Id.ToString()).SetAsync(cleanData, SetOptions.Overwrite);
    }

    public void StartTasksListener(Func<Guid, string, DateTime, Task> onTaskUpdatedCallback, ILogger logger)
    {
        var query = _db.Collection("tasks");
        query.Listen(async snapshot =>
        {
            foreach (var change in snapshot.Changes)
            {
                // Só nos interessam as tarefas que foram Modificadas (ex: concluídas no telemóvel)
                if (change.ChangeType == DocumentChange.Type.Modified)
                {
                    var doc = change.Document;

                    // Ignora se não tiver os campos necessários
                    if (!doc.ContainsField("sqlId") || !doc.ContainsField("status")) continue;

                    var sqlIdStr = doc.GetValue<string>("sqlId");
                    var status = doc.GetValue<string>("status");

                    // Tenta extrair a data de conclusão (se existir, senão usa a data atual)
                    DateTime completedAt = DateTime.Now;
                    if (doc.ContainsField("completedAt"))
                    {
                        completedAt = doc.GetValue<Timestamp>("completedAt").ToDateTime().ToLocalTime();
                    }

                    if (Guid.TryParse(sqlIdStr, out Guid sqlGuid))
                    {
                        logger.LogInformation("Detetada alteração na tarefa {TaskId} para o estado: {Status}", sqlIdStr, status);
                        await onTaskUpdatedCallback(sqlGuid, status, completedAt);
                    }
                }
            }
        });
    }

    public async Task<IEnumerable<DriverFirebaseModel>> GetAuthorizedDriversAsync()
    {
        var snapshot = await _db.Collection("users")
            .WhereEqualTo("role", "Driver")
            .WhereEqualTo("isAuthorized", true)
            .GetSnapshotAsync();
            
        var list = new List<DriverFirebaseModel>();
        foreach(var doc in snapshot.Documents)
        {
            if(!doc.Exists) continue;
            list.Add(new DriverFirebaseModel
            {
                Uid = doc.ContainsField("firebaseUid") && !string.IsNullOrEmpty(doc.GetValue<string>("firebaseUid")) 
                        ? doc.GetValue<string>("firebaseUid") 
                        : doc.Id,
                SqlId = doc.ContainsField("sqlId") ? doc.GetValue<string>("sqlId") : string.Empty,
                Name = doc.ContainsField("name") ? doc.GetValue<string>("name") : string.Empty,
                Nickname = doc.ContainsField("nickname") ? doc.GetValue<string>("nickname") : string.Empty,
                Phone = doc.ContainsField("phone") ? doc.GetValue<string>("phone") : string.Empty,
                Email = doc.ContainsField("email") ? doc.GetValue<string>("email") : string.Empty
            });
        }
        return list;
    }

    public async Task<IEnumerable<DriverFirebaseModel>> GetAllFirebaseUsersAsync()
    {
        // Vamos buscar todos para garantir que apanhamos utilizadores antigos sem o campo 'role'
        var snapshot = await _db.Collection("users").GetSnapshotAsync();
            
        var list = new List<DriverFirebaseModel>();
        foreach(var doc in snapshot.Documents)
        {
            if(!doc.Exists) continue;
            
            var data = doc.ToDictionary();
            
            // Se tiver role, tem de ser Driver. Se não tiver role, assumimos que é driver (retrocompatibilidade)
            if (data.ContainsKey("role") && data["role"]?.ToString() != "Driver") continue;

            string name = doc.ContainsField("name") ? doc.GetValue<string>("name") : string.Empty;
            string nickname = doc.ContainsField("nickname") ? doc.GetValue<string>("nickname") : string.Empty;

            // Fallback para utilizadores antigos que só têm o campo 'name'
            if (string.IsNullOrWhiteSpace(nickname) && !string.IsNullOrWhiteSpace(name))
            {
                nickname = name;
            }

            list.Add(new DriverFirebaseModel
            {
                Uid = doc.ContainsField("firebaseUid") && !string.IsNullOrEmpty(doc.GetValue<string>("firebaseUid")) 
                        ? doc.GetValue<string>("firebaseUid") 
                        : doc.Id,
                SqlId = doc.ContainsField("sqlId") ? doc.GetValue<string>("sqlId") : string.Empty,
                Name = name,
                Nickname = nickname,
                Phone = doc.ContainsField("phone") ? doc.GetValue<string>("phone") : string.Empty,
                Email = doc.ContainsField("email") ? doc.GetValue<string>("email") : string.Empty
            });
        }
        return list;
    }

    public async Task UpdateDriverSqlIdAsync(string uid, string generatedSqlId)
    {
        var docRef = _db.Collection("users").Document(uid);
        var data = new Dictionary<string, object> { { "sqlId", generatedSqlId } };
        await docRef.SetAsync(data, SetOptions.MergeAll);
    }

    public async Task UpdateDriverIdAsync(string uid, string driverId)
    {
        var docRef = _db.Collection("users").Document(uid);
        var data = new Dictionary<string, object> { { "driverId", driverId } };
        await docRef.SetAsync(data, SetOptions.MergeAll);
    }
}
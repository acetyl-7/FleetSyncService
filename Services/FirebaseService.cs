#pragma warning disable CS8602

using FleetSyncService.Config;
using FleetSyncService.Models;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace FleetSyncService.Services;

public interface IFirebaseService
{
    bool IsEnabled { get; }
    Task<FirestoreDb> GetDatabaseAsync();
    Task UpsertDriverAsync(DriverSqlModel sqlDriver);
    Task UpsertCompanyDriverAsync(DriverSqlModel sqlDriver);
    Task UpsertTaskAsync(TaskSqlModel sqlTask);
    Task UpsertMessageAsync(MessageFirebaseModel msg);
    void StartTasksListener(Func<Guid, string, DateTime, Task> onTaskUpdatedCallback, ILogger logger);
    Task<IEnumerable<DriverFirebaseModel>> GetAuthorizedDriversAsync();
    Task<IEnumerable<DriverFirebaseModel>> GetAllFirebaseUsersAsync();
    Task UpdateDriverSqlIdAsync(string uid, string generatedSqlId);
    Task UpdateDriverIdAsync(string uid, string driverId);
    Task<List<TaskModel>> GetTasksPendingSqlSyncAsync();
    Task MarkTaskAsSyncedAsync(string firebaseTaskId);
    Task<List<TaskModel>> GetActiveFirebaseTasksAsync();
    Task DeleteTaskAsync(string taskId);
    Task<List<MessageFirebaseModel>> GetMessagesPendingSqlSyncAsync();
    Task MarkMessageAsSyncedAsync(string messageId, string sqlNotificationId);
    Task<List<MessageFirebaseModel>> GetMessagesPendingAckSyncAsync();
    Task MarkMessageAsAckedAsync(string messageId);
    Task IncrementYearlyStatsAsync(string driverId, DateTime date, string type);
    FirestoreChangeListener ListenToPendingTasks(Func<List<TaskModel>, Task> callback);
    FirestoreChangeListener ListenToPendingMessages(Func<List<MessageFirebaseModel>, Task> callback);
    FirestoreChangeListener ListenToPendingAcks(Func<List<MessageFirebaseModel>, Task> callback);

    // Novos métodos de Sincronização e Limpeza periódica
    Task<List<AbastecimentoFirestoreModel>> GetPendingRefuelsAsync();
    Task MarkRefuelAsSyncedAsync(string id);
    Task DeleteRefuelAsync(string id);
    Task<List<IncidentFirestoreModel>> GetPendingIncidentsAsync();
    Task MarkIncidentAsSyncedAsync(string id);
    Task DeleteIncidentAsync(string id);
    Task DeleteMessageAsync(string id);
    Task UpdateDriverSyncErrorAsync(string driverUid, string syncError);
    Task<int> GetUnsyncedTasksForDriverCountAsync(string driverUid, string sqlDriverId);
    Task<int> GetUnsyncedMessagesForDriverCountAsync(string driverUid);
    Task<int> GetUnsyncedRefuelsForDriverCountAsync(string driverUid);
    Task<int> GetUnsyncedIncidentsForDriverCountAsync(string driverUid);
    Task<string> GetLastCleanupDateAsync();
    Task SetLastCleanupDateAsync(string dateStr);
    Task<List<TaskModel>> GetSyncedTasksForDriverAsync(string driverUid, string sqlDriverId);
    Task<List<AbastecimentoFirestoreModel>> GetSyncedRefuelsForDriverAsync(string driverUid);
    Task<List<IncidentFirestoreModel>> GetSyncedIncidentsForDriverAsync(string driverUid);
    Task<List<MessageFirebaseModel>> GetSyncedMessagesForDriverAsync(string driverUid);
}

public class FirebaseService : IFirebaseService
{
    private readonly FirebaseConfig _config;
    private readonly FirestoreDb? _db;
    private readonly bool _isEnabled;

    public bool IsEnabled => _isEnabled;

    public FirebaseService(IOptions<FirebaseConfig> config, ILogger<FirebaseService> logger)
    {
        _config = config.Value;

        if (string.IsNullOrEmpty(_config.ProjectId))
        {
            logger.LogError("Firebase ProjectId is not configured in appsettings.json.");
            _isEnabled = false;
            return;
        }

        // Resolve absolute paths relative to application base directory to avoid issues when running as a Windows Service
        string credentialsPath = string.IsNullOrEmpty(_config.CredentialsFilePath) 
            ? "service-account.json" 
            : _config.CredentialsFilePath;

        if (!System.IO.Path.IsPathRooted(credentialsPath))
        {
            credentialsPath = System.IO.Path.Combine(AppContext.BaseDirectory, credentialsPath);
        }

        if (System.IO.File.Exists(credentialsPath))
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", credentialsPath);
        }

        try
        {
            _db = FirestoreDb.Create(_config.ProjectId);
            _isEnabled = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not initialize Firestore: {Message}. Firebase synchronization features will be disabled. To enable them, please place a valid 'service-account.json' in the project root or configure 'CredentialsFilePath' in appsettings.json.", ex.Message);
            _db = null;
            _isEnabled = false;
        }
    }

    public Task<FirestoreDb> GetDatabaseAsync()
    {
        if (_db == null)
        {
            throw new InvalidOperationException("Firebase database is not initialized due to missing credentials.");
        }
        return Task.FromResult(_db);
    }

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

    /// <summary>
    /// Escreve na coleção 'company_drivers' apenas os dados mínimos para validação de telemóvel.
    /// Esta coleção é separada da 'users' para não a poluir com motoristas que nunca usaram a app.
    /// </summary>
    public async Task UpsertCompanyDriverAsync(DriverSqlModel sqlDriver)
    {
        // Normalizar o telemóvel: só dígitos, sem prefixo 351
        var phone = new string((sqlDriver.TelemovelEmpresa ?? "").Where(char.IsDigit).ToArray());
        if (phone.StartsWith("351") && phone.Length > 9)
        {
            phone = phone.Substring(3);
        }

        if (string.IsNullOrEmpty(phone)) return;

        var docRef = _db.Collection("company_drivers").Document(sqlDriver.Id.ToString());
        var data = new Dictionary<string, object>
        {
            { "sqlId", sqlDriver.Id.ToString() },
            { "nickname", sqlDriver.Alcunha ?? "" },
            { "companyPhone", phone }
        };

        await docRef.SetAsync(data, SetOptions.MergeAll);
    }

    public async Task UpsertTaskAsync(TaskSqlModel sqlTask)
    {
        // O Tradutor Inverso: Converte o número do SQL para a palavra do Firebase
        string firebaseStatus = sqlTask.Status?.ToString() switch
        {
            "0" => "por_enviar",
            "1" => "por_enviar",
            "10" => "enviada",
            "20" => "recebida",
            "30" => "vista",
            "40" => "iniciada",
            "80" => "terminada",
            "90" => "anulada",
            _ => "por_enviar"
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
            { "ref", sqlTask.Ref ?? "" },
            { "obs", sqlTask.Obs ?? "" },
            { "taskTypeName", sqlTask.TaskTypeName ?? "" },
            { "driverId", sqlTask.FleetcomDriverId?.ToString() ?? "" },
            { "fleetcomTaskOrder", sqlTask.FleetcomTaskOrder },
            { "fleetcomTaskId", sqlTask.FleetcomTaskId },
            { "fleetcomTractorId", sqlTask.FleetcomTractorId },
            { "fleetcomTrailerId", sqlTask.FleetcomTrailerId },
            { "fleetcomTaskTypeId", sqlTask.FleetcomTaskTypeId },
            { "date", sqlTask.Date.HasValue ? Timestamp.FromDateTime(sqlTask.Date.Value.ToUniversalTime()) : null },
            { "timestamp", sqlTask.Date.HasValue ? Timestamp.FromDateTime(sqlTask.Date.Value.ToUniversalTime()) : Timestamp.FromDateTime(DateTime.UtcNow) }
        };

        // Remove valores nulos para não sujar o Firebase
        var cleanData = taskData.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);

        var docRef = _db.Collection("tasks").Document(sqlTask.Id.ToString());
        var snapshot = await docRef.GetSnapshotAsync();

        // Se a tarefa já existir e estiver pendente de sync, não atualizamos o estado para evitar atropelos
        if (snapshot.Exists && snapshot.ContainsField("needsSqlSync") && snapshot.GetValue<bool>("needsSqlSync") == true)
        {
            cleanData.Remove("status");
        }

        await docRef.SetAsync(cleanData, SetOptions.MergeAll);
    }

    public async Task UpsertMessageAsync(MessageFirebaseModel msg)
    {
        var docRef = _db.Collection("messages").Document(msg.Id);
        var data = new Dictionary<string, object?>
        {
            { "text", msg.Text },
            { "sender", msg.Sender },
            { "role", "hq" },
            { "driverId", msg.DriverId },
            { "status", msg.Status },
            { "type", msg.Type },
            { "timestamp", msg.Timestamp.HasValue ? Timestamp.FromDateTime(msg.Timestamp.Value.ToUniversalTime()) : null },
            { "sqlNotificationId", msg.SqlNotificationId },
            { "needsSqlSync", msg.NeedsSqlSync },
            { "sqlAck", false }
        };

        var cleanData = data.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        await docRef.SetAsync(cleanData, SetOptions.MergeAll);
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
                    DateTime completedAt = DateTime.UtcNow;
                    if (doc.ContainsField("completedAt"))
                    {
                        completedAt = doc.GetValue<Timestamp>("completedAt").ToDateTime();
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
                Email = doc.ContainsField("email") ? doc.GetValue<string>("email") : string.Empty,
                SyncError = doc.ContainsField("syncError") ? doc.GetValue<string>("syncError") : string.Empty
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
                Email = doc.ContainsField("email") ? doc.GetValue<string>("email") : string.Empty,
                SyncError = doc.ContainsField("syncError") ? doc.GetValue<string>("syncError") : string.Empty
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

    public async Task<List<TaskModel>> GetTasksPendingSqlSyncAsync()
    {
        // Utiliza uma query nativa do Firestore para buscar apenas as tarefas pendentes,
        // reduzindo drasticamente o custo de leitura.
        var snapshot = await _db.Collection("tasks")
                                .WhereEqualTo("needsSqlSync", true)
                                .GetSnapshotAsync();

        var list = new List<TaskModel>();
        foreach (var doc in snapshot.Documents)
        {
            if (!doc.Exists) continue;

            DateTime? statusDate = null;
            if (doc.ContainsField("statusDate"))
                statusDate = doc.GetValue<Timestamp>("statusDate").ToDateTime();
            else if (doc.ContainsField("startedAt"))
                statusDate = doc.GetValue<Timestamp>("startedAt").ToDateTime();
            else if (doc.ContainsField("completedAt"))
                statusDate = doc.GetValue<Timestamp>("completedAt").ToDateTime();

            double? lat = null;
            double? lon = null;
            var status = doc.ContainsField("status") ? doc.GetValue<string>("status") : string.Empty;

            if (doc.ContainsField("startLocation"))
            {
                var gp = doc.GetValue<GeoPoint>("startLocation");
                lat = gp.Latitude;
                lon = gp.Longitude;
            }
            if (doc.ContainsField("completeLocation"))
            {
                var gp = doc.GetValue<GeoPoint>("completeLocation");
                lat = gp.Latitude;
                lon = gp.Longitude;
            }
            if (lat == null && doc.ContainsField("lat"))
                lat = doc.GetValue<double>("lat");
            if (lon == null && doc.ContainsField("lon"))
                lon = doc.GetValue<double>("lon");

            list.Add(new TaskModel
            {
                Id = doc.Id,
                SqlId = doc.ContainsField("sqlId") ? doc.GetValue<string>("sqlId") : string.Empty,
                Status = status,
                StatusDate = statusDate,
                Lat = lat,
                Lon = lon,
                NeedsSqlSync = true,
                FleetcomTaskTypeId = doc.ContainsField("fleetcomTaskTypeId") ? (int?)doc.GetValue<long>("fleetcomTaskTypeId") : null,
                DriverId = doc.ContainsField("driverId") ? doc.GetValue<string>("driverId") : string.Empty,
                CompletedAt = statusDate
            });
        }
        return list;
    }

    public async Task MarkTaskAsSyncedAsync(string firebaseTaskId)
    {
        var docRef = _db.Collection("tasks").Document(firebaseTaskId);
        await docRef.SetAsync(new Dictionary<string, object> { { "needsSqlSync", false } }, SetOptions.MergeAll);
    }

    public async Task<List<TaskModel>> GetActiveFirebaseTasksAsync()
    {
        var snapshot = await _db.Collection("tasks")
            .GetSnapshotAsync();
            
        var list = new List<TaskModel>();
        foreach (var doc in snapshot.Documents)
        {
            if (!doc.Exists) continue;
            list.Add(new TaskModel
            {
                Id = doc.Id,
                SqlId = doc.ContainsField("sqlId") ? doc.GetValue<string>("sqlId") : string.Empty,
                Status = doc.ContainsField("status") ? doc.GetValue<string>("status") : string.Empty,
                NeedsSqlSync = doc.ContainsField("needsSqlSync") && doc.GetValue<bool>("needsSqlSync")
            });
        }
        return list;
    }

    public async Task DeleteTaskAsync(string taskId)
    {
        var docRef = _db.Collection("tasks").Document(taskId);
        await docRef.DeleteAsync();
    }

    public async Task<List<MessageFirebaseModel>> GetMessagesPendingSqlSyncAsync()
    {
        var snapshot = await _db.Collection("messages")
            .WhereEqualTo("sender", "hq")
            .WhereEqualTo("needsSqlSync", true)
            .GetSnapshotAsync();

        var list = new List<MessageFirebaseModel>();
        foreach (var doc in snapshot.Documents)
        {
            if (!doc.Exists) continue;
            
            DateTime? timestamp = null;
            if (doc.ContainsField("timestamp"))
            {
                var ts = doc.GetValue<Timestamp>("timestamp");
                timestamp = ts.ToDateTime();
            }

            list.Add(new MessageFirebaseModel
            {
                Id = doc.Id,
                Text = doc.ContainsField("text") ? doc.GetValue<string>("text") : string.Empty,
                Sender = doc.ContainsField("sender") ? doc.GetValue<string>("sender") : string.Empty,
                DriverId = doc.ContainsField("driverId") ? doc.GetValue<string>("driverId") : string.Empty,
                Status = doc.ContainsField("status") ? doc.GetValue<string>("status") : string.Empty,
                Type = doc.ContainsField("type") ? doc.GetValue<string>("type") : string.Empty,
                Timestamp = timestamp,
                NeedsSqlSync = true
            });
        }
        return list;
    }

    public async Task MarkMessageAsSyncedAsync(string messageId, string sqlNotificationId)
    {
        var docRef = _db.Collection("messages").Document(messageId);
        await docRef.SetAsync(new Dictionary<string, object> 
        { 
            { "needsSqlSync", false },
            { "sqlNotificationId", sqlNotificationId },
            { "sqlAck", false }
        }, SetOptions.MergeAll);
    }

    public async Task<List<MessageFirebaseModel>> GetMessagesPendingAckSyncAsync()
    {
        var snapshot = await _db.Collection("messages")
            .WhereEqualTo("sender", "hq")
            .WhereEqualTo("status", "read")
            .WhereEqualTo("sqlAck", false)
            .GetSnapshotAsync();

        var list = new List<MessageFirebaseModel>();
        foreach (var doc in snapshot.Documents)
        {
            if (!doc.Exists) continue;

            var data = doc.ToDictionary();
            list.Add(new MessageFirebaseModel
            {
                Id = doc.Id,
                SqlNotificationId = data.ContainsKey("sqlNotificationId") ? data["sqlNotificationId"]?.ToString() : null,
                Status = "read"
            });
        }
        return list;
    }

    public async Task MarkMessageAsAckedAsync(string messageId)
    {
        var docRef = _db.Collection("messages").Document(messageId);
        await docRef.SetAsync(new Dictionary<string, object> { { "sqlAck", true } }, SetOptions.MergeAll);
    }

    public async Task IncrementYearlyStatsAsync(string driverId, DateTime date, string type)
    {
        if (string.IsNullOrEmpty(driverId)) return;
        
        var yearStr = date.Year.ToString();
        var monthStr = date.Month.ToString(); // ex: "1", "2" ... "12"
        
        var docRef = _db.Collection("users")
                        .Document(driverId)
                        .Collection("yearly_stats")
                        .Document(yearStr);
                        
        var updates = new Dictionary<string, object>
        {
            { $"{type}.{monthStr}", FieldValue.Increment(1) }
        };
        
        await docRef.SetAsync(updates, SetOptions.MergeAll);
    }

    public FirestoreChangeListener ListenToPendingTasks(Func<List<TaskModel>, Task> callback)
    {
        var query = _db.Collection("tasks").WhereEqualTo("needsSqlSync", true);
        return query.Listen(async snapshot =>
        {
            var list = new List<TaskModel>();
            foreach (var doc in snapshot.Documents)
            {
                if (!doc.Exists) continue;

                DateTime? statusDate = null;
                if (doc.ContainsField("statusDate"))
                    statusDate = doc.GetValue<Timestamp>("statusDate").ToDateTime();
                else if (doc.ContainsField("startedAt"))
                    statusDate = doc.GetValue<Timestamp>("startedAt").ToDateTime();
                else if (doc.ContainsField("completedAt"))
                    statusDate = doc.GetValue<Timestamp>("completedAt").ToDateTime();

                double? lat = null;
                double? lon = null;
                var status = doc.ContainsField("status") ? doc.GetValue<string>("status") : string.Empty;

                if (doc.ContainsField("startLocation"))
                {
                    var gp = doc.GetValue<GeoPoint>("startLocation");
                    lat = gp.Latitude;
                    lon = gp.Longitude;
                }
                if (doc.ContainsField("completeLocation"))
                {
                    var gp = doc.GetValue<GeoPoint>("completeLocation");
                    lat = gp.Latitude;
                    lon = gp.Longitude;
                }
                if (lat == null && doc.ContainsField("lat"))
                    lat = doc.GetValue<double>("lat");
                if (lon == null && doc.ContainsField("lon"))
                    lon = doc.GetValue<double>("lon");

                list.Add(new TaskModel
                {
                    Id = doc.Id,
                    SqlId = doc.ContainsField("sqlId") ? doc.GetValue<string>("sqlId") : string.Empty,
                    Status = status,
                    StatusDate = statusDate,
                    Lat = lat,
                    Lon = lon,
                    NeedsSqlSync = true,
                    FleetcomTaskTypeId = doc.ContainsField("fleetcomTaskTypeId") ? (int?)doc.GetValue<long>("fleetcomTaskTypeId") : null,
                    DriverId = doc.ContainsField("driverId") ? doc.GetValue<string>("driverId") : string.Empty,
                    CompletedAt = statusDate
                });
            }

            if (list.Count > 0)
            {
                await callback(list);
            }
        });
    }

    public FirestoreChangeListener ListenToPendingMessages(Func<List<MessageFirebaseModel>, Task> callback)
    {
        var query = _db.Collection("messages")
                       .WhereEqualTo("sender", "hq")
                       .WhereEqualTo("needsSqlSync", true);

        return query.Listen(async snapshot =>
        {
            var list = new List<MessageFirebaseModel>();
            foreach (var doc in snapshot.Documents)
            {
                if (!doc.Exists) continue;

                DateTime? timestamp = null;
                if (doc.ContainsField("timestamp"))
                {
                    timestamp = doc.GetValue<Timestamp>("timestamp").ToDateTime();
                }

                list.Add(new MessageFirebaseModel
                {
                    Id = doc.Id,
                    Text = doc.ContainsField("text") ? doc.GetValue<string>("text") : string.Empty,
                    Sender = doc.ContainsField("sender") ? doc.GetValue<string>("sender") : string.Empty,
                    DriverId = doc.ContainsField("driverId") ? doc.GetValue<string>("driverId") : string.Empty,
                    Status = doc.ContainsField("status") ? doc.GetValue<string>("status") : string.Empty,
                    Type = doc.ContainsField("type") ? doc.GetValue<string>("type") : string.Empty,
                    Timestamp = timestamp,
                    NeedsSqlSync = true
                });
            }

            if (list.Count > 0)
            {
                await callback(list);
            }
        });
    }

    public FirestoreChangeListener ListenToPendingAcks(Func<List<MessageFirebaseModel>, Task> callback)
    {
        var query = _db.Collection("messages")
                       .WhereEqualTo("sender", "hq")
                       .WhereEqualTo("status", "read")
                       .WhereEqualTo("sqlAck", false);

        return query.Listen(async snapshot =>
        {
            var list = new List<MessageFirebaseModel>();
            foreach (var doc in snapshot.Documents)
            {
                if (!doc.Exists) continue;

                var data = doc.ToDictionary();
                list.Add(new MessageFirebaseModel
                {
                    Id = doc.Id,
                    SqlNotificationId = data.ContainsKey("sqlNotificationId") ? data["sqlNotificationId"]?.ToString() : null,
                    Status = "read"
                });
            }

            if (list.Count > 0)
            {
                await callback(list);
            }
        });
    }

    public async Task<List<AbastecimentoFirestoreModel>> GetPendingRefuelsAsync()
    {
        var snapshot = await _db.Collection("refuels")
                                .WhereEqualTo("status", "approved")
                                .GetSnapshotAsync();
        
        var list = new List<AbastecimentoFirestoreModel>();
        foreach (var doc in snapshot.Documents)
        {
            if (!doc.Exists) continue;
            var data = doc.ToDictionary();
            
            // Se sqlSynced for true, ignoramos (já sincronizado)
            if (data.TryGetValue("sqlSynced", out var synced) && synced is bool b && b)
                continue;

            DateTime ts = DateTime.UtcNow;
            if (data.TryGetValue("timestamp", out var tsVal) && tsVal is Timestamp t)
                ts = t.ToDateTime();

            decimal lat = 0;
            decimal lon = 0;
            if (data.TryGetValue("location", out var locVal) && locVal is GeoPoint gp)
            {
                lat = (decimal)gp.Latitude;
                lon = (decimal)gp.Longitude;
            }

            decimal liters = 0;
            if (data.TryGetValue("liters", out var litVal))
            {
                if (litVal is double d) liters = (decimal)d;
                else if (litVal is long l) liters = (decimal)l;
            }

            int kms = 0;
            if (data.TryGetValue("kms", out var kmsVal))
            {
                if (kmsVal is long l) kms = (int)l;
                else if (kmsVal is string s && int.TryParse(s, out var parsedKms)) kms = parsedKms;
            }

            list.Add(new AbastecimentoFirestoreModel
            {
                Id = doc.Id,
                DriverId = data.TryGetValue("driverId", out var drId) ? drId?.ToString() ?? "" : "",
                Plate = data.TryGetValue("plate", out var pl) ? pl?.ToString() ?? "" : "",
                TrailerPlate = data.TryGetValue("trailerPlate", out var trPl) ? trPl?.ToString() ?? "" : "",
                Liters = liters,
                FuelType = data.TryGetValue("fuelType", out var fTy) ? fTy?.ToString() ?? "" : "",
                FullTank = data.TryGetValue("fullTank", out var fTa) && fTa is bool full && full,
                Notes = data.TryGetValue("notes", out var nt) ? nt?.ToString() ?? "" : "",
                ReceiptUrl = data.TryGetValue("receiptUrl", out var rUrl) ? rUrl?.ToString() ?? "" : "",
                Lat = lat,
                Lon = lon,
                Kms = kms,
                Timestamp = ts,
                Status = data.TryGetValue("status", out var st) ? st?.ToString() ?? "" : "",
                SqlSynced = false
            });
        }
        return list;
    }

    public async Task MarkRefuelAsSyncedAsync(string id)
    {
        var docRef = _db.Collection("refuels").Document(id);
        await docRef.SetAsync(new Dictionary<string, object> { { "sqlSynced", true } }, SetOptions.MergeAll);
    }

    public async Task DeleteRefuelAsync(string id)
    {
        var docRef = _db.Collection("refuels").Document(id);
        await docRef.DeleteAsync();
    }

    public async Task<List<IncidentFirestoreModel>> GetPendingIncidentsAsync()
    {
        var snapshot = await _db.Collection("incidents")
                                .WhereEqualTo("status", "approved")
                                .GetSnapshotAsync();
        
        var list = new List<IncidentFirestoreModel>();
        foreach (var doc in snapshot.Documents)
        {
            if (!doc.Exists) continue;
            var data = doc.ToDictionary();
            
            // Se sqlSynced for true, ignoramos (já sincronizado)
            if (data.TryGetValue("sqlSynced", out var synced) && synced is bool b && b)
                continue;

            DateTime ts = DateTime.UtcNow;
            if (data.TryGetValue("timestamp", out var tsVal) && tsVal is Timestamp t)
                ts = t.ToDateTime();

            decimal lat = 0;
            decimal lon = 0;
            if (data.TryGetValue("location", out var locVal) && locVal is GeoPoint gp)
            {
                lat = (decimal)gp.Latitude;
                lon = (decimal)gp.Longitude;
            }

            int kms = 0;
            if (data.TryGetValue("kms", out var kmsVal))
            {
                if (kmsVal is long l) kms = (int)l;
                else if (kmsVal is string s && int.TryParse(s, out var parsedKms)) kms = parsedKms;
            }

            var imageUrlsList = new List<string>();
            if (data.TryGetValue("imageUrls", out var imgs) && imgs is System.Collections.IEnumerable enumerable)
            {
                foreach (var img in enumerable)
                {
                    if (img != null) imageUrlsList.Add(img.ToString()!);
                }
            }

            list.Add(new IncidentFirestoreModel
            {
                Id = doc.Id,
                DriverId = data.TryGetValue("driverId", out var drId) ? drId?.ToString() ?? "" : "",
                Plate = data.TryGetValue("plate", out var pl) ? pl?.ToString() ?? "" : "",
                Description = data.TryGetValue("description", out var desc) ? desc?.ToString() ?? "" : "",
                ImageUrls = imageUrlsList,
                Lat = lat,
                Lon = lon,
                Kms = kms,
                Timestamp = ts,
                Type = data.TryGetValue("type", out var ty) ? ty?.ToString() ?? "" : "",
                CustomReason = data.TryGetValue("customReason", out var cr) ? cr?.ToString() ?? "" : "",
                Status = data.TryGetValue("status", out var st) ? st?.ToString() ?? "" : "",
                SqlSynced = false
            });
        }
        return list;
    }

    public async Task MarkIncidentAsSyncedAsync(string id)
    {
        var docRef = _db.Collection("incidents").Document(id);
        await docRef.SetAsync(new Dictionary<string, object> { { "sqlSynced", true } }, SetOptions.MergeAll);
    }

    public async Task DeleteIncidentAsync(string id)
    {
        var docRef = _db.Collection("incidents").Document(id);
        await docRef.DeleteAsync();
    }

    public async Task DeleteMessageAsync(string id)
    {
        var docRef = _db.Collection("messages").Document(id);
        await docRef.DeleteAsync();
    }

    public async Task UpdateDriverSyncErrorAsync(string driverUid, string syncError)
    {
        var docRef = _db.Collection("users").Document(driverUid);
        await docRef.SetAsync(new Dictionary<string, object> { { "syncError", syncError } }, SetOptions.MergeAll);
    }

    public async Task<int> GetUnsyncedTasksForDriverCountAsync(string driverUid, string sqlDriverId)
    {
        var searchIds = new List<string> { driverUid };
        if (!string.IsNullOrEmpty(sqlDriverId)) searchIds.Add(sqlDriverId);

        int totalCount = 0;
        foreach (var id in searchIds)
        {
            var snap = await _db.Collection("tasks")
                                .WhereEqualTo("driverId", id)
                                .WhereEqualTo("needsSqlSync", true)
                                .GetSnapshotAsync();
            totalCount += snap.Count;
        }
        return totalCount;
    }

    public async Task<int> GetUnsyncedMessagesForDriverCountAsync(string driverUid)
    {
        var snap = await _db.Collection("messages")
                            .WhereEqualTo("driverId", driverUid)
                            .WhereEqualTo("needsSqlSync", true)
                            .GetSnapshotAsync();
        return snap.Count;
    }

    public async Task<int> GetUnsyncedRefuelsForDriverCountAsync(string driverUid)
    {
        var snap = await _db.Collection("refuels")
                            .WhereEqualTo("driverId", driverUid)
                            .WhereEqualTo("status", "approved")
                            .GetSnapshotAsync();
        
        int count = 0;
        foreach (var doc in snap.Documents)
        {
            if (doc.Exists && (!doc.ContainsField("sqlSynced") || doc.GetValue<bool>("sqlSynced") != true))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<int> GetUnsyncedIncidentsForDriverCountAsync(string driverUid)
    {
        var snap = await _db.Collection("incidents")
                            .WhereEqualTo("driverId", driverUid)
                            .WhereEqualTo("status", "approved")
                            .GetSnapshotAsync();
        
        int count = 0;
        foreach (var doc in snap.Documents)
        {
            if (doc.Exists && (!doc.ContainsField("sqlSynced") || doc.GetValue<bool>("sqlSynced") != true))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<string> GetLastCleanupDateAsync()
    {
        var docRef = _db.Collection("config").Document("cleanup");
        var snap = await docRef.GetSnapshotAsync();
        if (snap.Exists && snap.ContainsField("lastRunDate"))
        {
            return snap.GetValue<string>("lastRunDate") ?? "";
        }
        return "";
    }

    public async Task SetLastCleanupDateAsync(string dateStr)
    {
        var docRef = _db.Collection("config").Document("cleanup");
        await docRef.SetAsync(new Dictionary<string, object> { { "lastRunDate", dateStr } }, SetOptions.MergeAll);
    }

    public async Task<List<TaskModel>> GetSyncedTasksForDriverAsync(string driverUid, string sqlDriverId)
    {
        var searchIds = new List<string> { driverUid };
        if (!string.IsNullOrEmpty(sqlDriverId)) searchIds.Add(sqlDriverId);

        var list = new List<TaskModel>();
        foreach (var id in searchIds)
        {
            var snap = await _db.Collection("tasks")
                               .WhereEqualTo("driverId", id)
                               .GetSnapshotAsync();
           
            foreach (var doc in snap.Documents)
            {
                if (!doc.Exists) continue;
                var status = doc.ContainsField("status") ? doc.GetValue<string>("status") : "";
                var needsSqlSync = doc.ContainsField("needsSqlSync") && doc.GetValue<bool>("needsSqlSync");
                var sqlId = doc.ContainsField("sqlId") ? doc.GetValue<string>("sqlId") : "";
               
                var isCompleted = status == "completed" || status == "terminada" || status == "anulada";
               
                if (isCompleted && !needsSqlSync && !string.IsNullOrEmpty(sqlId))
                {
                    DateTime? completedAt = null;
                    if (doc.ContainsField("completedAt")) completedAt = doc.GetValue<Timestamp>("completedAt").ToDateTime();
                    else if (doc.ContainsField("date")) completedAt = doc.GetValue<Timestamp>("date").ToDateTime();
                    else if (doc.ContainsField("timestamp")) completedAt = doc.GetValue<Timestamp>("timestamp").ToDateTime();

                    list.Add(new TaskModel
                    {
                        Id = doc.Id,
                        SqlId = sqlId,
                        Status = status,
                        CompletedAt = completedAt,
                        StatusDate = completedAt
                    });
                }
            }
        }
        return list;
    }

    public async Task<List<AbastecimentoFirestoreModel>> GetSyncedRefuelsForDriverAsync(string driverUid)
    {
        var snap = await _db.Collection("refuels")
                            .WhereEqualTo("driverId", driverUid)
                            .WhereEqualTo("status", "approved")
                            .GetSnapshotAsync();
       
        var list = new List<AbastecimentoFirestoreModel>();
        foreach (var doc in snap.Documents)
        {
            if (doc.Exists && doc.ContainsField("sqlSynced") && doc.GetValue<bool>("sqlSynced") == true)
            {
                DateTime timestamp = DateTime.UtcNow;
                if (doc.ContainsField("timestamp")) timestamp = doc.GetValue<Timestamp>("timestamp").ToDateTime();

                list.Add(new AbastecimentoFirestoreModel
                {
                    Id = doc.Id,
                    DriverId = driverUid,
                    Timestamp = timestamp
                });
            }
        }
        return list;
    }

    public async Task<List<IncidentFirestoreModel>> GetSyncedIncidentsForDriverAsync(string driverUid)
    {
        var snap = await _db.Collection("incidents")
                            .WhereEqualTo("driverId", driverUid)
                            .WhereEqualTo("status", "approved")
                            .GetSnapshotAsync();
       
        var list = new List<IncidentFirestoreModel>();
        foreach (var doc in snap.Documents)
        {
            if (doc.Exists && doc.ContainsField("sqlSynced") && doc.GetValue<bool>("sqlSynced") == true)
            {
                DateTime timestamp = DateTime.UtcNow;
                if (doc.ContainsField("timestamp")) timestamp = doc.GetValue<Timestamp>("timestamp").ToDateTime();

                list.Add(new IncidentFirestoreModel
                {
                    Id = doc.Id,
                    DriverId = driverUid,
                    Timestamp = timestamp
                });
            }
        }
        return list;
    }

    public async Task<List<MessageFirebaseModel>> GetSyncedMessagesForDriverAsync(string driverUid)
    {
        var snap = await _db.Collection("messages")
                            .WhereEqualTo("driverId", driverUid)
                            .GetSnapshotAsync();
       
        var list = new List<MessageFirebaseModel>();
        foreach (var doc in snap.Documents)
        {
            if (!doc.Exists) continue;
            var needsSqlSync = doc.ContainsField("needsSqlSync") && doc.GetValue<bool>("needsSqlSync");
            var sqlNotificationId = doc.ContainsField("sqlNotificationId") ? doc.GetValue<string>("sqlNotificationId") : "";
           
            if (!needsSqlSync && !string.IsNullOrEmpty(sqlNotificationId))
            {
                list.Add(new MessageFirebaseModel
                {
                    Id = doc.Id,
                    DriverId = driverUid
                });
            }
        }
        return list;
    }
}
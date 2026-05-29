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
}
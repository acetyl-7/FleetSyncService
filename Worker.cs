using System.Linq;
using FleetSyncService.Services;
using FleetSyncService.Models;

namespace FleetSyncService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IFirebaseService _firebaseService;
    private readonly ISqlService _sqlService;
    
    // Controlo de tempo para não correr a sincronização de utilizadores a cada 10 segundos
    private DateTime _lastUserSyncTime = DateTime.MinValue;
    private readonly TimeSpan _userSyncInterval = TimeSpan.FromMinutes(5);

    // Cache para evitar envios redundantes SQL -> Firebase
    private readonly Dictionary<Guid, string> _taskHashCache = new();

    // Controlo de tempo para limpar do Firebase as tarefas que foram apagadas ou concluídas no SQL
    private DateTime _lastTaskCleanupTime = DateTime.MinValue;
    private readonly TimeSpan _taskCleanupInterval = TimeSpan.FromMinutes(5);

    public Worker(ILogger<Worker> logger, IFirebaseService firebaseService, ISqlService sqlService)
    {
        _logger = logger;
        _firebaseService = firebaseService;
        _sqlService = sqlService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_firebaseService.IsEnabled)
                {
                    _logger.LogWarning("Firebase synchronization is disabled due to missing credentials. The sync cycle will be skipped. To fix this, place 'service-account.json' in the project root or configure 'CredentialsFilePath' in appsettings.json.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                _logger.LogInformation("Sync Service a correr");

                // 0. Sincronização: Firebase (Firestore) -> SQL (dbo.driver)
                // Corre apenas de X em X minutos para poupar recursos, já que os motoristas não mudam a toda a hora
                if (DateTime.UtcNow - _lastUserSyncTime >= _userSyncInterval)
                {
                    _logger.LogInformation("A verificar novos utilizadores no Firebase...");
                    var fbUsers = await _firebaseService.GetAllFirebaseUsersAsync();
                    int syncedCount = 0;

                    foreach (var fbUser in fbUsers)
                    {
                        if (!string.IsNullOrEmpty(fbUser.Phone))
                        {
                            var motorista = await _sqlService.ValidateCompanyPhoneAsync(fbUser.Phone);
                            if (motorista != null)
                            {
                                await _sqlService.RegisterDriverAsync(
                                    fbUser.Uid, 
                                    fbUser.Email, 
                                    motorista.Telemovel, 
                                    motorista.Alcunha, 
                                    motorista.Id
                                );
                                // Garante que o documento do utilizador no Firebase tem o driverId para a App Móvel conseguir ler as tarefas
                                await _firebaseService.UpdateDriverIdAsync(fbUser.Uid, motorista.Id.ToString());
                                
                                syncedCount++;
                            }
                        }
                    }
                    _logger.LogInformation("Sincronização concluída: {Count} motoristas processados para SQL.", syncedCount);
                    _lastUserSyncTime = DateTime.UtcNow;
                }

                // Sincronização Inversa (Firebase -> SQL) via Delta Sync
                var pendingTasks = await _firebaseService.GetTasksPendingSqlSyncAsync();
                _logger.LogInformation("Tarefas pendentes de sync Firebase->SQL: {Count}", pendingTasks.Count);
                foreach (var task in pendingTasks)
                {
                    _logger.LogInformation("A processar tarefa pendente: Firebase={Id}, SqlId={SqlId}, Status={Status}", task.Id, task.SqlId, task.Status);
                    var success = await _sqlService.ExecuteTaskStatusProcedureAsync(task);
                    if (success)
                    {
                        // Se a tarefa está num estado final (concluída/anulada), incrementamos estatísticas e eliminamos
                        if (task.Status == "terminada" || task.Status == "completed" || task.Status == "anulada")
                        {
                            await _firebaseService.IncrementYearlyStatsAsync(task.DriverId, task.CompletedAt ?? DateTime.UtcNow, "tasks");
                            await _firebaseService.DeleteTaskAsync(task.Id);
                            _logger.LogInformation("Tarefa {TaskId} concluída e removida do Firebase após sync.", task.Id);
                        }
                        else
                        {
                            await _firebaseService.MarkTaskAsSyncedAsync(task.Id);
                            _logger.LogInformation("Tarefa {TaskId} sincronizada com sucesso para o SQL Server.", task.Id);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Falha ao sincronizar tarefa {TaskId} para o SQL Server.", task.Id);
                    }
                }

                _logger.LogInformation("A aguardar que a base de dados processe os logs antes de iniciar sync SQL -> Firebase...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                // Sincronização de Tarefas (SQL -> Firebase)
                var activeTasks = await _sqlService.GetActiveTasksAsync();
                
                _logger.LogInformation("SQL devolveu {Count} tarefas ativas.", activeTasks.Count());
                foreach(var t in activeTasks)
                {
                    _logger.LogInformation("Ativa no SQL: {Id} com estado {Status}", t.Id, t.Status);
                }

                int updatedTasksCount = 0;
                foreach (var task in activeTasks)
                {
                    // Gera um hash simples com os campos relevantes para saber se mudou no SQL
                    var currentHash = $"{task.Status}_{task.TractorPlate}_{task.TrailerPlate}_{task.FleetcomDriverId}_{task.City}_{task.Address}";
                    
                    if (!_taskHashCache.TryGetValue(task.Id, out var previousHash) || previousHash != currentHash)
                    {
                        await _firebaseService.UpsertTaskAsync(task);
                        _taskHashCache[task.Id] = currentHash;
                        updatedTasksCount++;

                        // Se a tarefa acabou de ser enviada para o Firebase e estava no estado "1" (Por Enviar),
                        // atualizamos de imediato o estado para "10" (Enviada) no SQL para refletir que já chegou ao motorista.
                        if (task.Status == "1" || task.Status == "0")
                        {
                            var updateModel = new TaskModel
                            {
                                Id = task.Id.ToString(),
                                SqlId = task.Id.ToString(),
                                Status = "enviada", // SqlService traduz para 10
                                StatusDate = DateTime.UtcNow
                            };
                            
                            var success = await _sqlService.ExecuteTaskStatusProcedureAsync(updateModel);
                            if (success)
                            {
                                _logger.LogInformation("Estado da tarefa {TaskId} atualizado para Enviada (10) no SQL automaticamente.", task.Id);
                            }
                        }
                    }
                }
                if (updatedTasksCount > 0)
                {
                    _logger.LogInformation("Sincronizadas {Count} tarefas modificadas às {Time}", updatedTasksCount, DateTimeOffset.UtcNow);
                }

                // Sincronização de Mensagens enviadas pela Sede no Backoffice (SQL -> Firebase)
                var pendingSqlNotifications = await _sqlService.GetPendingNotificationsAsync();
                int sentCount = 0;
                foreach (var notif in pendingSqlNotifications)
                {
                    if (string.IsNullOrEmpty(notif.FirebaseUid)) continue;

                    var fbMsg = new MessageFirebaseModel
                    {
                        Id = notif.Id.ToString(), // usar o ID do SQL como ID do documento no Firebase para evitar duplicados e permitir ack
                        Text = notif.Body ?? "",
                        Sender = "hq",
                        DriverId = notif.FirebaseUid,
                        Status = "sent",
                        Type = "text",
                        Timestamp = notif.Date ?? DateTime.UtcNow,
                        SqlNotificationId = notif.Id.ToString(),
                        NeedsSqlSync = false // já veio do SQL, não precisa voltar
                    };

                    await _firebaseService.UpsertMessageAsync(fbMsg);
                    await _sqlService.MarkNotificationAsSentAsync(notif.Id);
                    sentCount++;
                }
                
                if (sentCount > 0)
                {
                    _logger.LogInformation("Enviadas {Count} notificações do SQL para o Firebase.", sentCount);
                }

                // Sincronização de Mensagens enviadas pela Sede (Firebase -> SQL dbo.notification)
                var pendingMessages = await _firebaseService.GetMessagesPendingSqlSyncAsync();
                if (pendingMessages.Any())
                {
                    _logger.LogInformation("Mensagens pendentes de inserção no SQL: {Count}", pendingMessages.Count);
                    foreach (var msg in pendingMessages)
                    {
                        var driverId = await _sqlService.GetDriverIdByFirebaseUidAsync(msg.DriverId);
                        if (driverId.HasValue && driverId.Value > 0)
                        {
                            string sqlId = await _sqlService.InsertNotificationAsync(driverId.Value, msg);
                            await _firebaseService.MarkMessageAsSyncedAsync(msg.Id, sqlId);
                        }
                        else
                        {
                            _logger.LogWarning("Não foi possível encontrar o motorista no SQL para o Firebase Uid {Uid}", msg.DriverId);
                        }
                    }
                }

                // Sincronização de estado de leitura das mensagens (App Motorista -> SQL ack=1)
                var readMessages = await _firebaseService.GetMessagesPendingAckSyncAsync();
                if (readMessages.Any())
                {
                    foreach (var msg in readMessages)
                    {
                        if (!string.IsNullOrEmpty(msg.SqlNotificationId))
                        {
                            await _sqlService.UpdateNotificationAckAsync(msg.SqlNotificationId, DateTime.UtcNow);
                            await _firebaseService.MarkMessageAsAckedAsync(msg.Id);
                        }
                    }
                }

                // Limpeza de tarefas apagadas ou concluídas no SQL que ainda estão ativas no Firebase
                if (DateTime.UtcNow - _lastTaskCleanupTime >= _taskCleanupInterval)
                {
                    _logger.LogInformation("A verificar tarefas apagadas/concluídas para limpar do Firebase...");
                    var activeFirebaseTasks = await _firebaseService.GetActiveFirebaseTasksAsync();
                    
                    var activeSqlTaskIds = activeTasks.Select(t => t.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    int removedCount = 0;

                    foreach(var fbTask in activeFirebaseTasks)
                    {
                        _logger.LogInformation("Avaliar FirebaseTask {Id}: SqlId='{SqlId}', NeedsSqlSync={NeedsSync}, Contains={Contains}", fbTask.Id, fbTask.SqlId, fbTask.NeedsSqlSync, activeSqlTaskIds.Contains(fbTask.SqlId));
                        
                        // Se a tarefa no Firebase diz que está pendente de sync, não a apagamos
                        if (fbTask.NeedsSqlSync) continue; 
                        
                        // Se a tarefa já está terminada ou anulada no Firebase, não a apagamos!
                        if (fbTask.Status == "terminada" || fbTask.Status == "completed" || fbTask.Status == "anulada") continue;

                        // Se a tarefa não tem SqlId (criada no Firebase/Backoffice diretamente), não a apagamos
                        if (string.IsNullOrEmpty(fbTask.SqlId)) continue;

                        // Se a tarefa existe no Firebase (e não está terminada nem anulada), mas já não vem nas activeTasks do SQL,
                        // significa que foi apagada (deleted=1) ou o seu estado avançou no SQL (>=80) via Backoffice.
                        if (!activeSqlTaskIds.Contains(fbTask.SqlId))
                        {
                            var sqlTask = await _sqlService.GetTaskByIdAsync(fbTask.SqlId);
                            if (sqlTask == null || sqlTask.Deleted)
                            {
                                await _firebaseService.DeleteTaskAsync(fbTask.Id);
                                removedCount++;
                                _logger.LogInformation("Tarefa {TaskId} (SqlId: {SqlId}) removida do Firebase porque foi eliminada/não existe no SQL.", fbTask.Id, fbTask.SqlId);
                            }
                            else if (sqlTask.Status == "80" || sqlTask.Status == "90")
                            {
                                // O estado avançou no SQL (concluída/anulada). Em vez de apagar do Firebase,
                                // atualizamos o estado no Firebase para manter o histórico (trips/past tasks).
                                await _firebaseService.UpsertTaskAsync(sqlTask);
                                _logger.LogInformation("Tarefa {TaskId} (SqlId: {SqlId}) atualizada para estado final no Firebase ({Status}) em vez de ser removida.", fbTask.Id, fbTask.SqlId, sqlTask.Status);
                            }
                            else
                            {
                                await _firebaseService.DeleteTaskAsync(fbTask.Id);
                                removedCount++;
                                _logger.LogInformation("Tarefa {TaskId} (SqlId: {SqlId}) removida do Firebase por inconsistência de estado no SQL.", fbTask.Id, fbTask.SqlId);
                            }
                        }
                    }
                    if (removedCount > 0)
                    {
                        _logger.LogInformation("Limpeza concluída. {Count} tarefas obsoletas removidas do Firebase.", removedCount);
                    }
                    else
                    {
                        _logger.LogInformation("Limpeza concluída. Nenhuma tarefa obsoleta encontrada.");
                    }
                    _lastTaskCleanupTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o ciclo de sincronização.");
            }

            // Sync every 10 seconds for tasks
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

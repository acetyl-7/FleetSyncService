using System.Linq;
using System.Net.Http;
using FleetSyncService.Services;
using FleetSyncService.Models;
using Google.Cloud.Firestore;

namespace FleetSyncService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IFirebaseService _firebaseService;
    private readonly ISqlService _sqlService;
    
    // Listeners em tempo real para o Firebase
    private FirestoreChangeListener? _tasksListener;
    private FirestoreChangeListener? _messagesListener;
    private FirestoreChangeListener? _acksListener;
    
    // Controlo de tempo para não correr a sincronização de utilizadores a cada 10 segundos
    private DateTime _lastUserSyncTime = DateTime.MinValue;
    private readonly TimeSpan _userSyncInterval = TimeSpan.FromMinutes(5);

    // Cache para evitar envios redundantes SQL -> Firebase
    private readonly Dictionary<Guid, string> _taskHashCache = new();

    // Controlo de tempo para limpar do Firebase as tarefas que foram apagadas ou concluídas no SQL
    private DateTime _lastTaskCleanupTime = DateTime.MinValue;
    private readonly TimeSpan _taskCleanupInterval = TimeSpan.FromMinutes(5);

    // Controlo de tempo para recalcular erros de sincronização (syncError) para os motoristas
    private DateTime _lastSyncErrorCalculationTime = DateTime.MinValue;
    private readonly TimeSpan _syncErrorCalculationInterval = TimeSpan.FromMinutes(5);

    public Worker(ILogger<Worker> logger, IFirebaseService firebaseService, ISqlService sqlService)
    {
        _logger = logger;
        _firebaseService = firebaseService;
        _sqlService = sqlService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Evita bloquear o Windows Service Control Manager (SCM) durante o arranque inicial

        if (_firebaseService.IsEnabled)
        {
            _logger.LogInformation("A inicializar os Real-Time Listeners do Firebase...");
            try
            {
                _tasksListener = _firebaseService.ListenToPendingTasks(OnPendingTasksReceivedAsync);
                _messagesListener = _firebaseService.ListenToPendingMessages(OnPendingMessagesReceivedAsync);
                _acksListener = _firebaseService.ListenToPendingAcks(OnPendingAcksReceivedAsync);
                _logger.LogInformation("Real-Time Listeners do Firebase ativados com sucesso.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao inicializar os Real-Time Listeners do Firebase. O serviço continuará.");
            }
        }

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

                // A. Sincronização de Abastecimentos (Firebase -> SQL)
                _logger.LogInformation("A verificar abastecimentos pendentes de sincronização...");
                try
                {
                    var pendingRefuels = await _firebaseService.GetPendingRefuelsAsync();
                    foreach (var refuel in pendingRefuels)
                    {
                        var driverIdVal = await _sqlService.GetDriverIdByFirebaseUidAsync(refuel.DriverId);
                        if (driverIdVal.HasValue && driverIdVal.Value > 0)
                        {
                            byte[]? imageBytes = null;
                            if (!string.IsNullOrEmpty(refuel.ReceiptUrl))
                            {
                                try
                                {
                                    using var httpClient = new HttpClient();
                                    imageBytes = await httpClient.GetByteArrayAsync(refuel.ReceiptUrl);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Erro ao descarregar imagem de abastecimento: {Url}", refuel.ReceiptUrl);
                                }
                            }

                            var model = new AbastecimentoModel
                            {
                                DtReal = refuel.Timestamp,
                                DtUser = refuel.Timestamp,
                                MobileDriverId = driverIdVal.Value.ToString(),
                                Lat = refuel.Lat,
                                Lon = refuel.Lon,
                                Kms = refuel.Kms,
                                Litros = refuel.Liters,
                                MatTractor = refuel.Plate,
                                MatReboque = refuel.TrailerPlate,
                                TipoCartao = "",
                                Nota = refuel.Notes,
                                Imagem = imageBytes,
                                TipoProd = refuel.FuelType,
                                Atesto = refuel.FullTank
                            };

                            var success = await _sqlService.ExecuteProcessaAbastecimentoProcedureAsync(model);
                            if (success)
                            {
                                await _firebaseService.MarkRefuelAsSyncedAsync(refuel.Id);
                                _logger.LogInformation("Abastecimento {Id} sincronizado com sucesso para o SQL Server.", refuel.Id);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar sincronização de abastecimentos.");
                }

                // B. Sincronização de Incidentes (Firebase -> SQL)
                _logger.LogInformation("A verificar incidentes pendentes de sincronização...");
                try
                {
                    var pendingIncidents = await _firebaseService.GetPendingIncidentsAsync();
                    foreach (var incident in pendingIncidents)
                    {
                        var driverIdVal = await _sqlService.GetDriverIdByFirebaseUidAsync(incident.DriverId);
                        if (driverIdVal.HasValue && driverIdVal.Value > 0)
                        {
                            var model = new IncidentModel
                            {
                                DtIncidente = incident.Timestamp,
                                DtUser = incident.Timestamp,
                                MobileDriverId = driverIdVal.Value.ToString(),
                                Lat = incident.Lat,
                                Lon = incident.Lon,
                                Kms = incident.Kms,
                                MatTractor = incident.Plate,
                                MatReboque = "",
                                ImageIds = string.Join(",", incident.ImageUrls),
                                Descricao = incident.Description,
                                Tipo = incident.Type,
                                RazaoCustom = incident.CustomReason
                            };

                            var success = await _sqlService.ExecuteProcessaIncidenteProcedureAsync(model);
                            if (success)
                            {
                                await _firebaseService.MarkIncidentAsSyncedAsync(incident.Id);
                                _logger.LogInformation("Incidente {Id} sincronizado com sucesso para o SQL Server.", incident.Id);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar sincronização de incidentes.");
                }

                // C. Verificação de Erros de Sincronização (syncError) - Otimizado para correr a cada 5 minutos
                if (DateTime.UtcNow - _lastSyncErrorCalculationTime >= _syncErrorCalculationInterval)
                {
                    _logger.LogInformation("A recalcular estado de sincronização (syncError) dos motoristas...");
                    try
                    {
                        var fbUsers = await _firebaseService.GetAuthorizedDriversAsync();
                        foreach (var user in fbUsers)
                        {
                            var errors = new List<string>();

                            var unsyncedTasks = await _firebaseService.GetUnsyncedTasksForDriverCountAsync(user.Uid, user.SqlId);
                            if (unsyncedTasks > 0) errors.Add("tarefas");

                            var unsyncedMessages = await _firebaseService.GetUnsyncedMessagesForDriverCountAsync(user.Uid);
                            if (unsyncedMessages > 0) errors.Add("mensagens");

                            var unsyncedRefuels = await _firebaseService.GetUnsyncedRefuelsForDriverCountAsync(user.Uid);
                            if (unsyncedRefuels > 0) errors.Add("abastecimento");

                            var unsyncedIncidents = await _firebaseService.GetUnsyncedIncidentsForDriverCountAsync(user.Uid);
                            if (unsyncedIncidents > 0) errors.Add("incidente");

                            string syncErrorText = "All in sync";
                            if (errors.Count > 0)
                            {
                                syncErrorText = "Falta sincronizar " + string.Join(", ", errors);
                            }

                            // Apenas atualiza o Firebase se o estado do erro tiver mudado
                            if (user.SyncError != syncErrorText)
                            {
                                await _firebaseService.UpdateDriverSyncErrorAsync(user.Uid, syncErrorText);
                                user.SyncError = syncErrorText; // Atualiza em memória
                                _logger.LogInformation("Estado de sincronização do motorista {NickName} atualizado para: {Status}", user.Nickname, syncErrorText);
                            }
                        }
                        _lastSyncErrorCalculationTime = DateTime.UtcNow;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro ao atualizar estado de sincronização dos motoristas.");
                    }
                }

                // D. Cleanup semanal de segundas-feiras às 00:00 UTC
                if (DateTime.UtcNow.DayOfWeek == DayOfWeek.Monday && DateTime.UtcNow.Hour == 0)
                {
                    try
                    {
                        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
                        var lastRun = await _firebaseService.GetLastCleanupDateAsync();
                        
                        if (lastRun != todayStr)
                        {
                            _logger.LogInformation("A iniciar o cleanup semanal das segundas-feiras...");
                            var fbUsers = await _firebaseService.GetAuthorizedDriversAsync();
                            foreach (var user in fbUsers)
                            {
                                // 1. Limpar tarefas
                                var tasksToClean = await _firebaseService.GetSyncedTasksForDriverAsync(user.Uid, user.SqlId);
                                foreach (var task in tasksToClean)
                                {
                                    var completionDate = task.CompletedAt ?? task.StatusDate ?? DateTime.UtcNow;
                                    await _firebaseService.IncrementYearlyStatsAsync(user.Uid, completionDate, "tasks");
                                    await _firebaseService.DeleteTaskAsync(task.Id);
                                }

                                // 2. Limpar refuels
                                var refuelsToClean = await _firebaseService.GetSyncedRefuelsForDriverAsync(user.Uid);
                                foreach (var refuel in refuelsToClean)
                                {
                                    await _firebaseService.IncrementYearlyStatsAsync(user.Uid, refuel.Timestamp, "refuels");
                                    await _firebaseService.DeleteRefuelAsync(refuel.Id);
                                }

                                // 3. Limpar incidents
                                var incidentsToClean = await _firebaseService.GetSyncedIncidentsForDriverAsync(user.Uid);
                                foreach (var incident in incidentsToClean)
                                {
                                    await _firebaseService.IncrementYearlyStatsAsync(user.Uid, incident.Timestamp, "incidents");
                                    await _firebaseService.DeleteIncidentAsync(incident.Id);
                                }

                                // 4. Limpar messages
                                var messagesToClean = await _firebaseService.GetSyncedMessagesForDriverAsync(user.Uid);
                                foreach (var message in messagesToClean)
                                {
                                    await _firebaseService.DeleteMessageAsync(message.Id);
                                }
                            }
                            
                            await _firebaseService.SetLastCleanupDateAsync(todayStr);
                            _logger.LogInformation("Cleanup semanal concluído com sucesso!");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Erro no ciclo de cleanup semanal.");
                    }
                }

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

                    // Sincronização SQL → Firebase: Enviar todos os motoristas do SQL para o Firebase
                    // para que a app móvel possa validar telemóveis diretamente no Firestore
                    var sqlDrivers = await _sqlService.GetActiveDriversAsync();
                    int driversSynced = 0;
                    foreach (var driver in sqlDrivers)
                    {
                        await _firebaseService.UpsertCompanyDriverAsync(driver);
                        driversSynced++;
                    }
                    if (driversSynced > 0)
                    {
                        _logger.LogInformation("Sincronizados {Count} motoristas do SQL para o Firebase.", driversSynced);
                    }

                    _lastUserSyncTime = DateTime.UtcNow;
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
            catch (OperationCanceledException)
            {
                _logger.LogInformation("O ciclo de sincronização foi cancelado (serviço a parar).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o ciclo de sincronização.");
            }

            try
            {
                // Sync every 30 seconds for SQL->Firebase tasks (highly optimized and completely cost-free on Firebase Reads)
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Cancelamento normal durante a paragem do serviço, sairá do ciclo naturalmente
            }
        }

        _logger.LogInformation("A parar os Real-Time Listeners do Firebase...");
        try
        {
            if (_tasksListener != null) await _tasksListener.StopAsync();
            if (_messagesListener != null) await _messagesListener.StopAsync();
            if (_acksListener != null) await _acksListener.StopAsync();
            _logger.LogInformation("Listeners do Firebase parados com sucesso.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar os Listeners do Firebase.");
        }
    }

    private async Task OnPendingTasksReceivedAsync(List<TaskModel> pendingTasks)
    {
        _logger.LogInformation("[Listener] Recebidas {Count} tarefas pendentes de sincronização Firebase->SQL.", pendingTasks.Count);
        foreach (var task in pendingTasks)
        {
            try
            {
                _logger.LogInformation("[Listener] A processar tarefa pendente: Firebase={Id}, SqlId={SqlId}, Status={Status}", task.Id, task.SqlId, task.Status);
                var success = await _sqlService.ExecuteTaskStatusProcedureAsync(task);
                if (success)
                {
                    // Apenas marcar a tarefa como sincronizada para o SQL, mantendo o documento no Firebase
                    // para que o Backoffice consiga exibir o histórico e as imagens associadas.
                    await _firebaseService.MarkTaskAsSyncedAsync(task.Id);
                    _logger.LogInformation("[Listener] Tarefa {TaskId} sincronizada com sucesso para o SQL Server (Estado: {Status}).", task.Id, task.Status);
                }
                else
                {
                    _logger.LogWarning("[Listener] Falha ao sincronizar tarefa {TaskId} para o SQL Server.", task.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Listener] Erro ao processar tarefa {TaskId} no callback.", task.Id);
            }
        }
    }

    private async Task OnPendingMessagesReceivedAsync(List<MessageFirebaseModel> pendingMessages)
    {
        _logger.LogInformation("[Listener] Recebidas {Count} mensagens pendentes de sincronização para o SQL.", pendingMessages.Count);
        foreach (var msg in pendingMessages)
        {
            try
            {
                var driverId = await _sqlService.GetDriverIdByFirebaseUidAsync(msg.DriverId);
                if (driverId.HasValue && driverId.Value > 0)
                {
                    string sqlId = await _sqlService.InsertNotificationAsync(driverId.Value, msg);
                    await _firebaseService.MarkMessageAsSyncedAsync(msg.Id, sqlId);
                    _logger.LogInformation("[Listener] Mensagem {MessageId} sincronizada para o SQL com ID {SqlId}.", msg.Id, sqlId);
                }
                else
                {
                    _logger.LogWarning("[Listener] Não foi possível encontrar o motorista no SQL para o Firebase Uid {Uid}", msg.DriverId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Listener] Erro ao sincronizar mensagem {MessageId} para o SQL.", msg.Id);
            }
        }
    }

    private async Task OnPendingAcksReceivedAsync(List<MessageFirebaseModel> readMessages)
    {
        _logger.LogInformation("[Listener] Recebidas {Count} confirmações de leitura (ACK) pendentes de sincronização para o SQL.", readMessages.Count);
        foreach (var msg in readMessages)
        {
            try
            {
                if (!string.IsNullOrEmpty(msg.SqlNotificationId))
                {
                    await _sqlService.UpdateNotificationAckAsync(msg.SqlNotificationId, DateTime.UtcNow);
                    await _firebaseService.MarkMessageAsAckedAsync(msg.Id);
                    _logger.LogInformation("[Listener] Confirmação de leitura da notificação {SqlNotificationId} sincronizada no SQL.", msg.SqlNotificationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Listener] Erro ao sincronizar confirmação de leitura da mensagem {MessageId}.", msg.Id);
            }
        }
    }
}

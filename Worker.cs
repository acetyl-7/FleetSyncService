using System.Linq;
using FleetSyncService.Services;

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
                        await _firebaseService.MarkTaskAsSyncedAsync(task.Id);
                        _logger.LogInformation("Tarefa {TaskId} sincronizada com sucesso para o SQL Server.", task.Id);
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
                    }
                }
                if (updatedTasksCount > 0)
                {
                    _logger.LogInformation("Sincronizadas {Count} tarefas modificadas às {Time}", updatedTasksCount, DateTimeOffset.UtcNow);
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

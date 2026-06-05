COMO ALTERNAR AMBIENTES (CASA VS CISTERPOR)

1. NO BACKEND (FleetSyncService)
O backend C# alterna automaticamente conforme o local onde e executado:
- Em Casa: Quando corre localmente pelo Visual Studio ou "dotnet run", assume o modo "Development" e liga-se a base de dados localhost e ao Firebase de testes (logichat).
- Na Cisterpor: Quando corre em producao, assume o modo "Production" e liga-se a base de dados local da sede (192.168.1.5) e ao Firebase da Cisterpor.

Se precisares de forcar a alteracao manual das definicoes no appsettings.json do backend, podes usar o script na pasta do backend:
Abra a consola PowerShell e execute:
Para Casa:
.\switch-db.ps1 localhost

Para Cisterpor:
.\switch-db.ps1 cisterpor


2. NAS APLICACOES FLUTTER (chat_logistica e backoffice_logistica)
Como o Flutter nao tem override automatico de ficheiros de configuracao, criamos um script para mudar os ficheiros de ligacao ao Firebase:
Abra a consola PowerShell na raiz de cada projeto e execute:

Para mudar para o ambiente de Casa (Logichat):
.\switch-env.ps1 logichat

Para mudar para o ambiente da Sede (Cisterpor):
.\switch-env.ps1 cisterpor

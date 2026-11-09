# psiapoio-api

API .NET 10 do PsiApoio. É a dona do Postgres e emite o JWT; o serviço Python do agente (`psiapoio-agent`) só é chamado por ela (ver ADR 0003 do workspace).

## Rodar

1. Guardar a connection string do Postgres nos user-secrets (nunca em `appsettings*.json`):

   ```bash
   cd src/PsiApoio.Api
   dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=SEU_BANCO;Username=SEU_USUARIO;Password=SUA_SENHA"
   ```

2. Subir:

   ```bash
   dotnet run --project src/PsiApoio.Api
   ```

3. Conferir: `GET /health` (processo) e `GET /health/ready` (processo + Postgres).

## Configuração

| Chave | Onde | Descrição |
|---|---|---|
| `ConnectionStrings:Postgres` | user-secrets | Banco do projeto |
| `Agent:BaseUrl` | appsettings | URL do serviço Python (padrão `http://localhost:8000`) |
| `Agent:ApiKey` | user-secrets | Chave compartilhada com o serviço Python (a definir) |

## Estado

Esqueleto: só `/health` e `/health/ready`. Pacotes já referenciados para as próximas fatias: Npgsql + EF Core, ASP.NET Identity, JwtBearer.

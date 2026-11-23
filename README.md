# psiapoio-api

API .NET 10 do **PsiApoio**: autenticação, pacientes, perfil do agente, conversa, alertas, resumo e pauta. É a **única dona do Postgres** e a única que chama o serviço do agente. Parte do projeto [PsiApoio](https://github.com/danielbmendprofissional/psiapoio).

| Repositório | Papel |
|---|---|
| [psiapoio](https://github.com/danielbmendprofissional/psiapoio) | Vitrine e visão geral |
| [psiapoio-workspace](https://github.com/danielbmendprofissional/psiapoio-workspace) | Specs, ADRs, glossário |
| **psiapoio-api** (este) | API .NET |
| [psiapoio-frontend](https://github.com/danielbmendprofissional/psiapoio-frontend) | Front Angular |
| [psiapoio-agent](https://github.com/danielbmendprofissional/psiapoio-agent) | Serviço Python do agente |

## O que faz

- **Sessão:** ASP.NET Identity, JWT (só `sub` e papel) entregue em **cookie httpOnly** `SameSite=Strict`, 60 minutos, sem refresh (ADR 0006).
- **Papéis:** `Psicologo` e `Paciente`. O psicólogo só enxerga os próprios pacientes (outro psicólogo recebe `404`).
- **Convite:** o psicólogo cadastra o paciente; a API envia por e-mail um link de criação de senha (72 h, reenviável).
- **Perfil do agente:** formulário clínico (psicólogo) + pessoal (paciente); o agente só responde depois de aprovado.
- **Conversa:** monta o contexto, chama o agente, aplica a regra de risco (ADR 0002): em `risco` a resposta é **fixa**, não do LLM, e gera **Alerta** + e-mail sem conteúdo clínico. Mensagem sem classificação nunca vira "sem risco". Limite de 20 mensagens por dia (fuso `America/Sao_Paulo`).
- **Resumo diário e Pauta:** gerados sob demanda pelo agente e guardados em cache.

## Pré-requisitos

.NET 10 SDK, um **PostgreSQL** acessível, e o serviço [psiapoio-agent](https://github.com/danielbmendprofissional/psiapoio-agent) rodando. Para ver os e-mails em desenvolvimento, um SMTP local (Mailpit).

## Rodar

```bash
cd src/PsiApoio.Api

# Connection strings (nunca em appsettings versionado)
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=SEU_BANCO;Username=SEU_USUARIO;Password=SUA_SENHA"
# Usuário dono do banco, usado só para criar as tabelas em Development; pode ser o mesmo da linha acima
dotnet user-secrets set "ConnectionStrings:PostgresMigrations" "Host=localhost;Port=5432;Database=SEU_BANCO;Username=SEU_USUARIO;Password=SUA_SENHA"
# Chave compartilhada com o agente (a mesma AGENT_API_KEY dele)
dotnet user-secrets set "Agent:ApiKey" "uma-chave-qualquer"

# E-mails de dev (Mailpit, caixa em http://localhost:8025)
docker run -d --name psiapoio-mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit

dotnet run --launch-profile http        # http://localhost:5159
```

Em **Development** a API cria as tabelas sozinha e semeia o psicólogo de dev: `psicologo@psiapoio.dev` / `Psi12345`. `GET /health` (processo) e `GET /health/ready` (processo + Postgres).

## Configuração

| Chave | Onde | Descrição |
|---|---|---|
| `ConnectionStrings:Postgres` | user-secrets | Conexão de runtime |
| `ConnectionStrings:PostgresMigrations` | user-secrets | Dono do banco; só para criar o esquema (`EnsureCreated`, só em Development) |
| `Agent:BaseUrl` / `Agent:ApiKey` | appsettings / user-secrets | Serviço Python e chave (`X-Api-Key`) |
| `Jwt:Key` | user-secrets | Assinatura do JWT, mín. 32 caracteres (em Development há uma chave só de dev no `appsettings.Development.json`) |
| `Smtp:Host/Port/From`, `Front:BaseUrl` | appsettings | SMTP e URL do front usada no link do convite |
| `Conversa:LimiteDiario` | appsettings | Mensagens por dia por paciente (20) |

## Rotas (`/api/v1`)

| Grupo | Rotas |
|---|---|
| Auth | `POST auth/login`, `auth/logout`, `auth/definir-senha`; `GET usuarios/me` |
| Psicólogo | `GET/POST pacientes`, `GET/DELETE pacientes/{id}`, `POST pacientes/{id}/convite`, `PUT pacientes/{id}/clinico`, `POST pacientes/{id}/perfil/aprovar` e `reprovar`, `GET pacientes/{id}/conversa` e `alertas`, `POST pacientes/{id}/alertas/lidos`, `GET/POST pacientes/{id}/resumos` e `pauta` |
| Paciente | `GET meu/acompanhamento`, `POST meu/consentimento`, `PUT meu/pessoal`, `GET meu/conversa`, `POST meu/conversa/mensagens`, `POST meu/tarefas/{id}/marcacao` |

Erros em `application/problem+json` com um campo `codigo` (ex.: `limite_diario`, `perfil_nao_aprovado`, `sem_consentimento`).

## Estrutura

```
src/PsiApoio.Api/
  Domain/      entidades e enums
  Data/        AppDbContext (EF Core + Npgsql) e Seed
  Services/    AgentClient, ConversaService, Email (SMTP), TokenService, Clock
  Endpoints/   Auth, Pacientes (psicólogo) e Meu (paciente)
tests/PsiApoio.Api.Tests/   testes de integração
```

## Testes

```bash
dotnet test
```

19 testes de integração, com agente e e-mail falsos e banco em memória: cookie httpOnly, isolamento entre psicólogos, ciclo do perfil, resposta fixa de risco e Alerta, mensagem não classificada, falha do agente, limite diário, resumo e pauta.

## Limitações

Esquema criado por `EnsureCreated` em Development (em produção seriam migrations); sessão de 60 minutos sem refresh; sem encaminhamento a serviço de emergência (decisão registrada no ADR 0002). Só dados fictícios.

# psiapoio-api

API .NET 10 do PsiApoio. É a dona do Postgres e emite o JWT; o serviço Python do agente (`psiapoio-agent`) só é chamado por ela (ver ADR 0003 do workspace). Passo a passo completo e roteiro de demonstração em [COMO-RODAR.md](../COMO-RODAR.md).

## Rodar

```bash
dotnet user-secrets set "Agent:ApiKey" "<mesma AGENT_API_KEY do agente>" --project src/PsiApoio.Api
dotnet run --project src/PsiApoio.Api --launch-profile http   # http://localhost:5159
```

As connection strings vêm da infra compartilhada (ver COMO-RODAR.md, passo 1) e ficam nos user-secrets, nunca em `appsettings*.json`. Em Development a API cria as tabelas e o psicólogo de dev (`psicologo@psiapoio.dev` / `Psi12345`). `GET /health` e `GET /health/ready` conferem processo e Postgres.

## Configuração

| Chave | Onde | Descrição |
|---|---|---|
| `ConnectionStrings:Postgres` | user-secrets | Role de runtime do banco `aiagentfaculdade` |
| `ConnectionStrings:PostgresMigrations` | user-secrets | Role dona: usada só para criar o esquema (`EnsureCreated`, só em Development) |
| `Agent:BaseUrl` / `Agent:ApiKey` | appsettings / user-secrets | Serviço Python e chave compartilhada (`X-Api-Key`) |
| `Jwt:Key` | user-secrets (em Development há uma chave só de dev no appsettings) | Assinatura do JWT (mín. 32 caracteres) |
| `Smtp:*`, `Front:BaseUrl` | appsettings | SMTP (Mailpit em dev) e URL do front para o link do convite |
| `Conversa:LimiteDiario` | appsettings | Mensagens por dia por paciente (20) |

## Rotas (`/api/v1`)

Auth: `POST auth/login|logout|definir-senha`, `GET usuarios/me`. Psicólogo: `/pacientes` (lista, cadastro, convite, remoção, detalhe, `clinico`, `perfil/aprovar|reprovar`, `conversa`, `alertas`, `resumos`, `pauta`). Paciente: `/meu/acompanhamento|consentimento|pessoal|conversa|tarefas/{id}/marcacao`. Erros em `problem+json` com `codigo`.

## Testes

`dotnet test`: 19 testes de integração (agente e e-mail falsos, banco em memória): sessão por cookie, isolamento entre psicólogos, ciclo do perfil, resposta fixa de risco, limite diário, resumo e pauta.

## Estado

Fatias A1, B1, B2, B3, C3, D1, D2 e D3 implementadas. Esquema criado por `EnsureCreated` em Development (migrations ficariam para produção).

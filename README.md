<div align="center">

# punched-api

**Punched — .NET 8 Web API** for the Punched digital loyalty rewards platform.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169E1?logo=postgresql&logoColor=white)](https://postgresql.org/)
[![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?logo=docker&logoColor=white)](https://docker.com/)

</div>

---

## Overview

This repository contains the backend API for **Punched**, a full-stack loyalty card
platform that replaces paper punch cards with real-time digital stamping, instant
rewards, and actionable business analytics. The companion frontend lives in the
[punched-ui](https://github.com/CodeWithMaina/punched-ui) repository.

## Project Structure

```
punched-api/
├── PunchedApi/                 # .NET 8 Web API
│   ├── API/Controllers/        # REST controllers
│   ├── Application/
│   │   ├── DTOs/               # Request/response models
│   │   ├── Services/           # Business logic
│   │   ├── Validators/         # FluentValidation rules
│   │   └── Mappings/           # AutoMapper profiles
│   ├── Domain/
│   │   ├── Entities/           # EF Core entities
│   │   └── Interfaces/         # Service contracts
│   ├── Infrastructure/
│   │   ├── Data/               # DbContext, configurations
│   │   ├── Repositories/       # Generic + unit of work
│   │   └── Services/           # Email, SSE, cleanup
│   ├── Migrations/             # EF Core migrations
│   └── Dockerfile
├── PunchedApi.Tests/           # xUnit test suite
├── punched.sln
└── docker-compose.yml          # db + api orchestration
```

## Getting Started

```bash
# 1. Configure environment
cp .env.example .env        # then fill in DB_PASSWORD, JWT_SECRET, SMTP_*

# 2. Run the stack (PostgreSQL + API)
docker compose up -d

# 3. Or run locally
dotnet restore
dotnet run --project PunchedApi
```

The API is exposed on `http://localhost:8080` with Swagger in Development mode.

## Tests

```bash
dotnet test PunchedApi.Tests --nologo
```

## Security Highlights

- **JWT**: Short-lived access tokens (60 min) + rotating refresh tokens (30 days)
- **Password**: BCrypt with automatic rehashing
- **Rate limiting**: IP-based throttling on OTP, login, and general endpoints
- **CORS**: Configurable allowed origins (env-driven for Docker)
- **Input validation**: FluentValidation

## License

This project is proprietary. All rights reserved.

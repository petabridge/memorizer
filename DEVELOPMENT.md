# Development Guide

## Running from IDE with Dockerized Dependencies

This guide explains how to run the Memorizer application from your IDE (Rider, Visual Studio, VS Code) while running all dependencies in Docker containers.

### Quick Start

1. **Start dependencies** (only needs to be done once):
   ```bash
   docker-compose -f docker-compose.dev.yml up -d
   ```

2. **Wait for initialization** (first time only):
   - PostgreSQL starts immediately
   - Ollama downloads the embedding model (~100MB)
   - Ollama downloads the LLM model (~300MB)
   - Check progress: `docker-compose -f docker-compose.dev.yml logs -f ollama-init`

3. **Run the application** from your IDE:
   - Open `Memorizer.sln` in your IDE
   - Set `Memorizer` as the startup project
   - Press F5 (or your IDE's run/debug command)
   - Application will start on `http://localhost:5000`

### Services Overview

The `docker-compose.dev.yml` file starts these services:

| Service | Port | Purpose | Access |
|---------|------|---------|--------|
| **PostgreSQL** | 5432 | Vector database with pgvector | `localhost:5432` |
| **pgAdmin** | 5050 | Database management UI | http://localhost:5050 |
| **Ollama** | 11434 | LLM and embedding service | `localhost:11434` |

### Default Credentials

**PostgreSQL:**
- Host: `localhost`
- Port: `5432`
- Database: `postgmem`
- Username: `postgres`
- Password: `postgres`

**pgAdmin:**
- URL: http://localhost:5050
- Email: `admin@example.com`
- Password: `admin`

### Configuration

The application is pre-configured via `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Storage": "Host=localhost;Port=5432;Database=postgmem;Username=postgres;Password=postgres"
  },
  "Embeddings": {
    "ApiUrl": "http://localhost:11434",
    "Model": "all-minilm:33m-l12-v2-fp16"
  },
  "LLM": {
    "ApiUrl": "http://localhost:11434",
    "Model": "qwen2:0.5b"
  }
}
```

### Managing Dependencies

**Check status:**
```bash
docker-compose -f docker-compose.dev.yml ps
```

**View logs:**
```bash
# All services
docker-compose -f docker-compose.dev.yml logs -f

# Specific service
docker-compose -f docker-compose.dev.yml logs -f postgres
docker-compose -f docker-compose.dev.yml logs -f ollama
```

**Stop dependencies:**
```bash
docker-compose -f docker-compose.dev.yml down
```

**Stop and remove data volumes:**
```bash
docker-compose -f docker-compose.dev.yml down -v
```

**Restart a specific service:**
```bash
docker-compose -f docker-compose.dev.yml restart postgres
```

### Troubleshooting

**PostgreSQL not ready:**
```bash
# Check if PostgreSQL is healthy
docker-compose -f docker-compose.dev.yml ps postgres

# View PostgreSQL logs
docker-compose -f docker-compose.dev.yml logs postgres
```

**Ollama models not loaded:**
```bash
# Check model download status
docker-compose -f docker-compose.dev.yml logs ollama-init

# Manually trigger model download
docker exec -it postgmem-ollama-dev ollama pull all-minilm:33m-l12-v2-fp16
docker exec -it postgmem-ollama-dev ollama pull qwen2:0.5b

# List loaded models
docker exec -it postgmem-ollama-dev ollama list
```

**Port conflicts:**
If ports 5432, 5050, or 11434 are already in use, you can modify the port mappings in `docker-compose.dev.yml` and update `appsettings.json` accordingly.

### Running Tests

Integration tests require the same dependencies. Make sure `docker-compose.dev.yml` is running, then:

```bash
dotnet test
```

### Alternative: Full Stack in Docker

If you want to run everything (including the app) in Docker:

```bash
# Build and run full stack
docker-compose -f docker-compose.local.yml up --build

# Access at http://localhost:5000
```

### Switching Between Modes

**IDE → Docker:**
1. Stop your IDE debugger
2. Build local image: `docker build -t memorizer:latest .`
3. Start full stack: `docker-compose -f docker-compose.local.yml up`

**Docker → IDE:**
1. Stop full stack: `docker-compose -f docker-compose.local.yml down`
2. Start dependencies only: `docker-compose -f docker-compose.dev.yml up -d`
3. Run from IDE

### Development Workflow

1. Start dependencies once: `docker-compose -f docker-compose.dev.yml up -d`
2. Code changes → Save → Hot reload (if enabled) or restart debugger
3. When done: `docker-compose -f docker-compose.dev.yml down`

Dependencies persist across sessions, so you only need to start them once per machine restart.

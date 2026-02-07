# Local Development

This guide describes how to run Memorizer locally without using the containerized application.

---

## Start Only Infrastructure Services

```bash
docker-compose up -d postgres pgadmin ollama ollama-init
```

## Run the .NET App Locally

### PowerShell
```powershell
cd Memorizer
$env:ConnectionStrings__Storage="Host=localhost;Port=5432;Database=postgmem;Username=postgres;Password=postgres"
$env:Embeddings__ApiUrl="http://localhost:11434"
$env:Embeddings__Model="all-minilm:33m-l12-v2-fp16"
dotnet run
```

### Bash
```bash
cd Memorizer
export ConnectionStrings__Storage="Host=localhost;Port=5432;Database=postgmem;Username=postgres;Password=postgres"
export Embeddings__ApiUrl="http://localhost:11434"
export Embeddings__Model="all-minilm:33m-l12-v2-fp16"
dotnet run
``` 
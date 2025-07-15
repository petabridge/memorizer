# PoC Test Data Creation Script - Fixed Version

param(
    [string]$BaseUrl = "http://localhost:5000",
    [string]$ApiEndpoint = "/api/memory"
)

Add-Type -AssemblyName System.Web

$headers = @{
    "Content-Type" = "application/json"
}

# Test memories designed to match the OpenTelemetry queries
$testMemories = @(
    @{
        title = "Docker Compose Environment Variables"
        type = "reference"
        content = "Instructions for configuring POSTGMEM environment variables in docker-compose.yml files. Includes database connection strings, embedding service URLs, and authentication tokens."
        source = "documentation"
        tags = @("docker-compose", "environment-variables", "postgmem", "configuration")
        confidence = 0.9
    },
    @{
        title = "ConfigurationController API Endpoints"
        type = "reference"
        content = "REST API endpoints for configuration management including data mapping between UI forms and backend services. Handles configuration validation and persistence."
        source = "code-documentation"
        tags = @("configurationcontroller", "data-mapping", "api", "ui")
        confidence = 0.9
    }
)

# Test queries from OpenTelemetry examples
$testQueries = @(
    "docker-compose environment variable names POSTGMEM configuration",
    "ConfigurationController data-mapping api ui"
)

Write-Host "Creating test memories for dual embedding PoC..." -ForegroundColor Green

foreach ($memory in $testMemories) {
    $body = @{
        type = $memory.type
        title = $memory.title
        content = $memory.content
        source = $memory.source
        tags = $memory.tags
        confidence = $memory.confidence
    } | ConvertTo-Json -Depth 10

    Write-Host "Creating: $($memory.title)" -ForegroundColor Yellow
    
    try {
        $response = Invoke-RestMethod -Uri "$BaseUrl$ApiEndpoint" -Method POST -Body $body -Headers $headers
        Write-Host "Created memory: $($response.id)" -ForegroundColor Green
    }
    catch {
        Write-Host "Failed to create memory: $($memory.title)" -ForegroundColor Red
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "Running comparison tests with multiple thresholds..." -ForegroundColor Green

$thresholds = @(0.5, 0.7, 0.8, 0.9)

foreach ($query in $testQueries) {
    Write-Host "`n=== Testing Query: '$query' ===" -ForegroundColor Cyan
    
    foreach ($threshold in $thresholds) {
        Write-Host "`n--- Threshold: $threshold ---" -ForegroundColor Yellow
        
        try {
            $encodedQuery = [System.Web.HttpUtility]::UrlEncode($query)
            $compareUrl = "$BaseUrl$ApiEndpoint/search/compare?query=$encodedQuery" + "&minSimilarity=$threshold&limit=5"
            $comparison = Invoke-RestMethod -Uri $compareUrl -Method GET
            
            Write-Host "Full Embedding: $($comparison.summary.fullEmbeddingCount) results" -ForegroundColor White
            Write-Host "Metadata Embedding: $($comparison.summary.metadataEmbeddingCount) results" -ForegroundColor White
            
            if ($comparison.summary.fullEmbeddingBestScore) {
                Write-Host "Best Full Score: $([math]::Round($comparison.summary.fullEmbeddingBestScore, 4))" -ForegroundColor Green
            }
            if ($comparison.summary.metadataEmbeddingBestScore) {
                Write-Host "Best Metadata Score: $([math]::Round($comparison.summary.metadataEmbeddingBestScore, 4))" -ForegroundColor Green
            }
            
            # Show top result from each approach
            if ($comparison.fullEmbeddingResults.Count -gt 0) {
                $topFull = $comparison.fullEmbeddingResults[0]
                Write-Host "Top Full: '$($topFull.title)' (Score: $([math]::Round($topFull.similarity, 4)))" -ForegroundColor Magenta
            }
            if ($comparison.metadataEmbeddingResults.Count -gt 0) {
                $topMeta = $comparison.metadataEmbeddingResults[0]
                Write-Host "Top Metadata: '$($topMeta.title)' (Score: $([math]::Round($topMeta.similarity, 4)))" -ForegroundColor Magenta
            }
            
            # Show which approach wins at this threshold
            $fullCount = $comparison.summary.fullEmbeddingCount
            $metaCount = $comparison.summary.metadataEmbeddingCount
            $fullScore = if ($comparison.summary.fullEmbeddingBestScore) { $comparison.summary.fullEmbeddingBestScore } else { 0 }
            $metaScore = if ($comparison.summary.metadataEmbeddingBestScore) { $comparison.summary.metadataEmbeddingBestScore } else { 0 }
            
            if ($fullCount -eq 0 -and $metaCount -gt 0) {
                Write-Host "*** METADATA WINS: Has results while Full has none ***" -ForegroundColor Red
            } elseif ($metaCount -eq 0 -and $fullCount -gt 0) {
                Write-Host "*** FULL WINS: Has results while Metadata has none ***" -ForegroundColor Red
            } elseif ($metaScore -gt $fullScore) {
                Write-Host "*** METADATA has higher confidence score ***" -ForegroundColor Blue
            } elseif ($fullScore -gt $metaScore) {
                Write-Host "*** FULL has higher confidence score ***" -ForegroundColor Blue
            }
        }
        catch {
            Write-Host "Failed to test query at threshold $threshold" -ForegroundColor Red
            Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

Write-Host "PoC test completed!" -ForegroundColor Green
# Dual Embedding Proof of Concept

## Problem Statement

LLMs typically use keyword-focused metadata queries when searching vector stores (based on OpenTelemetry observations):

- `"docker-compose environment variable names POSTGMEM configuration"`
- `"ConfigurationController data-mapping api ui"`
- `"2025-05-23 project_note bug-fix"`
- `"web UI JavaScript data structure bug fix"`

However, current vector search implementations embed the entire memory content, which can be large and dilute the metadata signals that queries are actually targeting. This leads to poor similarity scores even for relevant content.

## Proposed Solution

Store **two embeddings per memory**:
1. **Full Embedding** (current): title + full content
2. **Metadata Embedding** (new): title + tags only

## Implementation

### Database Changes
- Added `embedding_metadata VECTOR(384)` column to `memories` table
- Added index for metadata embedding searches

### API Changes
- New endpoint: `GET /api/memory/search/full` - Search using full embeddings
- New endpoint: `GET /api/memory/search/metadata` - Search using metadata embeddings
- New endpoint: `GET /api/memory/search/compare` - Side-by-side comparison

### Code Changes
- Modified `StoreMemory()` to generate both embeddings
- Added comparison search methods to `IStorage`
- Enhanced `Memory` model with `EmbeddingMetadata` property

## Testing

### Test Data Creation
Run the PowerShell script to create test memories and run comparisons:

```powershell
./scripts/poc-test-data.ps1
```

### Manual Testing
Compare search approaches using the API:

```bash
# Compare both approaches for a keyword query
curl "http://localhost:5000/api/memory/search/compare?query=docker-compose%20environment%20variable%20names%20POSTGMEM%20configuration&minSimilarity=0.5&limit=5"
```

### Expected Results

The metadata embedding approach should show:
- **Higher similarity scores** for keyword-style queries
- **Better precision** for metadata-focused searches
- **More relevant results** at higher similarity thresholds

## Running the PoC

1. **Start the application:**
   ```bash
   cd src/PostgMem
   dotnet run
   ```

2. **Run test script:**
   ```powershell
   ./scripts/poc-test-data.ps1
   ```

3. **Compare results** using the `/search/compare` endpoint

## Comparison Metrics

The comparison endpoint returns:
- **Result counts** for each approach
- **Best similarity scores** for each approach  
- **Unique results** found by each method
- **Side-by-side result lists** with scores

## Implementation Notes

- Metadata embeddings use: `"{title} {tag1} {tag2} {tag3}"`
- Both embeddings use the same embedding service/model
- Backward compatibility maintained (existing embeddings still work)
- No performance impact on existing search functionality 
# Supported Embedding Models

This document provides guidance on choosing and configuring embedding models for Memorizer.

## What Are Embedding Models?

Embedding models convert text into dense numerical vectors (embeddings) that capture semantic meaning. These vectors enable similarity search - finding memories that are conceptually related to a query, not just keyword matches.

Memorizer uses these embeddings for:
- **Semantic search**: Find memories by meaning, not just exact text
- **Metadata search**: Search by title, tags, and type combined
- **Similarity ranking**: Order results by relevance

## Supported Providers

Memorizer works with any embedding API that follows the Ollama embedding format:

```
POST /api/embeddings
{
  "model": "model-name",
  "prompt": "text to embed"
}
```

### Ollama (Recommended for Local)

[Ollama](https://ollama.ai/) provides an easy way to run embedding models locally:

```bash
# Install Ollama
curl -fsSL https://ollama.ai/install.sh | sh

# Pull an embedding model
ollama pull all-minilm

# Verify it's working
curl http://localhost:11434/api/embeddings -d '{
  "model": "all-minilm",
  "prompt": "test"
}'
```

### Other Compatible Providers

- **LocalAI**: Self-hosted, OpenAI-compatible API
- **LM Studio**: Desktop app with API server mode
- **vLLM**: High-performance inference server

## Popular Embedding Models

### Quick Reference Table

| Model | Dimensions | Size | Speed | Quality | Best For |
|-------|------------|------|-------|---------|----------|
| `all-minilm` | 384 | 23MB | Fast | Good | General use, limited resources |
| `all-minilm:33m-l12-v2-fp16` | 384 | 66MB | Fast | Good | Better precision than base |
| `nomic-embed-text` | 768 | 274MB | Medium | Better | Balanced quality/speed |
| `mxbai-embed-large` | 1024 | 670MB | Slow | Best | Maximum quality |
| `bge-base-en-v1.5` | 768 | 438MB | Medium | Better | English text |
| `bge-large-en-v1.5` | 1024 | 1.3GB | Slow | Best | English text, high quality |
| `bge-m3` | 1024 | 1.2GB | Slow | Best | Multilingual |
| `snowflake-arctic-embed` | 1024 | 1.1GB | Slow | Best | Retrieval tasks |

### Detailed Model Information

#### all-minilm (Recommended Default)

```bash
ollama pull all-minilm
```

- **Dimensions**: 384
- **Context**: 256 tokens
- **Strengths**: Very fast, low memory, good general performance
- **Weaknesses**: Smaller context window, lower quality on complex queries
- **Use when**: Running on limited hardware, need fast responses, memories are short

#### nomic-embed-text

```bash
ollama pull nomic-embed-text
```

- **Dimensions**: 768
- **Context**: 8192 tokens
- **Strengths**: Long context, good quality, reasonable speed
- **Weaknesses**: Larger than minilm, requires more memory
- **Use when**: Memories contain longer documents, need better semantic understanding

#### mxbai-embed-large

```bash
ollama pull mxbai-embed-large
```

- **Dimensions**: 1024
- **Context**: 512 tokens
- **Strengths**: High quality embeddings, good for retrieval
- **Weaknesses**: Slower, requires more storage for embeddings
- **Use when**: Quality is priority, have adequate compute resources

#### bge-large-en-v1.5

```bash
ollama pull bge-large-en-v1.5
```

- **Dimensions**: 1024
- **Context**: 512 tokens
- **Strengths**: Excellent quality for English, well-benchmarked
- **Weaknesses**: English-only, large model size
- **Use when**: English content only, maximum retrieval quality needed

#### bge-m3

```bash
ollama pull bge-m3
```

- **Dimensions**: 1024
- **Context**: 8192 tokens
- **Strengths**: Multilingual (100+ languages), long context
- **Weaknesses**: Slower, large model
- **Use when**: Multilingual content, long documents

## Configuration

Configure your embedding model in `appsettings.json`:

```json
{
  "Embeddings": {
    "ApiUrl": "http://localhost:11434",
    "Model": "all-minilm",
    "Timeout": "00:01:00"
  }
}
```

### Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| `ApiUrl` | URL of the embedding API | `http://localhost:11434` |
| `Model` | Model name to use | Required |
| `Timeout` | Request timeout | `00:01:00` |

### Environment Variables

You can also configure via environment variables:

```bash
export Embeddings__ApiUrl=http://localhost:11434
export Embeddings__Model=nomic-embed-text
export Embeddings__Timeout=00:02:00
```

## Changing Models

### Same Dimension Models

If you switch to a model with the **same dimensions** (e.g., `all-minilm` to another 384D model), no migration is needed. Just update the config and restart.

### Different Dimension Models

If the new model has **different dimensions**, you must run a migration:

1. Update `Embeddings:Model` in configuration
2. Restart Memorizer - you'll see a warning banner
3. Navigate to **Tools > Dimension Migration**
4. Click **Start Dimension Migration**
5. Wait for all embeddings to regenerate

See [Embedding Migration](./embedding-migration.md) for detailed migration instructions.

### Migration Time Estimates

Migration time depends on:
- Number of memories
- Embedding model speed
- Hardware resources

Rough estimates for 1,000 memories:
| Model | Approximate Time |
|-------|-----------------|
| `all-minilm` | ~2-5 minutes |
| `nomic-embed-text` | ~5-10 minutes |
| `mxbai-embed-large` | ~10-20 minutes |

## Choosing a Model

### Decision Flowchart

```
Start
  │
  ▼
Do you have GPU acceleration?
  │
  ├─ No ──► all-minilm (384D)
  │
  ▼ Yes
  │
Is multilingual support needed?
  │
  ├─ Yes ──► bge-m3 (1024D)
  │
  ▼ No
  │
Are your documents long (>500 words)?
  │
  ├─ Yes ──► nomic-embed-text (768D)
  │
  ▼ No
  │
Is maximum quality critical?
  │
  ├─ Yes ──► mxbai-embed-large (1024D)
  │
  ▼ No
  │
nomic-embed-text (768D)
```

### Recommendations by Use Case

| Use Case | Recommended Model | Why |
|----------|------------------|-----|
| Personal notes | `all-minilm` | Fast, works on any hardware |
| Code snippets | `nomic-embed-text` | Better semantic understanding |
| Documentation | `nomic-embed-text` | Long context support |
| Research papers | `bge-large-en-v1.5` | High quality retrieval |
| Multilingual content | `bge-m3` | 100+ language support |
| Maximum accuracy | `mxbai-embed-large` | Best benchmark scores |

## Storage Considerations

Higher dimension models require more database storage:

| Dimensions | Storage per Memory | 10,000 Memories |
|------------|-------------------|-----------------|
| 384 | ~1.5 KB | ~15 MB |
| 768 | ~3 KB | ~30 MB |
| 1024 | ~4 KB | ~40 MB |

Note: These are estimates for the embedding vectors only. Actual storage includes text content, metadata embeddings, and indexes.

## Performance Tuning

### For Faster Embedding Generation

1. Use GPU acceleration if available
2. Choose a smaller model (`all-minilm`)
3. Increase timeout for batch operations
4. Run Ollama with `OLLAMA_NUM_PARALLEL=4` for concurrent requests

### For Better Search Quality

1. Use a larger model (`mxbai-embed-large`, `bge-large`)
2. Ensure model context covers your typical memory length
3. Use descriptive titles and tags (they're embedded separately)

## Troubleshooting

### "Model not found"

```bash
# List available models
ollama list

# Pull the model you need
ollama pull model-name
```

### "Embedding API timeout"

Increase the timeout in configuration:

```json
{
  "Embeddings": {
    "Timeout": "00:05:00"
  }
}
```

### "Dimension mismatch detected"

You've changed to a model with different output dimensions. See [Embedding Migration](./embedding-migration.md).

### Slow embedding generation

- Check if Ollama is using GPU: `ollama ps`
- Try a smaller model
- Ensure adequate system memory

## External Resources

- [Ollama Models Library](https://ollama.ai/library)
- [MTEB Leaderboard](https://huggingface.co/spaces/mteb/leaderboard) - Embedding model benchmarks
- [Sentence Transformers](https://www.sbert.net/) - Model documentation
- [pgvector](https://github.com/pgvector/pgvector) - Vector storage in PostgreSQL

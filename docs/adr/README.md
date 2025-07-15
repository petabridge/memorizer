# Architecture Decision Records (ADRs)

This directory contains Architecture Decision Records (ADRs) for the PostgMem project. ADRs are used to document important architectural decisions, their context, and consequences.

## What is an ADR?

An ADR is a document that captures an important architectural decision made along with its context and consequences. Each ADR describes:

- The architectural decision that was made
- The context and forces at play
- The current status (proposed, accepted, deprecated, superseded)
- The consequences of the decision

## Current ADRs

1. [Plain Text Memory Column](2025-05-21-plain-text-memory-column.md) - Introduces dedicated text storage for better embedding quality and LLM interaction
2. [Always Return Relationship Type](2025-05-22-always-return-relationship-type.md) - Ensures relationship types are always populated in memory DTOs
3. [Asynchronous Memory Chunking with Actors](2025-05-23-asynchronous-memory-chunking-with-actors.md) - Implements background chunking using Akka.NET actors for better search quality and fast responses
4. [Migration from Ollama to OllamaSharp](2025-05-23-migration-from-ollama-to-ollama-sharp.md) - Switches to OllamaSharp package for better .NET ecosystem integration and dependency injection support
5. [Chunking Integration Test Design](2025-05-23-chunking-integration-test-design.md) - Comprehensive testing strategy for asynchronous chunking system with TestContainers and actor verification
6. [Preserve Original Memories During Chunking](2025-01-27-preserve-original-memories-during-chunking.md) - Ensures original memory content is never modified during chunking, preventing data loss and maintaining user trust
7. [Memory Search Result Ranking and Tag Handling](2025-05-23-memory-search-ranking.md) - Similarity is primary, tags are a soft boost, always show something, and rationale/consequences documented 
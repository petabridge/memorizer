#### 2.2.0-beta2 July 1st 2026 ####

**Features**
- [Add workspace scope to memory tools](https://github.com/petabridge/memorizer/pull/197) - Memory search and storage now support workspace-level scoping
  - `SearchMemories` can filter by workspace ID to query all memories in a workspace in a single call
  - `Store` supports assigning memories directly to workspaces (not just projects)
  - Closes scoping gaps for agents operating at the workspace level

#### 2.2.0-beta1 June 26th 2026 ####

**Features**
- [Add cross-project tag filtering with multi-tag selection and dynamic filter panel](https://github.com/petabridge/memorizer/pull/168) - Filter memories across projects and workspaces by tags, type, and location from a persistent filter panel
  - Multi-tag selection with searchable suggestions and removable filter badges
  - Tag suggestions and location filters update dynamically based on the current filter context
  - Memory cards show owner badges when filtering across locations, and tag badges link directly to filtered results
  - MCP `GetByFilter` supports filtering by type, location, and tags
- [Harden OpenAI-compatible embedding provider support](https://github.com/petabridge/memorizer/pull/194) - Improves OpenAI-compatible embedding provider configuration, migration behavior, and credential safety
  - Embedding API keys are read from runtime configuration instead of persisted provider settings
  - Sensitive provider configuration values are stripped before API display or database storage
  - Adds a database migration to remove previously persisted provider API keys from `provider_settings.config`
  - Makes fallback embedding dimensions safer during dimension migrations

**Bug Fixes**
- [Self-heal stale dimension configuration after migration](https://github.com/petabridge/memorizer/pull/177) - Automatically reconciles `embedding_config` when the live provider probe and database schema already match, avoiding unnecessary reruns of embedding dimension migration

**Updates**
- [Bump Akka.Streams from 1.5.60 to 1.5.62](https://github.com/petabridge/memorizer/pull/172)
- [Bump Dapper from 2.1.66 to 2.1.72](https://github.com/petabridge/memorizer/pull/180)

#### 2.1.0 March 4th 2026 ####

**Features**
- [Add move-project-to-workspace and workspace reparenting](https://github.com/petabridge/memorizer/pull/167) - Move projects between workspaces and reparent workspaces in the hierarchy
  - `MoveProjectToWorkspaceAsync` atomically moves a project and all its descendants to a target workspace
  - Workspace reparenting with circular-reference guard to prevent invalid hierarchies
  - Full support across REST API, MCP tools, and Web UI with modal dialogs
- [Add canonical URL support to MCP tool responses](https://github.com/petabridge/memorizer/pull/162) - MCP tool responses now include clickable URLs to the web UI for memories, workspaces, and projects
  - Configure via `Server:CanonicalUrl` setting to enable links in MCP responses
  - URLs included in `Store`, `Get`, `SearchMemories`, `GetMany`, `CreateWorkspace`, `CreateProject`, `GetWorkspace`, and `GetProjectContext` responses

**Bug Fixes**
- [Make optional Guid MCP parameters resilient to client schema variations](https://github.com/petabridge/memorizer/pull/166) - Fixes compatibility with MCP clients (notably Cursor) that send empty strings or `"null"` for optional Guid parameters instead of proper JSON null

#### 2.1.0-beta1 February 15th 2026 ####

**Features**
- [Improve search quality with hybrid text + vector search](https://github.com/petabridge/memorizer/pull/157) - Combines PostgreSQL full-text search with vector similarity search using Reciprocal Rank Fusion (RRF) to dramatically improve search relevance
  - Queries like "race condition", "phobos", and "dependency injection" that previously returned zero results now surface relevant matches
  - AND-prefix FTS matching handles stemming mismatches (e.g., "postgres" matches "postgresql")
  - Adaptive weighting favors text matching for short queries, both methods equally for longer queries
  - Workspace and project search also use hybrid approach for better name matching
  - New database migration adds `search_vector` tsvector column with GIN index

**Bug Fixes**
- [Fix inconsistent memory counts by excluding system memories from user-facing queries](https://github.com/petabridge/memorizer/pull/154) - Memory list and stats no longer inflate counts with internal system index memories

**Updates**
- [Bump Microsoft.AspNetCore.Mvc.Testing from 10.0.2 to 10.0.3](https://github.com/petabridge/memorizer/pull/156)
- [Bump Akka.Streams from 1.5.58 to 1.5.60](https://github.com/petabridge/memorizer/pull/152)

#### 2.0.1 February 12th 2026 ####

**Improvements**
- [Add link to homepage on sidebar Memorizer logo](https://github.com/petabridge/memorizer/pull/148) - Sidebar logo is now clickable and navigates to the Memorizer homepage

**Bug Fixes**
- [Fix Docker commands in README for PostgreSQL backup](https://github.com/petabridge/memorizer/pull/139) - Corrected container name from `memorizer-postgres` to `postgmem-postgres` in backup instructions

**Documentation**
- [Add commands reference documentation for Memorizer](https://github.com/petabridge/memorizer/pull/141) - New reference guide documenting all available Memorizer commands
- [Update README with detailed update instructions](https://github.com/petabridge/memorizer/pull/140) - Added step-by-step bash commands for the upgrade process

#### 2.0.0 February 7th 2026 ####

**Major Release - Memorizer V2 Merge**

This release merges all Memorizer V2 features into the open-source repository, bringing significant new capabilities for organizing and managing AI agent memories.

> [!CAUTION]
> **Back up your database before upgrading.** Memorizer 2.0 runs automatic schema migrations on startup that alter tables and add new columns. These migrations are not reversible. Create a `pg_dump` of your database before pulling the new image. See the [Upgrading to Memorizer 2.0](README.md#upgrading-to-memorizer-20) section in the README for backup instructions.

**Breaking Changes**
- MCP endpoint moved from `/` to `/mcp`. Update client configs from `http://localhost:5000` to `http://localhost:5000/mcp`
- Web UI moved from `/ui` to `/`. The Web UI is now at the root path.

**New Features**
- **Workspaces**: Organize memories into isolated workspaces with independent configurations
- **Projects**: Group related memories within workspaces for better organization
- **Provider Settings**: Configure AI providers (embedding models, LLM endpoints) per-workspace
- **Structured Memory Types**: Support for documents, records, and references with distinct behaviors
- **Memory Archiving**: Archive memories instead of deleting them, with restore capability
- **System Memories**: Hidden system index memories for semantic project/workspace search
- **Unit Tests**: New unit test project for core domain logic

**Database Migrations**
- Migrations 011-019 run automatically on startup
- Existing V1 databases will be upgraded seamlessly with new tables for workspaces, projects, provider settings, and structured memory types

**V2 Release History (from private development)**
- 2.0.9: Fix broken navigation links, add Archetype field to MemorySummaryDto
- 2.0.8: Fix archived memories filtering, add archetype visibility to MCP tools
- 2.0.7: Add system memories for semantic project/workspace search

#### 1.9.0 November 29th 2025 ####

**Features**
- [Add memory versioning with full audit trail and diff support](https://github.com/petabridge/memorizer-v1/pull/96) - Comprehensive version tracking system that records all changes to memories with complete history
  - **Edit Tool**: Targeted text replacements with automatic version snapshots
  - **UpdateMetadata Tool**: Update memory metadata (type, tags, confidence, title) without changing content
  - **RevertToVersion Tool**: Restore previous versions of memories
  - **Visual Diffs**: Line-by-line comparison between versions in Web UI
  - **Event Audit Log**: Complete operation history for compliance and debugging
- [Add memory similarity discovery feature](https://github.com/petabridge/memorizer-v1/pull/117) - Surface related memories using vector similarity search with optional relationship persistence
  - On-demand similarity queries with configurable threshold (50-95%)
  - UI slider to adjust similarity sensitivity
  - Create bidirectional `similar-to` relationships with scores
  - Helps evaluate embedding model quality and build knowledge graphs
- [Add automatic embedding dimension detection and migration](https://github.com/petabridge/memorizer-v1/pull/107) - Automatically detect and migrate embeddings when switching between models with different dimensions
  - Detects dimension mismatches at startup
  - Provides UI warnings and migration tools
  - Preserves existing data during model transitions

**Improvements**
- [Switch search and similarity to use metadata embeddings](https://github.com/petabridge/memorizer-v1/pull/120) - MCP search and similarity discovery now use metadata embeddings (title + tags) instead of full content embeddings for significantly better keyword query results
  - Treats metadata as higher weight than body content for similarity matching
  - Aligns MCP tool behavior with web UI search
- [Add lightweight search results to optimize context window usage](https://github.com/petabridge/memorizer-v1/pull/121) - MCP search returns lightweight results by default to prevent swamping agent context windows
  - Returns ID, title, type, tags, similarity score, and creation date
  - Agents use `Get` or `GetMany` tools to retrieve full content as needed
  - Configurable via `Search:ReturnFullContent` setting (default: `false`)

**Bug Fixes**
- [Rename Search tool to SearchMemories](https://github.com/petabridge/memorizer-v1/pull/105) - Fixes compatibility issues with Qwen Code CLI and Gemini that disallow tools named "search"
- [Fix version history display and add version selector to compare view](https://github.com/petabridge/memorizer-v1/pull/116) - Improved version history UI with proper comparison capabilities
- [Fix dimension migration bugs and add integration tests](https://github.com/petabridge/memorizer-v1/pull/115) - Resolved issues with embedding dimension migration process
- [Fix warning button text visibility in dark mode](https://github.com/petabridge/memorizer-v1/pull/119) - Dimension mismatch warning button now visible in dark theme

**Documentation**
- [Add Qwen3 embedding models and reorganize README](https://github.com/petabridge/memorizer-v1/pull/114) - Updated embedding model documentation with Qwen3 support
- [Add embedding documentation links to README](https://github.com/petabridge/memorizer-v1/pull/108) - Improved documentation for embedding configuration

**Updates**
- [Bump Npgsql from 9.0.4 to 10.0.0](https://github.com/petabridge/memorizer-v1/pull/100) - Major PostgreSQL driver update with performance improvements
- [Bump Microsoft.AspNetCore.Mvc.Testing from 9.0.10 to 10.0.0](https://github.com/petabridge/memorizer-v1/pull/98) - Updated to .NET 10 testing framework
- [Bump OpenTelemetry.Instrumentation.AspNetCore from 1.12.0 to 1.14.0](https://github.com/petabridge/memorizer-v1/pull/109)
- [Bump OpenTelemetry.Instrumentation.Http from 1.12.0 to 1.14.0](https://github.com/petabridge/memorizer-v1/pull/110)
- [Bump OpenTelemetry.Instrumentation.Runtime from 1.12.0 to 1.14.0](https://github.com/petabridge/memorizer-v1/pull/111)
- [Bump OpenTelemetry.Exporter.OpenTelemetryProtocol from 1.12.0 to 1.14.0](https://github.com/petabridge/memorizer-v1/pull/103)
- [Bump OpenTelemetry.Extensions.Hosting from 1.12.0 to 1.14.0](https://github.com/petabridge/memorizer-v1/pull/104)
- [Bump OllamaSharp from 5.3.6 to 5.4.11](https://github.com/petabridge/memorizer-v1/pull/101) - Latest model support
- [Bump Akka.Streams from 1.5.55 to 1.5.56](https://github.com/petabridge/memorizer-v1/pull/97)
- [Bump Microsoft.NET.Test.Sdk from 18.0.0 to 18.0.1](https://github.com/petabridge/memorizer-v1/pull/99)
- [Bump DiffPlex from 1.7.2 to 1.9.0](https://github.com/petabridge/memorizer-v1/pull/102)
- [Bump xunit.runner.visualstudio from 3.1.4 to 3.1.5](https://github.com/petabridge/memorizer-v1/pull/113)
- [Bump Registrator.Net from 3.1.0 to 3.2.4](https://github.com/petabridge/memorizer-v1/pull/112)

#### 1.9.0-beta3 November 28th 2025 ####

**Improvements**
- [Switch search and similarity to use metadata embeddings](https://github.com/petabridge/memorizer-v1/pull/120) - MCP search and similarity discovery now use metadata embeddings (title + tags) instead of full content embeddings for significantly better keyword query results
  - Treats metadata as higher weight than body content for similarity matching
  - Aligns MCP tool behavior with web UI search
- [Add lightweight search results to optimize context window usage](https://github.com/petabridge/memorizer-v1/pull/121) - MCP search returns lightweight results by default to prevent swamping agent context windows
  - Returns ID, title, type, tags, similarity score, and creation date
  - Agents use `Get` or `GetMany` tools to retrieve full content as needed
  - Configurable via `Search:ReturnFullContent` setting (default: `false`)

**Bug Fixes**
- [Fix warning button text visibility in dark mode](https://github.com/petabridge/memorizer-v1/pull/119) - Dimension mismatch warning button now visible in dark theme

**Updates**
- [Bump OpenTelemetry.Instrumentation.AspNetCore from 1.12.0 to 1.14.0](https://github.com/petabridge/memorizer-v1/pull/109)

#### 1.9.0-beta2 November 28th 2025 ####

**Features**
- [Add memory similarity discovery feature](https://github.com/petabridge/memorizer-v1/pull/117) - Surface related memories using vector similarity search with optional relationship persistence
  - On-demand similarity queries with configurable threshold (50-95%)
  - UI slider to adjust similarity sensitivity
  - Create bidirectional `similar-to` relationships with scores
  - Helps evaluate embedding model quality and build knowledge graphs
- [Add automatic embedding dimension detection and migration](https://github.com/petabridge/memorizer-v1/pull/107) - Automatically detect and migrate embeddings when switching between models with different dimensions
  - Detects dimension mismatches at startup
  - Provides UI warnings and migration tools
  - Preserves existing data during model transitions

**Bug Fixes**
- [Fix version history display and add version selector to compare view](https://github.com/petabridge/memorizer-v1/pull/116) - Improved version history UI with proper comparison capabilities
- [Fix dimension migration bugs and add integration tests](https://github.com/petabridge/memorizer-v1/pull/115) - Resolved issues with embedding dimension migration process

**Documentation**
- [Add Qwen3 embedding models and reorganize README](https://github.com/petabridge/memorizer-v1/pull/114) - Updated embedding model documentation with Qwen3 support
- [Add embedding documentation links to README](https://github.com/petabridge/memorizer-v1/pull/108) - Improved documentation for embedding configuration

#### 1.9.0-beta1 November 27th 2025 ####

**Features**
- [Add memory versioning with full audit trail and diff support](https://github.com/petabridge/memorizer-v1/pull/96) - Comprehensive version tracking system that records all changes to memories with complete history
  - **Edit Tool**: Targeted text replacements with automatic version snapshots
  - **UpdateMetadata Tool**: Update memory metadata (type, tags, confidence, title) without changing content
  - **RevertToVersion Tool**: Restore previous versions of memories
  - **Visual Diffs**: Line-by-line comparison between versions in Web UI
  - **Event Audit Log**: Complete operation history for compliance and debugging

**Bug Fixes**
- [Rename Search tool to SearchMemories](https://github.com/petabridge/memorizer-v1/pull/105) - Fixes compatibility issues with Qwen Code CLI and Gemini that disallow tools named "search"

**Updates**
- [Bump Npgsql from 9.0.4 to 10.0.0](https://github.com/petabridge/memorizer-v1/pull/100) - Major PostgreSQL driver update with performance improvements
- [Bump Microsoft.AspNetCore.Mvc.Testing from 9.0.10 to 10.0.0](https://github.com/petabridge/memorizer-v1/pull/98) - Updated to .NET 10 testing framework
- Updated OpenTelemetry packages to 1.14.0 for improved observability
- Updated OllamaSharp from 5.3.6 to 5.4.11 for latest model support
- Updated Akka.Streams from 1.5.55 to 1.5.56
- Updated Microsoft.NET.Test.Sdk from 18.0.0 to 18.0.1

#### 1.8.0 November 25th 2025 ####

**Features**
- [Unified Embedding Regeneration Tool](https://github.com/petabridge/memorizer-v1/pull/91) - Regenerate embeddings for both memory content and metadata, enabling model changes and dimension updates without data loss
- [.NET 10 SSE support](https://github.com/petabridge/memorizer-v1/pull/88) - Upgraded to .NET 10 with improved Server-Sent Events capabilities for real-time progress tracking
- [Add Mermaid.js support and dark/light theme switcher](https://github.com/petabridge/memorizer-v1/pull/92) - Enhanced documentation visualization with Mermaid diagrams and automatic theme detection

**Updates**
- [Updated Model Context Protocol (MCP) packages to 0.4.0-preview.3](https://github.com/petabridge/memorizer-v1/pull/93) - Latest MCP protocol improvements and bug fixes
- Updated Akka.NET and .NET dependencies to latest versions - Including Akka.Persistence.Sql.Hosting, Akka.Hosting, Akka.Hosting.TestKit, and Microsoft.NET.Test.Sdk

**Bug Fixes**
- [Fix footer link styling in light and dark themes](https://github.com/petabridge/memorizer-v1/pull/94) - Improved visibility and consistency of footer links across themes

#### 1.7.1 October 10th 2025 ####

**Breaking Changes**
- **MCP Endpoint Configuration Updated**: The recommended MCP endpoint has changed from `/sse` to the root path `/` to use modern Streamable HTTP transport (MCP spec 2025-03-26+)
  - **Before:** `"url": "http://localhost:5000/sse"`
  - **After:** `"url": "http://localhost:5000"`
  - The `/sse` endpoint still works for backward compatibility but is no longer recommended
  - [Update MCP endpoint from /sse to root path for Streamable HTTP](https://github.com/petabridge/memorizer-v1/pull/57)

**Bug Fixes**
- [Enable stateless mode for MCP HTTP transport](https://github.com/petabridge/memorizer-v1/pull/64) - Fixes "Session not found" errors when clients reconnect or server restarts by enabling sessionless operation

**Updates**
- [Bump ModelContextProtocol from 0.3.0-preview.4 to 0.4.0-preview.2](https://github.com/petabridge/memorizer-v1/pull/56) - Fixes server notification bugs and improves protocol compatibility
- [Bump ModelContextProtocol.AspNetCore from 0.3.0-preview.4 to 0.4.0-preview.2](https://github.com/petabridge/memorizer-v1/pull/56) - Adds Streamable HTTP transport support

#### 1.7.0 October 9th 2025 ####

**Features**
- [Add configurable CORS support for MCP SSE endpoints](https://github.com/petabridge/memorizer-v1/pull/53) - Enables MCP clients to connect with customizable CORS policies
- [Make embedding dimensions configurable](https://github.com/petabridge/memorizer-v1/pull/18) - Support different embedding models via `Dimensions` setting

**Updates**
- [Bump all MSFT dependencies to 9.0.9](https://github.com/petabridge/memorizer-v1/pull/54) - Updated to .NET 9 framework
- [Bump ModelContextProtocol from 0.3.0-preview.3 to 0.3.0-preview.4](https://github.com/petabridge/memorizer-v1/pull/36) - Framework improvements and bug fixes
- [Bump ModelContextProtocol.AspNetCore from 0.3.0-preview.3 to 0.3.0-preview.4](https://github.com/petabridge/memorizer-v1/pull/39) - Framework improvements and bug fixes

#### 1.6.1 July 24th 2025 ####

- [Bump OllamaSharp from 5.2.10 to 5.3.3](https://github.com/petabridge/memorizer-v1/pull/13)
- [Bump `ModelContextProtocol` from 0.1.0-preview.14 to 0.3.0-preview.3](https://github.com/petabridge/memorizer-v1/pull/15)

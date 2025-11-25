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
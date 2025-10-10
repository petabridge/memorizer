#### 1.7.1 October 10th 2025 ####

**Breaking Changes**
- **MCP Endpoint Configuration Updated**: The recommended MCP endpoint has changed from `/sse` to the root path `/` to use modern Streamable HTTP transport (MCP spec 2025-03-26+)
  - **Before:** `"url": "http://localhost:5000/sse"`
  - **After:** `"url": "http://localhost:5000"`
  - The `/sse` endpoint still works for backward compatibility but is no longer recommended
  - [Update MCP endpoint from /sse to root path for Streamable HTTP](https://github.com/petabridge/memorizer-v1/pull/57)

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
# Memorizer Commands Reference

Complete list of all available Memorizer MCP tools/commands.

---

## 🔍 Search & Retrieve

| Command | Description |
|---------|-------------|
| `SearchMemories` | Search for memories by text query with similarity scoring |
| `Get` | Retrieve a single memory by ID with full details and relationships |
| `GetMany` | Retrieve multiple memories by their IDs in one call |
| `ListArchived` | List all archived memories with pagination |

---

## ✏️ Create & Edit

| Command | Description |
|---------|-------------|
| `Store` | Create and store a new memory with optional project assignment |
| `Edit` | Edit a memory using find-and-replace (all changes are versioned) |
| `Delete` | Permanently delete a memory (including all version history) |
| `ArchiveMemory` | Archive a memory to mark it as obsolete (hidden from searches) |
| `RestoreMemory` | Restore an archived memory back to active status |
| `RevertToVersion` | Revert a memory to a specific previous version |
| `UpdateMetadata` | Update memory metadata (title, type, tags, confidence) without changing content |

---

## 🔗 Relationships

| Command | Description |
|---------|-------------|
| `CreateReference` | Create a relationship between two memories (e.g., "example-of", "related-to", "explains") |

---

## 📁 Projects

| Command | Description |
|---------|-------------|
| `GetProjectContext` | Get project details, list projects in a workspace, or search projects by name |
| `CreateProject` | Create a new project within a workspace with victory conditions |
| `UpdateProject` | Update project properties (name, description, status, victory conditions, parent) |
| `DeleteProject` | Delete a project (memories are moved to Unfiled) |
| `MoveMemory` | Move one or more memories to a project, workspace, or Unfiled |

---

## 🗂️ Workspaces

| Command | Description |
|---------|-------------|
| `GetWorkspace` | Get workspace information, list workspaces, or search by name |
| `CreateWorkspace` | Create a new top-level or nested workspace |
| `UpdateWorkspace` | Update workspace name or description |
| `DeleteWorkspace` | Delete a workspace (memories move to Unfiled) |

---

## Memory Archetypes

When storing memories, you can specify the archetype:

- **document** - Living, editable content (default)
- **record** - Historical, immutable records (e.g., work logs)

---

## Memory Archetypes

When storing memories via `memorizer_store`, you can specify the archetype:

- **document** - Living, editable content (default)
- **record** - Historical, immutable records (e.g., work logs)

These archetypes are defined in the `memorizer_store` tool schema.




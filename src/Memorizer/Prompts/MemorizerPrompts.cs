using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PostgMem.Tools;

/// <summary>
/// MCP prompts that teach a client the correct way to use Memorizer.
///
/// The prompt bodies are written in Simplified Technical English (ASD-STE100):
/// short sentences, the imperative mood, active voice, one instruction per
/// sentence, and a small, consistent vocabulary.
/// </summary>
[McpServerPromptType]
public class MemorizerPrompts
{
    [McpServerPrompt(Name = "memorizer_overview", Title = "How to use Memorizer"),
     Description("Explain the correct way to use Memorizer, including its structure and rules.")]
    public string MemorizerOverview() =>
        """
        Memorizer is your long-term memory. It keeps memories between sessions.

        Memorizer has three levels:
        1. A workspace is a top-level area. Examples: Engineering, Sales.
        2. A project is inside a workspace. A project has a goal.
        3. A memory is a fact, a note, or a decision. A memory belongs to a project or a workspace.

        Obey these rules:
        - Search before you store. Do not make duplicate memories.
        - Give each memory a short, clear title.
        - Add two or more tags to each memory.
        - Put each memory in the correct project or workspace.
        - Load related memories before you answer.
        - Update a memory when the facts change. Do not add a second memory for the same fact.
        """;

    [McpServerPrompt(Name = "store_memory", Title = "Store a memory correctly"),
     Description("Give the correct steps to store a new memory without making duplicates.")]
    public string StoreMemory(
        [Description("Optional summary of the fact or note to store.")] string? content = null) =>
        $"""
        Do these steps to store a memory{(string.IsNullOrWhiteSpace(content) ? "" : $" about: {content}")}.

        1. Search for related memories first. Use the search_memories tool.
        2. If a related memory exists, update it with the edit tool. Then stop.
        3. If no related memory exists, continue.
        4. Choose the owner. Use a project for work items. Use a workspace for general facts.
        5. Write a short, clear title.
        6. Add two or more tags.
        7. Store the memory with the store tool.
        8. If the memory relates to another memory, link them with the create_reference tool.
        """;

    [McpServerPrompt(Name = "find_context", Title = "Find context before you answer"),
     Description("Give the correct steps to find and load memories before you answer a question.")]
    public string FindContext(
        [Description("The subject of the question.")] string topic) =>
        $"""
        Do these steps before you answer a question about {topic}.

        1. Search for memories with the search_memories tool. Use "{topic}" as the query.
        2. Read the top results.
        3. Load the full text of the useful memories. Use the get tool or the get_many tool.
        4. If the question is about a project, load the project context. Use the get_project_context tool.
        5. Use the memories in your answer.
        6. If you find no memory, tell the user. Then answer from general knowledge.
        """;

    [McpServerPrompt(Name = "review_project", Title = "Review a project before you work"),
     Description("Give the correct steps to load a project's context before you work on it.")]
    public string ReviewProject(
        [Description("The name or ID of the project.")] string project) =>
        $"""
        Do these steps before you work on {project}.

        1. Get the project context with the get_project_context tool.
        2. Read the goal and the status.
        3. Read the recent memories in the project.
        4. Note the open questions and the decisions.
        5. Use this context in your work.
        6. Store new decisions as memories in the project.
        """;

    [McpServerPrompt(Name = "organize_memory", Title = "Choose the correct place for a memory"),
     Description("Give the correct steps to choose the workspace and project for a memory.")]
    public string OrganizeMemory() =>
        """
        Do these steps to place a memory.

        1. List the workspaces with the get_workspace tool.
        2. Choose the workspace that matches the subject.
        3. If no workspace matches, create one with the create_workspace tool. Use a clear name.
        4. Look for a project in the workspace. Use the get_workspace tool with the workspace ID.
        5. If a project matches the work, use that project.
        6. If no project matches, keep the memory at the workspace level.
        7. Do not use the Unfiled workspace if a better place exists.
        """;

    [McpServerPrompt(Name = "maintain_memory", Title = "Keep memories correct and current"),
     Description("Give the correct steps to keep memories correct, current, and clean.")]
    public string MaintainMemory() =>
        """
        Do these steps to keep memories correct.

        1. When facts change, edit the memory with the edit tool. Do not make a new memory.
        2. When a memory is old or not useful, archive it with the archive_memory tool.
        3. Do not delete a memory unless it is a mistake. Archive is safer than delete.
        4. To restore an archived memory, use the restore_memory tool.
        5. To undo a bad change, revert the memory to an earlier version with the revert_to_version tool.
        """;

    [McpServerPrompt(Name = "start_project", Title = "Start a project correctly"),
     Description("Give the correct steps to create a new project with a goal and victory conditions.")]
    public string StartProject(
        [Description("Optional one-sentence goal of the project.")] string? goal = null) =>
        $"""
        Do these steps to create a project{(string.IsNullOrWhiteSpace(goal) ? "" : $" with this goal: {goal}")}.

        1. Choose the workspace for the project. List the workspaces with the get_workspace tool.
        2. Look at the projects in that workspace. Do not make a duplicate project.
        3. Choose a short, clear name for the project.
        4. Write the goal in one sentence.
        5. Write the victory conditions. State how you know the project is complete.
        6. Create the project with the create_project tool. Put it in the workspace.
        7. To make a sub-project, set the parent project.
        8. Store the first decisions as memories in the project.
        """;
}

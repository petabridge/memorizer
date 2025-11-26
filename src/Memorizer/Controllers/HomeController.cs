using Memorizer.Services;
using Memorizer.Settings;
using Microsoft.AspNetCore.Mvc;

namespace Memorizer.Controllers;

[Route("ui")]
public class HomeController : Controller
{
    private readonly IMemoryStatsService _statsService;
    private readonly ServerSettings _serverSettings;

    public HomeController(IMemoryStatsService statsService, ServerSettings serverSettings)
    {
        _statsService = statsService;
        _serverSettings = serverSettings;
    }

    /// <summary>
    /// Main memory management page - accessible at /ui/
    /// </summary>
    [HttpGet]
    [Route("")]
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Memory details/edit page
    /// </summary>
    [HttpGet]
    [Route("edit/{id:guid}")]
    public IActionResult Edit(Guid id)
    {
        ViewBag.MemoryId = id;
        return View();
    }

    /// <summary>
    /// Memory view page - read-only with rendered markdown
    /// </summary>
    [HttpGet]
    [Route("view/{id:guid}")]
    public IActionResult View(Guid id)
    {
        ViewBag.MemoryId = id;
        return View();
    }

    /// <summary>
    /// Create new memory page
    /// </summary>
    [HttpGet]
    [Route("create")]
    public IActionResult Create()
    {
        return View();
    }

    /// <summary>
    /// Enhanced stats dashboard page
    /// </summary>
    [HttpGet]
    [Route("stats")]
    public async Task<IActionResult> Stats()
    {
        var stats = await _statsService.GetStatsAsync();
        return View(stats);
    }

    /// <summary>
    /// MCP configuration page - shows configuration UI
    /// </summary>
    [HttpGet]
    [Route("mcp-config")]
    public IActionResult McpConfig()
    {
        return View();
    }

    /// <summary>
    /// System configuration page - shows all settings for debugging
    /// </summary>
    [HttpGet]
    [Route("config")]
    public IActionResult Config()
    {
        return View();
    }

    /// <summary>
    /// View a specific version of a memory
    /// </summary>
    [HttpGet]
    [Route("view/{id:guid}/version/{versionNumber:int}")]
    public IActionResult ViewVersion(Guid id, int versionNumber)
    {
        ViewBag.MemoryId = id;
        ViewBag.VersionNumber = versionNumber;
        return View();
    }

    /// <summary>
    /// Compare two versions of a memory
    /// </summary>
    [HttpGet]
    [Route("view/{id:guid}/compare/{fromVersion:int}/{toVersion:int}")]
    public IActionResult CompareVersions(Guid id, int fromVersion, int toVersion)
    {
        ViewBag.MemoryId = id;
        ViewBag.FromVersion = fromVersion;
        ViewBag.ToVersion = toVersion;
        return View();
    }

    /// <summary>
    /// MCP configuration JSON endpoint - returns simplified JSON configuration for MCP client
    /// </summary>
    [HttpGet]
    [Route("mcp-config-json")]
    public IActionResult McpConfigJson()
    {
        var canonicalUrl = _serverSettings?.CanonicalUrl ?? "http://localhost:5000";
        
        var mcpConfig = new
        {
            mcpServers = new
            {
                memorizer = new
                {
                    url = canonicalUrl
                }
            }
        };

        return Json(mcpConfig);
    }
} 
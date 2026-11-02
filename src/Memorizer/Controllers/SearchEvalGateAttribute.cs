using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Memorizer.Controllers;

/// <summary>
/// Hide disabled evaluation endpoints before model binding and controller activation.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SearchEvalGateAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue<bool>("SearchEval:Enabled"))
            context.Result = new NotFoundObjectResult(new { message = "Search evaluation endpoints are disabled." });
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

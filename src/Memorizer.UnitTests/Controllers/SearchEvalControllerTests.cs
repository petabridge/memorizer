using Memorizer.Controllers;
using Memorizer.UnitTests.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memorizer.UnitTests.Controllers;

public class SearchEvalControllerTests
{
    [Fact]
    public async Task Empty_corpus_returns_status_and_requires_seeding_before_evaluation()
    {
        var controller = new SearchEvalController(
            new FakeWorkspaceStorage(), NullLogger<SearchEvalController>.Instance);

        Assert.IsType<OkObjectResult>(await controller.GetCorpusStatus(default));
        Assert.IsType<OkObjectResult>(await controller.DeleteCorpus(default));
        Assert.IsType<OkObjectResult>(await controller.CompareOrgSearch(new("test query"), default));
        var result = Assert.IsType<BadRequestObjectResult>((await controller.RunEvaluation(null, default)).Result);
        Assert.Contains("Corpus not seeded", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }
}

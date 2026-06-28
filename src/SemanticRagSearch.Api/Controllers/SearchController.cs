using Microsoft.AspNetCore.Mvc;
using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchController : ControllerBase
{
    private readonly ISemanticSearchService _semanticSearchService;

    public SearchController(ISemanticSearchService semanticSearchService)
    {
        _semanticSearchService = semanticSearchService ?? throw new ArgumentNullException(nameof(semanticSearchService));
    }

    [HttpPost]
    public async Task<ActionResult<SemanticSearchResponse>> SearchAsync(
        [FromBody] SemanticSearchRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        try
        {
            SemanticSearchResponse response = await _semanticSearchService.SearchAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
    }
}

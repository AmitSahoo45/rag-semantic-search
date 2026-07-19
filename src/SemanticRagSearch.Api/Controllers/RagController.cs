using Microsoft.AspNetCore.Mvc;
using SemanticRagSearch.Api.Rag;

namespace SemanticRagSearch.Api.Controllers;

[ApiController]
[Route("api/v1/ask")]
public class RagController : ControllerBase
{
    private readonly IRagService _ragService;

    public RagController(IRagService ragService)
    {
        _ragService = ragService ?? throw new ArgumentNullException(nameof(ragService));
    }

    [HttpPost]
    public async Task<ActionResult<RagResponse>> AskAsync(
        [FromBody] RagRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        try
        {
            RagResponse response = await _ragService.AskAsync(request, cancellationToken);
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
        catch (InvalidOperationException exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message });
        }
    }

    [HttpPost("stream")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IResult AskStream(
        [FromBody] RagRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(
                new { error = "Request body is required." });
        }

        return TypedResults.ServerSentEvents(
            _ragService.AskStreamAsync(request, cancellationToken));
    }
}

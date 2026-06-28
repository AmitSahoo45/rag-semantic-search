using Microsoft.AspNetCore.Mvc;
using SemanticRagSearch.Api.Ingestion;

namespace SemanticRagSearch.Api.Controllers;

[ApiController]
[Route("api/v1/documents")]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentIngestionService _documentIngestionService;

    public DocumentsController(IDocumentIngestionService documentIngestionService)
    {
        _documentIngestionService = documentIngestionService ?? throw new ArgumentNullException(nameof(documentIngestionService));
    }

    [HttpPost]
    public async Task<ActionResult<DocumentIngestionResult>> IngestAsync(
        [FromBody] DocumentIngestionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        try
        {
            DocumentIngestionResult result = await _documentIngestionService.IngestAsync(request, cancellationToken);

            if (result.WasDuplicate)
                return Ok(result);

            return Created($"/api/v1/documents/{result.DocumentId}", result);
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

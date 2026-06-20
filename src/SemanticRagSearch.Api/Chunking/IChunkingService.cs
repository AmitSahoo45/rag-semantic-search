using SemanticRagSearch.Api.Domain;

namespace SemanticRagSearch.Api.Chunking;

public interface IChunkingService
{
    IReadOnlyList<Chunk> ChunkDocument(Document document, string content);
}

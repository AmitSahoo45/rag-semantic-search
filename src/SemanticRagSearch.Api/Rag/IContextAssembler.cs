using SemanticRagSearch.Api.Search;

namespace SemanticRagSearch.Api.Rag;

public interface IContextAssembler
{
    ContextAssemblyResult Assemble(
        IReadOnlyList<SemanticSearchMatch> matches,
        int maxTokens);
}

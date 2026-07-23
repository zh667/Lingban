using Lingban.Application.Common.Interfaces;

namespace Lingban.Application.Knowledge.Queries;

public record KnowledgeSearchResultDto(
    string Query,
    IReadOnlyList<KnowledgeHit> Hits);

/// <summary>知识库向量检索:返回 top-K 分块(带文档标题与章节锚点,供引用契约使用)。</summary>
public record SearchKnowledgeQuery(string Query, int TopK = 5) : IRequest<KnowledgeSearchResultDto>;

public class SearchKnowledgeQueryValidator : AbstractValidator<SearchKnowledgeQuery>
{
    public SearchKnowledgeQueryValidator()
    {
        RuleFor(query => query.Query).NotEmpty().MaximumLength(1000);
        RuleFor(query => query.TopK).InclusiveBetween(1, 20);
    }
}

public class SearchKnowledgeQueryHandler : IRequestHandler<SearchKnowledgeQuery, KnowledgeSearchResultDto>
{
    private readonly IEmbeddingService _embeddings;
    private readonly IKnowledgeSearch _search;

    public SearchKnowledgeQueryHandler(IEmbeddingService embeddings, IKnowledgeSearch search)
    {
        _embeddings = embeddings;
        _search = search;
    }

    public async Task<KnowledgeSearchResultDto> Handle(SearchKnowledgeQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> vectors = await _embeddings.EmbedAsync(new[] { request.Query }, cancellationToken);
        IReadOnlyList<KnowledgeHit> hits = await _search.SearchAsync(vectors[0], request.TopK, cancellationToken);
        return new KnowledgeSearchResultDto(request.Query, hits);
    }
}

using Lingban.Application.Common;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.Common.Verification;
using Lingban.Application.Knowledge.Commands;
using Lingban.Application.Knowledge.Queries;
using Lingban.Infrastructure.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = Lingban.Infrastructure.Data.ApplicationDbContext;

namespace Lingban.Application.FunctionalTests.Knowledge;

/// <summary>
/// M5 知识管道(CI 安全:确定性假 embedding,不依赖 Ollama):
/// docx 解析 → 分块 → 向量落库(shadow 列)→ pgvector 检索 → 内容完整性校验。
/// </summary>
public class KnowledgeTests : TestBase
{
    private static string Asset(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static async Task<int> IngestAsync(IServiceScope scope, string fileName)
    {
        var handler = new IngestDocumentCommandHandler(
            scope.ServiceProvider.GetRequiredService<IApplicationDbContext>(),
            new DocumentParser(),
            new FakeEmbeddingService(),
            scope.ServiceProvider.GetRequiredService<IKnowledgeChunkWriter>());
        return await handler.Handle(
            new IngestDocumentCommand { FileName = fileName, Content = await File.ReadAllBytesAsync(Asset(fileName)) },
            CancellationToken.None);
    }

    [Test]
    public async Task IngestParseChunkEmbedSearchAndVerify()
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        await IngestAsync(scope, "SOP-回流焊作业指导书.docx");
        await IngestAsync(scope, "SOP-贴片机日点检表.docx");
        await IngestAsync(scope, "SOP-连锡缺陷处理规程.docx");

        var searchHandler = new SearchKnowledgeQueryHandler(
            new FakeEmbeddingService(),
            scope.ServiceProvider.GetRequiredService<IKnowledgeSearch>());
        var query = new SearchKnowledgeQuery("回流焊回流区峰值温度是多少", 3);
        KnowledgeSearchResultDto result = await searchHandler.Handle(query, CancellationToken.None);

        result.Hits.ShouldNotBeEmpty();
        result.Hits[0].DocumentTitle.ShouldBe("回流焊作业指导书");
        result.Hits.ShouldContain(hit => hit.Text.Contains("245"));

        // 内容完整性校验(独立 SQL 路径)。
        var verifier = scope.ServiceProvider.GetRequiredService<IFactVerifier>();
        (await verifier.VerifyAsync(ToolNames.SearchKnowledge, query, result))
            .Status.ShouldBe(VerificationStatus.Verified);

        // 篡改分块文本 → 抓获。
        var tampered = result with
        {
            Hits = result.Hits.Select((hit, index) => index == 0 ? hit with { Text = hit.Text + "。峰值 999 度" } : hit).ToList()
        };
        (await verifier.VerifyAsync(ToolNames.SearchKnowledge, query, tampered))
            .Status.ShouldBe(VerificationStatus.Discrepancy);
    }

    [Test]
    public async Task ReingestReplacesOldChunks()
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        await IngestAsync(scope, "SOP-回流焊作业指导书.docx");
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int firstCount = await context.KnowledgeChunks.CountAsync();

        await IngestAsync(scope, "SOP-回流焊作业指导书.docx");
        (await context.KnowledgeDocuments.CountAsync()).ShouldBe(1);
        (await context.KnowledgeChunks.CountAsync()).ShouldBe(firstCount);
    }

    [Test]
    public async Task InjectionSampleIsStoredAsPlainDataNotExecuted()
    {
        // 对抗语料按原文入库(它只是文本);执行与否由 M5 注入 eval 在 LLM 层验证。
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        await IngestAsync(scope, "SOP-连锡缺陷处理规程.docx");
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await context.KnowledgeChunks.AnyAsync(chunk => chunk.Text.Contains("忽略之前的所有指令")))
            .ShouldBeTrue();
    }

    /// <summary>确定性 1024 维向量:关键词 → 正交基,检索行为可预测且与真实服务同维。</summary>
    internal sealed class FakeEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 1024;

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            IReadOnlyList<float[]> result = texts.Select(text =>
            {
                var vector = new float[1024];
                int basis = text.Contains("回流") ? 0 : text.Contains("点检") ? 1 : text.Contains("连锡") ? 2 : 3;
                vector[basis] = 1f;
                if (text.Contains("峰值"))
                {
                    vector[10] = 0.5f;
                }

                return vector;
            }).ToList();
            return Task.FromResult(result);
        }
    }
}

using Lingban.Application.Knowledge.Commands;
using MediatR;

namespace Lingban.Web.Endpoints;

/// <summary>知识库文档上传(MesData 角色;大小限制由命令校验器执行)。</summary>
public class Knowledge : IEndpointGroup
{
    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.MapPost(Upload, "/documents")
            .RequireAuthorization("KnowledgeWrite")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(6 * 1024 * 1024))
            .DisableAntiforgery();
    }

    [EndpointSummary("Upload a knowledge document (.docx/.md/.txt)")]
    public static async Task<IResult> Upload(IFormFile file, ISender sender, CancellationToken cancellationToken)
    {
        const long maxBytes = 5 * 1024 * 1024;
        if (file.Length > maxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, cancellationToken);
        int documentId = await sender.Send(
            new IngestDocumentCommand { FileName = file.FileName, Content = stream.ToArray() },
            cancellationToken);
        return Results.Ok(new { documentId });
    }
}

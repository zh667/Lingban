using Lingban.Application.Knowledge.Commands;
using MediatR;

namespace Lingban.Web.Endpoints;

/// <summary>知识库文档上传(MesData 角色;大小限制由命令校验器执行)。</summary>
public class Knowledge : IEndpointGroup
{
    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.MapPost(Upload, "/documents")
            .RequireAuthorization("MesData")
            .DisableAntiforgery();
    }

    [EndpointSummary("Upload a knowledge document (.docx/.md/.txt)")]
    public static async Task<IResult> Upload(IFormFile file, ISender sender, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        int documentId = await sender.Send(
            new IngestDocumentCommand { FileName = file.FileName, Content = stream.ToArray() },
            cancellationToken);
        return Results.Ok(new { documentId });
    }
}

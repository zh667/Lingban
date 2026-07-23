using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests.Knowledge;

/// <summary>七审 #2 回归钉:MesReader 只能读,KnowledgeWrite 只对管理员/知识管理员放行。</summary>
public class KnowledgePolicyTests : TestBase
{
    [TestCase("MesReader", false)]
    [TestCase("Administrator", true)]
    [TestCase("KnowledgeManager", true)]
    public async Task KnowledgeWritePolicyEnforcesRoles(string role, bool expected)
    {
        using var scope = FunctionalTestSetup.ScopeFactory.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(ClaimTypes.Role, role) }, "test"));

        AuthorizationResult result = await authorization.AuthorizeAsync(principal, null, "KnowledgeWrite");
        result.Succeeded.ShouldBe(expected);
    }
}

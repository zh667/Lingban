using Lingban.Application.Common.Behaviours;
using Lingban.Application.Common.Interfaces;
using Lingban.Application.WorkOrders.Commands;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lingban.Application.UnitTests.Common.Behaviours;

public class RequestLoggerTests
{
    private Mock<ILogger<CreateWorkOrderCommand>> _logger = null!;
    private Mock<IUser> _user = null!;
    private Mock<IIdentityService> _identityService = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new Mock<ILogger<CreateWorkOrderCommand>>();
        _user = new Mock<IUser>();
        _identityService = new Mock<IIdentityService>();
    }

    [Test]
    public async Task ShouldCallGetUserNameAsyncOnceIfAuthenticated()
    {
        _user.Setup(x => x.Id).Returns(Guid.NewGuid().ToString());

        var requestLogger = new LoggingBehaviour<CreateWorkOrderCommand>(_logger.Object, _user.Object, _identityService.Object);

        await requestLogger.Process(new CreateWorkOrderCommand { Code = "WO-LOG", ProductId = 1, ProductionLineId = 1, PlannedQuantity = 1m }, new CancellationToken());

        _identityService.Verify(i => i.GetUserNameAsync(It.IsAny<string>()), Times.Once);
    }

    [Test]
    public async Task ShouldNotCallGetUserNameAsyncOnceIfUnauthenticated()
    {
        var requestLogger = new LoggingBehaviour<CreateWorkOrderCommand>(_logger.Object, _user.Object, _identityService.Object);

        await requestLogger.Process(new CreateWorkOrderCommand { Code = "WO-LOG", ProductId = 1, ProductionLineId = 1, PlannedQuantity = 1m }, new CancellationToken());

        _identityService.Verify(i => i.GetUserNameAsync(It.IsAny<string>()), Times.Never);
    }
}

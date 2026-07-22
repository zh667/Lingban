using Lingban.Domain.Constants;
using Lingban.Domain.Entities;
using Lingban.Domain.Entities.Calendar;
using Lingban.Domain.Entities.Materials;
using Lingban.Domain.Entities.Production;
using Lingban.Domain.Entities.Quality;
using Lingban.Domain.ValueObjects;
using Lingban.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lingban.Infrastructure.Data;

public static class InitialiserExtensions
{
    public static async Task InitialiseDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitialiser>();

        await initialiser.InitialiseAsync();
        await initialiser.SeedAsync();
    }
}

public class ApplicationDbContextInitialiser
{
    private readonly ILogger<ApplicationDbContextInitialiser> _logger;
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public ApplicationDbContextInitialiser(ILogger<ApplicationDbContextInitialiser> logger, ApplicationDbContext context, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        _logger = logger;
        _context = context;
        _userManager = userManager;
        _roleManager = roleManager;
    }

    public async Task InitialiseAsync()
    {
        try
        {
            // See https://jasontaylor.dev/ef-core-database-initialisation-strategies
            await _context.Database.EnsureDeletedAsync();
            await _context.Database.EnsureCreatedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initialising the database.");
            throw;
        }
    }

    public async Task SeedAsync()
    {
        try
        {
            await TrySeedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    public async Task TrySeedAsync()
    {
        // Default roles
        var administratorRole = new IdentityRole(Roles.Administrator);

        if (_roleManager.Roles.All(r => r.Name != administratorRole.Name))
        {
            await _roleManager.CreateAsync(administratorRole);
        }

        // Default users
        var administrator = new ApplicationUser { UserName = "administrator@localhost", Email = "administrator@localhost" };

        if (_userManager.Users.All(u => u.UserName != administrator.UserName))
        {
            await _userManager.CreateAsync(administrator, "Administrator1!");
            if (!string.IsNullOrWhiteSpace(administratorRole.Name))
            {
                await _userManager.AddToRolesAsync(administrator, new[] { administratorRole.Name });
            }
        }

        // Default data
        // Seed, if necessary
        if (!_context.TodoLists.Any())
        {
            _context.TodoLists.Add(new TodoList
            {
                Title = "Tasks",
                Colour = Colour.Green,
                Items =
                {
                    new TodoItem { Title = "Make a todo list 📃" },
                    new TodoItem { Title = "Check off the first item ✅" },
                    new TodoItem { Title = "Realise you've already done two things on the list! 🤯"},
                    new TodoItem { Title = "Reward yourself with a nice, long nap 🏆" },
                }
            });

            await _context.SaveChangesAsync();
        }

        await TrySeedMesDataAsync();
    }

    /// <summary>
    /// SMT 电子装配演示数据:两级批次谱系(来料 → PCBA → 整机),
    /// 供开发调试与后续 Agent 工具演示;测试自建数据,不依赖这里。
    /// </summary>
    private async Task TrySeedMesDataAsync()
    {
        if (_context.Products.Any())
        {
            return;
        }

        var day = new Shift { Code = "DAY", Name = "白班", StartLocalTime = new TimeOnly(8, 0), EndLocalTime = new TimeOnly(20, 0) };
        var night = new Shift { Code = "NIGHT", Name = "夜班", StartLocalTime = new TimeOnly(20, 0), EndLocalTime = new TimeOnly(8, 0) };
        _context.Shifts.AddRange(day, night);

        _context.DefectTypes.AddRange(
            new DefectType { Code = "SOLDER-BRIDGE", Name = "连锡" },
            new DefectType { Code = "MISSING-COMP", Name = "缺件" },
            new DefectType { Code = "COSMETIC", Name = "外观不良" });

        var line = new ProductionLine { Code = "SMT-1", Name = "SMT 一线" };
        var smtStation = new Workstation { Code = "ST-SMT", Name = "贴片工位", ProductionLine = line };
        var asmStation = new Workstation { Code = "ST-ASM", Name = "组装工位", ProductionLine = line };
        _context.ProductionLines.Add(line);
        _context.Workstations.AddRange(smtStation, asmStation);

        var pcb = new Product { Code = "RM-PCB-01", Name = "裸板", UnitOfMeasure = "PCS" };
        var ic = new Product { Code = "RM-IC-01", Name = "主控 IC", UnitOfMeasure = "PCS" };
        var resistor = new Product { Code = "RM-RES-01", Name = "贴片电阻", UnitOfMeasure = "PCS" };
        var pcba = new Product { Code = "SFG-PCBA-01", Name = "PCBA 半成品", UnitOfMeasure = "PCS" };
        var controller = new Product { Code = "FG-CTRL-01", Name = "控制器成品", UnitOfMeasure = "PCS" };
        _context.Products.AddRange(pcb, ic, resistor, pcba, controller);

        _context.BomLines.AddRange(
            new BomLine { Product = pcba, ComponentProduct = pcb, QuantityPer = 1m },
            new BomLine { Product = pcba, ComponentProduct = ic, QuantityPer = 1m },
            new BomLine { Product = pcba, ComponentProduct = resistor, QuantityPer = 10m },
            new BomLine { Product = controller, ComponentProduct = pcba, QuantityPer = 1m });

        await _context.SaveChangesAsync();

        var t0 = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

        var pcbLot = MaterialLot.Create("L-PCB-2607A", pcb.Id, 200m, "PCS", t0, "SUP-PCB-88");
        var icLot = MaterialLot.Create("L-IC-2607A", ic.Id, 200m, "PCS", t0, "SUP-IC-31");
        var resLot = MaterialLot.Create("L-RES-2607A", resistor.Id, 5000m, "PCS", t0, "SUP-RES-07");
        _context.MaterialLots.AddRange(pcbLot, icLot, resLot);
        await _context.SaveChangesAsync();

        // 一级:PCBA 工单,消耗来料,产出 PCBA 批次。
        var pcbaOrder = WorkOrder.Create("WO-SEED-01", pcba.Id, line.Id, 100m, "PCS");
        pcbaOrder.Release();
        pcbaOrder.Start(t0.AddHours(1));
        pcbaOrder.RecordConsumption(pcbLot, 100m, smtStation.Id, t0.AddHours(2), "seed");
        pcbaOrder.RecordConsumption(icLot, 100m, smtStation.Id, t0.AddHours(2), "seed");
        pcbaOrder.RecordConsumption(resLot, 1000m, smtStation.Id, t0.AddHours(2), "seed");
        var pcbaLot = pcbaOrder.ProduceLot("L-PCBA-2607A", 100m, t0.AddHours(10));
        pcbaOrder.ReportProduction(100m, 98m, 2m, 0m);
        pcbaOrder.Complete(t0.AddHours(11));
        _context.WorkOrders.Add(pcbaOrder);
        await _context.SaveChangesAsync();

        // 二级:整机工单,消耗 PCBA 批次,产出成品批次。
        var fgOrder = WorkOrder.Create("WO-SEED-02", controller.Id, line.Id, 50m, "PCS");
        fgOrder.Release();
        fgOrder.Start(t0.AddDays(1));
        fgOrder.RecordConsumption(pcbaLot, 50m, asmStation.Id, t0.AddDays(1).AddHours(1), "seed");
        fgOrder.ProduceLot("L-CTRL-2607A", 50m, t0.AddDays(1).AddHours(9));
        fgOrder.ReportProduction(50m, 50m, 0m, 0m);
        fgOrder.Complete(t0.AddDays(1).AddHours(10));
        _context.WorkOrders.Add(fgOrder);

        // 进行中的工单,供状态类工具演示。
        var wipOrder = WorkOrder.Create("WO-SEED-03", controller.Id, line.Id, 30m, "PCS");
        wipOrder.Release();
        wipOrder.Start(t0.AddDays(2));
        _context.WorkOrders.Add(wipOrder);

        await _context.SaveChangesAsync();
    }
}

using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests.BackgroundProcessing;

public class DatabaseBudgetTests
{
    private static DatabaseBudget Plan() => new()
    {
        DeploymentConnectionBudget = 105,
        ApiPoolSize = 8,
        WorkerPoolSize = 6,
        ApiReplicas = 1,
        WorkerReplicas = 1,
        MigrationConnections = 2,
        MaintenanceConnections = 2
    };

    [Fact]
    public void The_documented_free_plan_plan_fits()
    {
        var plan = Plan();

        Assert.Equal(18, plan.PlannedConnections);
        Assert.Empty(plan.Validate("api", roleMaximumPoolSize: 8, roleConnectionBudget: 40));
        Assert.Empty(plan.Validate("worker", roleMaximumPoolSize: 6, roleConnectionBudget: 40));
    }

    [Fact]
    public void A_pool_larger_than_the_declared_role_size_is_rejected()
    {
        var problems = Plan().Validate("worker", roleMaximumPoolSize: 8, roleConnectionBudget: 40);

        Assert.Contains(problems, problem => problem.Contains("WorkerPoolSize"));
    }

    [Fact]
    public void A_pool_larger_than_the_per_role_budget_is_rejected()
    {
        var problems = Plan().Validate("api", roleMaximumPoolSize: 8, roleConnectionBudget: 4);

        Assert.Contains(problems, problem => problem.Contains("ConnectionBudget"));
    }

    [Fact]
    public void Scaling_the_worker_beyond_the_compute_is_rejected()
    {
        var plan = Plan();
        plan.WorkerReplicas = 20; // 20 x 6 + 8 + 2 + 2 = 132 > 105

        var problems = plan.Validate("worker", roleMaximumPoolSize: 6, roleConnectionBudget: 40);

        Assert.Contains(problems, problem => problem.Contains("exceeds Database:DeploymentConnectionBudget"));
    }

    [Fact]
    public void A_bigger_compute_only_needs_a_bigger_budget_value()
    {
        var plan = Plan();
        plan.WorkerReplicas = 4;
        plan.DeploymentConnectionBudget = 443; // 1 CU

        Assert.Empty(plan.Validate("worker", roleMaximumPoolSize: 6, roleConnectionBudget: 40));
        Assert.Equal(36, plan.PlannedConnections);
    }

    [Fact]
    public void The_summary_is_ascii_and_contains_no_connection_string()
    {
        var summary = Plan().Describe("api", 8);

        Assert.Contains("18 of 105", summary);
        Assert.DoesNotContain("@", summary);
        Assert.DoesNotContain("postgres", summary, StringComparison.OrdinalIgnoreCase);
    }
}

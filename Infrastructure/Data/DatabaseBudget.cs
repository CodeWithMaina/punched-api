namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// The deployment's PostgreSQL connection budget.
///
/// <para><b>Why this exists.</b> Each container has its own Npgsql pool, so the
/// database sees the <i>sum</i> of every role's pool, not one shared number. Neon's
/// 0.25 CU compute allows 112 connections (105 usable after the 7 superuser slots);
/// 0.5 CU allows 225, 1 CU 450, 2 CU 901. A copy/paste of the same environment file
/// into both containers is the classic way to double a budget silently, so the total
/// is computed from configuration and validated at startup.</para>
///
/// <para>The arithmetic is a pure function so it can be tested without a database.</para>
/// </summary>
public sealed class DatabaseBudget
{
    public const string SectionName = "Database";

    public int DeploymentConnectionBudget { get; set; } = 105;
    public int ApiPoolSize { get; set; } = 8;
    public int WorkerPoolSize { get; set; } = 6;
    public int ApiReplicas { get; set; } = 1;
    public int WorkerReplicas { get; set; } = 1;
    public int MigrationConnections { get; set; } = 2;
    public int MaintenanceConnections { get; set; } = 2;

    /// <summary>Connections this deployment can hold at once, on paper.</summary>
    public int PlannedConnections =>
        Math.Max(0, ApiPoolSize) * Math.Max(0, ApiReplicas) +
        Math.Max(0, WorkerPoolSize) * Math.Max(0, WorkerReplicas) +
        Math.Max(0, MigrationConnections) +
        Math.Max(0, MaintenanceConnections);

    /// <summary>
    /// Validates the plan for the process that is starting.
    /// </summary>
    /// <param name="role">"api" or "worker".</param>
    /// <param name="roleMaximumPoolSize">The resolved <c>Database:MaximumPoolSize</c> for this process.</param>
    /// <param name="roleConnectionBudget">The resolved per-role ceiling.</param>
    public IReadOnlyList<string> Validate(string role, int roleMaximumPoolSize, int roleConnectionBudget)
    {
        var problems = new List<string>();

        if (DeploymentConnectionBudget is < 1 or > 4000)
            problems.Add($"Database:DeploymentConnectionBudget must be between 1 and 4000 (got {DeploymentConnectionBudget}).");
        if (ApiPoolSize < 1) problems.Add($"Database:ApiPoolSize must be >= 1 (got {ApiPoolSize}).");
        if (WorkerPoolSize < 1) problems.Add($"Database:WorkerPoolSize must be >= 1 (got {WorkerPoolSize}).");
        if (ApiReplicas < 1) problems.Add($"Database:ApiReplicas must be >= 1 (got {ApiReplicas}).");
        if (WorkerReplicas < 1) problems.Add($"Database:WorkerReplicas must be >= 1 (got {WorkerReplicas}).");
        if (MigrationConnections < 1) problems.Add($"Database:MigrationConnections must be >= 1 (got {MigrationConnections}).");
        if (MaintenanceConnections < 0) problems.Add($"Database:MaintenanceConnections must be >= 0 (got {MaintenanceConnections}).");

        if (problems.Count > 0) return problems;

        // The pool this process actually opens must be the pool the plan says it opens.
        // Divergence means the environment file and the plan disagree — exactly the
        // situation that silently over-commits a small compute.
        var isWorkerRole = string.Equals(role, "worker", StringComparison.OrdinalIgnoreCase);
        var declared = isWorkerRole ? WorkerPoolSize : ApiPoolSize;
        if (roleMaximumPoolSize > declared)
        {
            var declaredKey = isWorkerRole ? "WorkerPoolSize" : "ApiPoolSize";
            problems.Add(
                $"Database:MaximumPoolSize ({roleMaximumPoolSize}) exceeds the declared Database:{declaredKey} ({declared}) " +
                $"for role '{role}'. Update the deployment plan (or the pool size) so the budget reflects reality.");
        }

        if (roleMaximumPoolSize > roleConnectionBudget)
        {
            problems.Add(
                $"Database:MaximumPoolSize ({roleMaximumPoolSize}) exceeds Database:ConnectionBudget ({roleConnectionBudget}) for role '{role}'.");
        }

        if (PlannedConnections > DeploymentConnectionBudget)
        {
            problems.Add(
                $"The planned connection total ({PlannedConnections}: " +
                $"api {ApiPoolSize}×{ApiReplicas} + worker {WorkerPoolSize}×{WorkerReplicas} + " +
                $"migrations {MigrationConnections} + maintenance {MaintenanceConnections}) exceeds " +
                $"Database:DeploymentConnectionBudget ({DeploymentConnectionBudget}). " +
                "Lower a pool size, reduce replicas, or raise the budget to match the compute's max_connections.");
        }

        return problems;
    }

    /// <summary>One-line summary for the startup log. Contains no host names or credentials.</summary>
    public string Describe(string role, int roleMaximumPoolSize) =>
        $"role={role} pool<={roleMaximumPoolSize}; api {ApiPoolSize}x{ApiReplicas} + worker {WorkerPoolSize}x{WorkerReplicas} + " +
        $"migrations {MigrationConnections} + maintenance {MaintenanceConnections} = {PlannedConnections} of " +
        $"{DeploymentConnectionBudget} usable connections";
}

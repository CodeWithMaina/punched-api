using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Services;

namespace PunchedApi.Tests;

/// <summary>
/// Shared test infrastructure helpers.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a no-op ILogger&lt;T&gt; for use in tests.
    /// </summary>
    public static ILogger<T> CreateLogger<T>() => NullLogger<T>.Instance;
}

/// <summary>
/// Configurable module-entitlement stub for module-gate tests (§12/§16).
/// Construct with the module keys that should be enabled, then flip at runtime
/// to assert read-vs-write gate semantics against the same fixture.
/// </summary>
internal sealed class StubModuleEntitlements : IModuleEntitlementService
{
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);

    public StubModuleEntitlements(params string[] enabledModules)
    {
        foreach (var module in enabledModules)
            _enabled.Add(module);
    }

    /// <summary>Enables a module at runtime (fluent).</summary>
    public StubModuleEntitlements Enable(string moduleKey)
    {
        _enabled.Add(moduleKey);
        return this;
    }

    /// <summary>Disables a module at runtime (fluent) — simulates a downgrade.</summary>
    public StubModuleEntitlements Disable(string moduleKey)
    {
        _enabled.Remove(moduleKey);
        return this;
    }

    public Task<ModuleEntitlementResult> GetBusinessModulesAsync(Guid businessId, Guid? userId = null) =>
        Task.FromResult(new ModuleEntitlementResult());

    public Task<bool> IsModuleEnabledAsync(Guid businessId, string moduleKey) =>
        Task.FromResult(_enabled.Contains(moduleKey));

    public Task<HashSet<string>> GetEffectiveModuleKeysAsync(Guid businessId) =>
        Task.FromResult(new HashSet<string>(_enabled, StringComparer.Ordinal));

    public void Invalidate(Guid businessId) { }

    public IReadOnlyList<string> ValidateConfiguration(IEnumerable<(string ModuleKey, bool Enabled)> overrides) =>
        Array.Empty<string>();
}

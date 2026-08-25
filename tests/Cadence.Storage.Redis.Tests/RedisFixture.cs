using Cadence.Storage.Conformance;
using Testcontainers.Redis;
using Xunit;

namespace Cadence.Storage.Redis.Tests;

/// <summary>
/// One Redis container for the whole test assembly, with a fresh key prefix per test.
/// </summary>
/// <remarks>
/// <para>
/// The SQL fixture isolates tests with a database each; Redis has numbered databases but only
/// sixteen of them, so this isolates by prefix instead. That is weaker on paper — a test could
/// reach another's keys by constructing the name — and stronger in practice for this suite, because
/// the prefix is itself a configuration option every component already routes through, so a test
/// that leaked would be a bug in the code under test rather than in the fixture.
/// </para>
/// <para>
/// When no Docker daemon is reachable the fixture records why and every test that needs it skips,
/// so the rest of the suite stays runnable on a machine without Docker.
/// </para>
/// </remarks>
public sealed class RedisFixture : IAsyncLifetime
{
    /// <summary>The Redis image the tests run against.</summary>
    /// <remarks>
    /// Named explicitly rather than left to the library's default: which Redis the tests ran
    /// against is part of what a green build means, and a floating default can change under us
    /// between package versions.
    /// </remarks>
    private const string RedisImage = "redis:7.4-alpine";

    private RedisContainer? _container;
    private int _prefixCounter;

    /// <summary>Why the container is unavailable, or null when it started.</summary>
    public string? SkipReason { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (DockerDaemon.SkipReason is { } noDocker)
        {
            SkipReason = noDocker;
            return;
        }

        try
        {
            _container = new RedisBuilder(RedisImage).Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            // Recorded rather than thrown, so the suite skips instead of failing.
            SkipReason = $"A Redis container could not be started: {ex.Message}";
            _container = null;
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Skips the calling test when there is no container to talk to.</summary>
    public void RequireContainer() => Skip.If(SkipReason is not null, SkipReason ?? string.Empty);

    /// <summary>Options pointing at a key space no other test uses.</summary>
    /// <param name="label">Short label identifying the caller, used in the prefix.</param>
    /// <param name="configure">Adjusts the options before they are validated.</param>
    public RedisStorageOptions CreateOptions(
        string label,
        Action<RedisStorageOptions>? configure = null)
    {
        RequireContainer();

        var prefix = $"{{cadence-{Sanitise(label)}-{Interlocked.Increment(ref _prefixCounter)}}}:";

        var options = new RedisStorageOptions
        {
            ConnectionString = _container!.GetConnectionString(),
            KeyPrefix = prefix,
        };

        configure?.Invoke(options);
        options.Validate();

        return options;
    }

    private static string Sanitise(string label)
        => new([.. label.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>Shares one container across every test class in the assembly.</summary>
[CollectionDefinition(Name)]
public sealed class RedisCollectionDefinition : ICollectionFixture<RedisFixture>
{
    /// <summary>The collection name test classes opt into.</summary>
    public const string Name = "redis";
}

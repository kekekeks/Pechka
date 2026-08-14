using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.Jobs;

namespace Pechka.AspNet.Tests;

public class JobRegistryTests
{
    private static BackgroundJobRegistry Registry(params BackgroundJobRegistration[] registrations) =>
        new(registrations);

    [Fact]
    public void Default_Identifier_Is_The_Job_Type_Full_Name()
    {
        var services = new ServiceCollection();
        services.AddBackgroundJob<TestJob, TestJobHandler>();
        var registration = services.BuildServiceProvider().GetRequiredService<BackgroundJobRegistration>();
        Assert.Equal(typeof(TestJob).FullName, registration.Identifier);
        Assert.Equal(typeof(TestJob), registration.JobType);
        Assert.False(registration.RetryTransientFailures);
    }

    [Fact]
    public void Custom_Identifier_Is_Honored()
    {
        var services = new ServiceCollection();
        services.AddBackgroundJob<TestJob, TestJobHandler>("custom-id", retryTransientFailures: true);
        var registration = services.BuildServiceProvider().GetRequiredService<BackgroundJobRegistration>();
        Assert.Equal("custom-id", registration.Identifier);
        Assert.True(registration.RetryTransientFailures);
    }

    [Fact]
    public void Registrations_Are_Looked_Up_By_Identifier_And_Job_Type()
    {
        var registration = new BackgroundJobRegistration<TestJob, TestJobHandler>("id", false);
        var registry = Registry(registration);
        Assert.Same(registration, registry.TryGetByIdentifier("id"));
        Assert.Same(registration, registry.GetByJobType(typeof(TestJob)));
    }

    [Fact]
    public void Unknown_Identifier_Returns_Null()
    {
        Assert.Null(Registry().TryGetByIdentifier("missing"));
    }

    [Fact]
    public void Unknown_Job_Type_Throws()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Registry().GetByJobType(typeof(TestJob)));
        Assert.Contains("is not registered", e.Message);
    }

    [Fact]
    public void Duplicate_Identifier_Throws()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Registry(
            new BackgroundJobRegistration<TestJob, TestJobHandler>("same", false),
            new BackgroundJobRegistration<RetryableTestJob, RetryableTestJobHandler>("same", false)));
        Assert.Contains("Duplicate background job identifier", e.Message);
    }

    [Fact]
    public void Duplicate_Job_Type_Throws()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Registry(
            new BackgroundJobRegistration<TestJob, TestJobHandler>("a", false),
            new BackgroundJobRegistration<TestJob, TestJobHandler>("b", false)));
        Assert.Contains("registered more than once", e.Message);
    }
}

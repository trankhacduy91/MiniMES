using MediatR;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Api.Controllers;

namespace MiniMes.IntegrationTests;

// "One pattern only" (CLAUDE.md): a business controller gets nothing but ISender, so no service
// class can sneak back in beside MediatR. Lives here because UnitTests cannot reference Api.
public class ControllerConventionTests
{
    // Not use cases: dev-only endpoints that throw on purpose to show the error format.
    private static readonly Type[] Exempt = [typeof(DebugController)];

    [Fact]
    public void Business_controllers_depend_only_on_ISender()
    {
        var offenders = typeof(ItemsController).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(ControllerBase)) && !t.IsAbstract && !Exempt.Contains(t))
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType != typeof(ISender))))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(offenders);
    }
}

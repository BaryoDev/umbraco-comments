using System.Reflection;
using Umbraco.Cms.Core.Security;

namespace BaryoDev.Umbraco.Comments.IntegrationTests;

// Answers named members; everything else returns a default.
public class Stub : DispatchProxy
{
    public Dictionary<string, Func<object?[], object?>> On { get; } = new();

    public static T Make<T>(params (string Member, Func<object?[], object?> Answer)[] answers)
        where T : class
    {
        var proxy = Create<T, Stub>();
        foreach (var (member, answer) in answers)
        {
            ((Stub)(object)proxy).On[member] = answer;
        }

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (On.TryGetValue(targetMethod!.Name, out var answer))
        {
            return answer(args ?? []);
        }

        var type = targetMethod.ReturnType;
        if (type == typeof(string))
        {
            return "/";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        }

        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

public sealed class FixedAccessor(IBackOfficeSecurity? security) : IBackOfficeSecurityAccessor
{
    public IBackOfficeSecurity BackOfficeSecurity { get; } = security!;
}

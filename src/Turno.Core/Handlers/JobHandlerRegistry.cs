using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Turno.Core.Domain;

namespace Turno.Core.Handlers;

public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, Type> handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Types => handlers.Keys.Order(StringComparer.Ordinal).ToArray();

    public int Count => handlers.Count;

    public void Register(string type, Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        var name = JobTypeNames.Normalize(type);
        if (!JobTypeNames.IsValid(name))
        {
            throw new ArgumentException($"Nome de tipo inválido: '{type}'. Use letras minúsculas, números, ponto, hífen ou sublinhado.", nameof(type));
        }

        if (!typeof(IJobHandler).IsAssignableFrom(handlerType) || handlerType.IsAbstract || handlerType.IsInterface)
        {
            throw new ArgumentException($"{handlerType.FullName} precisa ser uma classe concreta que implementa {nameof(IJobHandler)}.", nameof(handlerType));
        }

        if (handlers.TryGetValue(name, out var existing) && existing != handlerType)
        {
            throw new InvalidOperationException($"O tipo '{name}' já está associado a {existing.FullName}.");
        }

        handlers[name] = handlerType;
    }

    public bool TryGet(string type, out Type handlerType)
    {
        if (handlers.TryGetValue(JobTypeNames.Normalize(type), out var found))
        {
            handlerType = found;
            return true;
        }

        handlerType = typeof(void);
        return false;
    }
}

public static class JobHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string? type = null)
        where THandler : class, IJobHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        var name = type ?? typeof(THandler).GetCustomAttribute<JobTypeAttribute>()?.Name
            ?? throw new InvalidOperationException($"Informe o tipo da tarefa ou use [JobType] em {typeof(THandler).Name}.");
        GetRegistry(services).Register(name, typeof(THandler));
        services.TryAddTransient<THandler>();
        return services;
    }

    public static IServiceCollection AddJobHandlersFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        var registry = GetRegistry(services);
        var candidates = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IJobHandler).IsAssignableFrom(t))
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<JobTypeAttribute>()))
            .Where(c => c.Attribute is not null);

        foreach (var (handlerType, attribute) in candidates)
        {
            registry.Register(attribute!.Name, handlerType);
            services.TryAddTransient(handlerType);
        }

        return services;
    }

    public static JobHandlerRegistry GetRegistry(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(JobHandlerRegistry) && d.ImplementationInstance is not null);
        if (descriptor?.ImplementationInstance is JobHandlerRegistry existing)
        {
            return existing;
        }

        var registry = new JobHandlerRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}

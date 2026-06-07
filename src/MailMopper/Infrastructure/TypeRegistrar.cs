using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace MailMopper.Infrastructure;

public sealed class TypeRegistrar(IServiceCollection builder) : ITypeRegistrar
{
    private readonly IServiceCollection _builder = builder ?? throw new ArgumentNullException(nameof(builder));

    public ITypeResolver Build() =>
        new TypeResolver(_builder.BuildServiceProvider());

    public void Register(Type service, Type implementation) =>
        _builder.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        _builder.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) =>
        _builder.AddSingleton(service, _ => factory());
}

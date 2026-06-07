using MailMopper.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MailMopper.Tests.Infrastructure;

public class TypeResolverTests
{
    [Fact]
    public void Constructor_NullProvider_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new TypeResolver(null!));
        Assert.Equal("provider", ex.ParamName);
    }

    [Fact]
    public void Resolve_NullType_ReturnsNull()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        using var resolver = new TypeResolver(provider);

        var result = resolver.Resolve(null);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_RegisteredType_ReturnsInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton("hello");
        var provider = services.BuildServiceProvider();
        using var resolver = new TypeResolver(provider);

        var result = resolver.Resolve(typeof(string));

        Assert.Equal("hello", result);
    }

    [Fact]
    public void Resolve_UnregisteredType_ReturnsNull()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        using var resolver = new TypeResolver(provider);

        var result = resolver.Resolve(typeof(string));

        Assert.Null(result);
    }

    [Fact]
    public void Dispose_ProviderIsDisposable_CallsDispose()
    {
        var disposableProvider = new DisposableServiceProvider();
        var resolver = new TypeResolver(disposableProvider);

        resolver.Dispose();

        Assert.True(disposableProvider.DisposeCalled);
    }

    private sealed class DisposableServiceProvider : IServiceProvider, IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public object? GetService(Type serviceType) => null;

        public void Dispose()
        {
            DisposeCalled = true;
        }
    }
}

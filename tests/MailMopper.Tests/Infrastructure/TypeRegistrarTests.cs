using MailMopper.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MailMopper.Tests.Infrastructure;

public class TypeRegistrarTests
{
    [Fact]
    public void Constructor_NullBuilder_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new TypeRegistrar(null!));
        Assert.Equal("builder", ex.ParamName);
    }

    [Fact]
    public void Register_TypeToImplementation_CanResolveViaBuiltResolver()
    {
        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);
        registrar.Register(typeof(IList<string>), typeof(List<string>));

        var resolver = registrar.Build();
        var resolved = resolver.Resolve(typeof(IList<string>));

        Assert.NotNull(resolved);
        Assert.IsType<List<string>>(resolved);
    }

    [Fact]
    public void RegisterInstance_CanResolveSameObject()
    {
        var expected = "hello-world";
        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);
        registrar.RegisterInstance(typeof(string), expected);

        var resolver = registrar.Build();
        var resolved = resolver.Resolve(typeof(string));

        Assert.Same(expected, resolved);
    }

    [Fact]
    public void RegisterLazy_DefersFactoryUntilResolution()
    {
        var callCount = 0;
        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);
        registrar.RegisterLazy(typeof(string), () =>
        {
            callCount++;
            return "lazy-value";
        });

        Assert.Equal(0, callCount);

        var resolver = registrar.Build();
        Assert.Equal(0, callCount);

        resolver.Resolve(typeof(string));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void RegisterLazy_CachesInstance_OnlyInvokesFactoryOnce()
    {
        var callCount = 0;
        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);
        registrar.RegisterLazy(typeof(string), () =>
        {
            callCount++;
            return "cached-value";
        });

        var resolver = registrar.Build();
        resolver.Resolve(typeof(string));
        resolver.Resolve(typeof(string));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Build_ReturnsNonNullTypeResolver()
    {
        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);

        var resolver = registrar.Build();

        Assert.NotNull(resolver);
        Assert.IsType<TypeResolver>(resolver);
    }

    [Fact]
    public void Register_ThenBuild_EndToEndResolves()
    {
        var services = new ServiceCollection();
        var registrar = new TypeRegistrar(services);
        registrar.Register(typeof(List<int>), typeof(List<int>));

        var resolver = registrar.Build();
        var resolved = resolver.Resolve(typeof(List<int>));

        Assert.NotNull(resolved);
        Assert.IsType<List<int>>(resolved);
    }
}

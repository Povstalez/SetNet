using System;
using Microsoft.Extensions.DependencyInjection;
using SetNet.Core.Commands;
using SetNet.DependencyInjection;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>
    /// Covers the single construction seam (<see cref="HandlerActivator"/>) that every discovered SetNet component —
    /// message handlers, protocol channel services / <c>[Op]</c> classes, client <c>[Event]</c> handlers, and RPC handlers —
    /// goes through, and that <c>SetNet.DependencyInjection</c> routes it through a container with constructor injection.
    /// </summary>
    public class DependencyInjectionTests
    {
        private sealed class Dep { public int Value = 42; }

        // A stand-in for any discovered component (handler / channel service / [Op] class / RPC handler): it takes a
        // constructor dependency, which only a DI-aware activator can satisfy.
        private sealed class NeedsDep
        {
            public Dep Dep { get; }
            public NeedsDep(Dep dep) => Dep = dep;
        }

        private sealed class NoDeps { }

        [Fact]
        public void Factory_is_used_when_set_else_parameterless_ctor()
        {
            var prev = HandlerActivator.Factory;
            try
            {
                var asked = false;
                HandlerActivator.Factory = t => { asked = true; return new NoDeps(); };
                Assert.IsType<NoDeps>(HandlerActivator.Create(typeof(NoDeps)));
                Assert.True(asked);

                HandlerActivator.Factory = null;                    // fall back to the parameterless ctor
                Assert.IsType<NoDeps>(HandlerActivator.Create(typeof(NoDeps)));
            }
            finally { HandlerActivator.Factory = prev; }
        }

        [Fact]
        public void UseSetNet_constructs_discovered_components_with_injected_dependencies()
        {
            var provider = new ServiceCollection()
                .AddSingleton<Dep>()
                .BuildServiceProvider();

            var prev = HandlerActivator.Factory;
            try
            {
                provider.UseSetNet();                               // routes ALL discovery construction through the container

                // Every discovered component is built via HandlerActivator.Create — here it gets its ctor dependency injected.
                var built = Assert.IsType<NeedsDep>(HandlerActivator.Create(typeof(NeedsDep)));
                Assert.Equal(42, built.Dep.Value);
            }
            finally { HandlerActivator.Factory = prev; }
        }

        [Fact]
        public void UseSetNetHandlers_is_an_alias()
        {
            var provider = new ServiceCollection().AddSingleton<Dep>().BuildServiceProvider();
            var prev = HandlerActivator.Factory;
            try
            {
                provider.UseSetNetHandlers();
                Assert.IsType<NeedsDep>(HandlerActivator.Create(typeof(NeedsDep)));
            }
            finally { HandlerActivator.Factory = prev; }
        }
    }
}

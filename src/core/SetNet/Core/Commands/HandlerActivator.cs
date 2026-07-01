using System;

namespace SetNet.Core.Commands
{
    /// <summary>
    /// The seam used to construct message-handler instances during discovery. By default handlers are created with their
    /// public parameterless constructor (<see cref="Activator"/>). Set <see cref="Factory"/> to route construction through
    /// a container instead — this is how <c>SetNet.DependencyInjection</c> lets handlers receive injected dependencies.
    /// A factory that returns <c>null</c> for a type falls back to the default construction.
    /// </summary>
    public static class HandlerActivator
    {
        /// <summary>Optional factory that builds a handler instance for a given handler type (return null to fall back to the default).</summary>
        public static Func<Type, object?>? Factory;

        /// <summary>Creates a handler instance via <see cref="Factory"/> when set (and non-null), otherwise the parameterless constructor.</summary>
        public static object Create(Type handlerType)
            => Factory?.Invoke(handlerType) ?? Activator.CreateInstance(handlerType)!;
    }
}

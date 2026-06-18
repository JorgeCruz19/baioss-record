using Microsoft.Extensions.DependencyInjection;
using Baioss.Record.Application.Abstractions;

namespace Baioss.Record.Infrastructure.Cqrs;

/// <summary>
/// Despachador CQRS por defecto: resuelve desde el contenedor DI el handler concreto de cada
/// comando/query y lo invoca. Sustituible por MediatR sin tocar endpoints ni handlers (mismas
/// firmas). El enlace al tipo concreto del comando se hace en tiempo de ejecución (dynamic).
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
    {
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        dynamic handler = services.GetRequiredService(handlerType);
        return handler.HandleAsync((dynamic)command, ct);
    }

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default)
    {
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        dynamic handler = services.GetRequiredService(handlerType);
        return handler.HandleAsync((dynamic)query, ct);
    }
}

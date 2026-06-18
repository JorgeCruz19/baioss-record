namespace Baioss.Record.Application.Abstractions;

// Abstracción mínima de CQRS. En producción se sustituye por MediatR sin tocar
// los handlers (mismas firmas). Comandos = mutan estado; Queries = solo lectura.

public interface ICommand<TResult>;
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}

/// <summary>Despachador que resuelve el handler adecuado desde el contenedor DI.</summary>
public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}

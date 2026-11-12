using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>An <see cref="ILogger{T}"/> that keeps every entry, for a component a test builds by hand.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
	public ConcurrentQueue<MemoriaApiFactory.CapturedLog> Entries { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull
	{
		return null;
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return true;
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		Dictionary<string, object?> values = [];
		if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
		{
			foreach ((string key, object? value) in pairs)
			{
				values[key] = value;
			}
		}

		Entries.Enqueue(new MemoriaApiFactory.CapturedLog(typeof(T).FullName!, logLevel, eventId, formatter(state, exception), values));
	}

	public MemoriaApiFactory.CapturedLog Single(int eventId)
	{
		return Assert.Single(Entries, entry => entry.EventId.Id == eventId);
	}
}

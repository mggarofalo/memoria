using System.Collections.Concurrent;
using System.Reflection;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Tests;

/// <summary>
/// An <see cref="IMemoriaQmdClient"/> answered by the test, method by method, for a component built without a host. A call
/// with no answer scripted fails the test, and every call is recorded by method name.
/// </summary>
public class ScriptedSidecarClient : DispatchProxy
{
	private readonly ConcurrentDictionary<string, Func<object?[], object?>> _answers = new(StringComparer.Ordinal);

	public ConcurrentQueue<string> Calls { get; } = new();

	public static (IMemoriaQmdClient Client, ScriptedSidecarClient Script) Create()
	{
		IMemoriaQmdClient client = Create<IMemoriaQmdClient, ScriptedSidecarClient>();
		return (client, (ScriptedSidecarClient)(object)client);
	}

	public ScriptedSidecarClient On(string method, Func<object?[], object?> answer)
	{
		_answers[method] = answer;
		return this;
	}

	protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
	{
		ArgumentNullException.ThrowIfNull(targetMethod);
		Calls.Enqueue(targetMethod.Name);
		return _answers.TryGetValue(targetMethod.Name, out Func<object?[], object?>? answer)
			? answer(args ?? [])
			: throw new InvalidOperationException($"No answer scripted for {targetMethod.Name}.");
	}
}

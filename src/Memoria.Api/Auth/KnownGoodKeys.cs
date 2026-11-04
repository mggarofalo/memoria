using System.Collections.Concurrent;

namespace Memoria.Api.Auth;

/// <summary>
/// The hashes of keys this process knows to be real. It decides one thing: whether
/// a request may reach the store while its address is over the failed-authentication
/// budget. Presenting a matching secret proves the caller is not guessing, so a valid
/// key is not locked out by an attacker at the same address, and the guesser cannot
/// use this path, because a wrong secret never matches.
/// <para>
/// It never decides acceptance: a match still goes to the store, which holds revocation
/// and expiry. It holds only what the database already holds, and a hash of a 256-bit
/// random secret gives nothing to guess against. The tradeoff: a key this process has
/// not seen, such as one bootstrapped by another process since startup, waits out the
/// window like anyone else at that address. Capacity is bounded; past it, new keys are
/// simply not remembered and behave the same way.
/// </para>
/// </summary>
public sealed class KnownGoodKeys(int capacity = KnownGoodKeys.DefaultCapacity)
{
	public const int DefaultCapacity = 10_000;

	private readonly ConcurrentDictionary<string, byte[]> _hashes = new(StringComparer.Ordinal);

	public int Count => _hashes.Count;

	public bool Matches(string id, ReadOnlySpan<byte> hash)
	{
		return _hashes.TryGetValue(id, out byte[]? known) && ApiKeyToken.HashesMatch(known, hash);
	}

	public void Remember(string id, byte[] hash)
	{
		if (_hashes.Count < capacity || _hashes.ContainsKey(id))
		{
			_hashes[id] = hash;
		}
	}

	public void Forget(string id)
	{
		_hashes.TryRemove(id, out _);
	}

	/// <summary>Loads every active key, so one that has not been used since startup is known too.</summary>
	public async Task LoadAsync(IApiKeyStore store, DateTimeOffset now, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		foreach (ApiKeyRecord record in await store.ListAsync(cancellationToken))
		{
			if (record.IsActive(now))
			{
				Remember(record.Id, record.Sha256Hash);
			}
		}
	}
}

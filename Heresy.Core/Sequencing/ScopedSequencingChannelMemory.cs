using System;
using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Owns the directly stored root channel-memory map and lazily materialized
/// independent maps for flattened invocations on one shared musical clock.
/// Positive scope IDs are monotonically allocated and never reused.
/// </summary>
public sealed class ScopedSequencingChannelMemory
{
	private readonly SequencingChannelStateMap _root;
	private readonly Dictionary<long, SequencingChannelStateMap> _scopes = [];
	private readonly HashSet<long> _active = [];
	private long _nextScopeId = 1;

	public ScopedSequencingChannelMemory(SequencingChannelStateMap? root = null)
		=> _root = root ?? new SequencingChannelStateMap();

	public int ActiveScopeCount => _active.Count;
	public int MaterializedScopeCount => _scopes.Count;

	public long AllocateScope()
	{
		if (_nextScopeId == long.MaxValue)
			throw new OverflowException("Flattened scope IDs are exhausted.");
		long id = _nextScopeId++;
		_active.Add(id);
		return id;
	}

	public SequencingChannelStateMap this[long scopeId]
	{
		get
		{
			if (scopeId == 0)
				return _root;
			if (scopeId < 0)
				throw new ArgumentOutOfRangeException(nameof(scopeId));
			if (!_active.Contains(scopeId))
				throw new InvalidOperationException(
					"Scope was never allocated or has already been retired.");
			if (!_scopes.TryGetValue(scopeId,
				out SequencingChannelStateMap? memory))
			{
				memory = new SequencingChannelStateMap();
				_scopes.Add(scopeId, memory);
			}
			return memory;
		}
	}

	public bool ForgetScope(long scopeId)
	{
		if (scopeId <= 0)
			return false;
		_scopes.Remove(scopeId);
		return _active.Remove(scopeId);
	}
}

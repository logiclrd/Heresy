using System;

namespace Heresy.Core.Diagnostics;

public sealed class SequencingResourceLimitException : Exception
{
	public SequencingResourceLimitException(string message) : base(message) { }
}

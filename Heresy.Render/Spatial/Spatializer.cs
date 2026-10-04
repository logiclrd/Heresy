using System;
using System.Numerics;

using Heresy.Render.Configuration;

namespace Heresy.Render.Spatial;

public static class Spatializer
{
	/// <summary>
	/// Positional gain g(d,p) = 2^(-p*d^2/2).
	/// </summary>
	public static double CalculateGain(
		Vector3 sourcePosition,
		OutputChannelConfiguration outputChannel)
	{
		ArgumentNullException.ThrowIfNull(outputChannel);

		double dx = (double)sourcePosition.X - outputChannel.Position.X;
		double dy = (double)sourcePosition.Y - outputChannel.Position.Y;
		double dz = (double)sourcePosition.Z - outputChannel.Position.Z;
		double distanceSquared = dx * dx + dy * dy + dz * dz;

		return Math.Pow(
			2.0,
			-outputChannel.PositionalImportance * distanceSquared / 2.0);
	}
}

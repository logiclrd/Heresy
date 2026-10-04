namespace Heresy.Core.Sequencing;

/// <summary>Append-only sink for generated note events.</summary>
public interface INoteReceiver
{
	void Append(NoteEvent noteEvent);
}

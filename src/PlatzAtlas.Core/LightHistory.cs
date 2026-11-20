namespace PlatzAtlas.Core;

internal sealed class LightHistory
{
    private readonly Stack<LightEdit> undo = new();
    private readonly Stack<LightEdit> redo = new();

    public LightEdit? NextUndo => undo.TryPeek(out var edit) ? edit : null;
    public LightEdit? NextRedo => redo.TryPeek(out var edit) ? edit : null;

    public void Record(LightEdit edit)
    {
        switch (edit.Kind)
        {
            case LightEditKind.Edit when edit.ReversesRevision is null:
                undo.Push(edit);
                redo.Clear();
                break;
            case LightEditKind.Undo:
                RecordReversal(undo, redo, edit);
                break;
            case LightEditKind.Redo:
                RecordReversal(redo, undo, edit);
                break;
            default:
                throw new InvalidDataException("The light history contains an invalid edit.");
        }
    }

    private static void RecordReversal(Stack<LightEdit> source, Stack<LightEdit> destination, LightEdit reversal)
    {
        if (!source.TryPeek(out var prior) ||
            reversal.ReversesRevision != prior.Revision ||
            reversal.ObjectId != prior.ObjectId ||
            reversal.SwitchId != prior.SwitchId ||
            reversal.WasOn != prior.IsOn ||
            reversal.IsOn != prior.WasOn)
        {
            throw new InvalidDataException("The light history contains an invalid undo or redo.");
        }

        destination.Push(reversal);
        source.Pop();
    }
}

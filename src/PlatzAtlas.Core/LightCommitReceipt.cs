using System.Text;

namespace PlatzAtlas.Core;

public sealed record AtlasCommitStamp(Guid OperationId, string PayloadSha256);

public sealed record LightCommitReceipt(
    Guid OperationId, Guid ProjectId, Guid SceneId, long BaseRevision, long Revision, Guid ObjectId,
    Guid SwitchId, bool WasOn, bool IsOn, string PayloadSha256)
{
    internal static LightCommitReceipt FromEdit(MuseumProject project, LightEdit edit)
    {
        var stamp = edit.AtlasCommit!;
        return new LightCommitReceipt(stamp.OperationId, project.ProjectId, project.SceneId, edit.Revision - 1,
            edit.Revision, edit.ObjectId, edit.SwitchId!.Value, edit.WasOn, edit.IsOn, stamp.PayloadSha256);
    }

    internal static string Digest(Guid projectId, Guid sceneId, long baseRevision, Guid objectId,
        Guid switchId, bool wasOn, bool isOn)
    {
        var fields = FormattableString.Invariant(
            $"platz-atlas/set-light/1\n{projectId:D}\n{sceneId:D}\n{baseRevision}\n{objectId:D}\n{switchId:D}\n{(wasOn ? 1 : 0)}\n{(isOn ? 1 : 0)}");
        return ProjectFormat.Digest(Encoding.UTF8.GetBytes(fields));
    }
}

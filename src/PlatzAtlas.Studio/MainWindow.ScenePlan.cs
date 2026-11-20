using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private static IReadOnlyList<SceneOperation> PlanOperations(SceneBuildPlan plan, ProjectReadScope scope)
    {
        var lane = plan.Lane switch { "left" => .32, "right" => .68, _ => .5 };
        var door = Add(SceneObjectKind.Door, "door", lane, .60, .22, .085);
        var lever = Add(SceneObjectKind.Switch, "switch", lane, .71, .042, .075);
        var leftEnd = lane - .105;
        var rightStart = lane + .105;
        var entities = new[]
        {
            door, lever,
            Add(SceneObjectKind.Wall, "west wall", leftEnd / 2, .60, leftEnd, .085),
            Add(SceneObjectKind.Wall, "east wall", (1 + rightStart) / 2, .60, 1 - rightStart, .085),
            Add(SceneObjectKind.Spawn, "start", lane, .90, .065, .105),
            Add(SceneObjectKind.Key, "key", lane, .78, .056, .096),
            Add(SceneObjectKind.Goal, "exit", lane, .25, .17, .15) with { Component = new SceneGoal(1) }
        };
        return entities.Select((entity, index) => (SceneOperation)new AddSceneEntity(scope.ProjectId, scope.Revision + index, entity))
            .Append(new ConfigureSceneSwitch(scope.ProjectId, scope.Revision + entities.Length, lever.Id, door.Id, SwitchBehavior.TurnOn))
            .ToArray();

        SceneEntity Add(SceneObjectKind kind, string label, double x, double y, double width, double height)
        {
            var entity = SceneObjectFactory.Create(Guid.NewGuid(), kind, $"{plan.Name} {label}", x, y);
            return entity with { Transform = entity.Transform with { Width = width, Height = height } };
        }
    }

    private void PreviewAtlasPlan(ProjectReadGrant grant, SceneAnswer answer, SceneBuildPlan plan)
    {
        var working = _session.PreviewWorkingPlan(PlanOperations(plan, grant.Scope));
        var review = _sceneAtlasHost.BeginReview(grant, answer, WorkingScope(_session.FreezeWorkingSnapshot()));
        _sceneAtlasDraft = new SceneAtlasDraft(review, Plan: working);
        SceneAtlasDraftText.Text = $"{plan.Summary}\nCreate {working.Entities.Count} objects and wire the switch in {working.Edits.Count} edits. " +
            "Apply to the working room as one Undo group?";
        SceneAtlasSource.Text = $"Working revision {grant.Scope.Revision}\nEvidence SHA-256 {answer.Sha256}\n\n" +
            string.Join("\n", working.Edits.Select((edit, index) => edit.Before is null && edit.After is { } created
                ? $"{index + 1}. Create {created.Name} at ({created.Transform.X:G}, {created.Transform.Y:G})."
                : $"{index + 1}. Wire {edit.After!.Name} to the closed door."));
        UpdateScenePresentation();
        SceneAtlasCancel.Focus();
        _diagnostics.Log("scene-atlas-plan-preview", new { revision = grant.Scope.Revision, edits = working.Edits.Count, review.Id });
    }

    private void ApplyAtlasPlan(SceneAtlasDraft draft, WorkingScenePlan plan)
    {
        var receipt = _sceneAtlasHost.Confirm(draft.Review, WorkingScope(_session.FreezeWorkingSnapshot()), (id, approval) =>
            _session.ApplyApprovedWorkingPlan(id, plan, approval));
        RevokeSceneAtlas();
        RefreshSceneList(plan.Entities.FirstOrDefault(entity => entity.Component is SceneSwitch)?.Id);
        UpdateStudio();
        SceneAtlasReceipt.IsExpanded = true;
        ShowStatus($"Applied {receipt.Changes} edits as one Undo group. Press F5, Up six times, E, then Up to the exit.");
        _diagnostics.Log("scene-atlas-plan-applied", receipt);
    }
}

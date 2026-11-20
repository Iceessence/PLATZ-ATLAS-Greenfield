using System.IO;
using System.Windows;
using PlatzAtlas.Atlas;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private readonly SceneQuestionHost _sceneAtlasHost = new();
    private ProjectReadGrant? _sceneAtlasGrant;
    private string? _sceneAtlasPath;
    private SceneAnswer? _sceneAtlasAnswer;
    private SceneAtlasDraft? _sceneAtlasDraft;

    private void ResetSceneAtlas()
    {
        RevokeSceneAtlas();
        SceneAtlasQuestion.Text = "Inspect selection";
    }

    private void RevokeSceneAtlas()
    {
        _sceneAtlasHost.Revoke();
        _sceneAtlasGrant = null;
        _sceneAtlasPath = null;
        _sceneAtlasAnswer = null;
        _sceneAtlasDraft = null;
        SceneAtlasResult.Text = "";
        SceneAtlasSource.Text = "";
    }

    private void UpdateSceneAtlas()
    {
        var current = _session.Current;
        var available = current.Scene is not null && _play is null;
        if (_sceneAtlasGrant is { } grant &&
            (!available || grant.Scope != WorkingScope(_session.FreezeWorkingSnapshot()) ||
                !string.Equals(_sceneAtlasPath, _session.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            var wasPreviewing = _sceneAtlasDraft is not null;
            RevokeSceneAtlas();
            if (wasPreviewing)
            {
                SceneAtlasResult.Text = "The working room changed. Allow a new read to preview an updated change.";
                UpdateScenePresentation();
            }
        }

        SceneAtlasAccess.IsEnabled = available;
        SceneAtlasAccess.Content = _sceneAtlasGrant is null ? "Allow scene read" : "Revoke scene read";
        SceneAtlasAsk.IsEnabled = _sceneAtlasGrant is not null;
        SceneAtlasHealth.IsEnabled = SceneAtlasAsk.IsEnabled;
        SceneAtlasScope.Text = !available ? "Return to Edit to ask about your room." : _sceneAtlasGrant is null
            ? $"Allow a read of working revision {current.Revision}."
            : $"Reading working revision {current.Revision}. Proposed changes require your approval.";
        SceneAtlasAnswerCard.Visibility = _sceneAtlasAnswer is null ? Visibility.Collapsed : Visibility.Visible;
        SceneAtlasShow.Visibility = _sceneAtlasAnswer?.Source is null ? Visibility.Collapsed : Visibility.Visible;
        SceneAtlasTarget.Visibility = (_sceneAtlasAnswer?.ProposedTarget ?? _sceneAtlasAnswer?.Target) is null
            ? Visibility.Collapsed : Visibility.Visible;
        SceneAtlasTarget.Content = _sceneAtlasAnswer?.ProposedTarget is null ? "Show linked target" : "Show proposed target";
        SceneAtlasPreview.Visibility = _sceneAtlasAnswer?.Proposal is null ? Visibility.Collapsed : Visibility.Visible;
        var receipt = _session.LatestSceneCommit;
        SceneAtlasReceipt.Visibility = receipt is null ? Visibility.Collapsed : Visibility.Visible;
        SceneAtlasReceiptText.Text = receipt is null ? "" :
            $"{(_session.SavedRevision >= receipt.Revision ? "Saved" : "Applied to working room")} r{receipt.Revision}\nObject {receipt.EntityId:D}\n\nOperation {receipt.OperationId:D}\n\nSHA-256 {receipt.PayloadSha256}";
    }

    private void SceneAtlasAccessClicked(object sender, RoutedEventArgs e) => WithSceneAtlas(() =>
    {
        if (_sceneAtlasGrant is not null)
        {
            RevokeSceneAtlas();
            SceneAtlasResult.Text = "Scene access revoked.";
        }
        else
        {
            if (_play is not null || _session.Current.Scene is null)
                return;
            var session = _session;
            var snapshot = session.FreezeWorkingSnapshot();
            var scope = WorkingScope(snapshot);
            _sceneAtlasGrant = _sceneAtlasHost.Allow(scope, () =>
            {
                if (!ReferenceEquals(_session, session) || !ReferenceEquals(session.Current, snapshot.Project))
                    throw new InvalidDataException("The room changed. Allow a new read.");
                return PublishSceneEvidence(snapshot.Project, scope);
            });
            _sceneAtlasPath = session.FilePath;
            SceneAtlasResult.Text = "";
        }
        UpdateSceneAtlas();
        UpdateScenePresentation();
    });

    private void SceneAtlasAskClicked(object sender, RoutedEventArgs e) => WithSceneAtlas(() =>
    {
        if (_sceneAtlasGrant is not { } grant)
            return;
        var result = _sceneAtlasHost.Ask(grant, SceneEvidenceProfile.Version, SelectedEntityId, SceneAtlasQuestion.Text);
        _sceneAtlasDraft = null;
        _sceneAtlasAnswer = result.Answer;
        if (result.Answer is { } answer)
        {
            SceneAtlasResult.Text = FormatAnswer(answer);
            var source = answer.Source;
            SceneAtlasSource.Text = $"Project {answer.Scope.ProjectId:D}\nScene {answer.Scope.SceneId:D}\nWorkspace {answer.Scope.WorkspaceId:D}\nWorking revision {answer.Scope.Revision}" +
                (source is null ? "\n\nNew object proposal for this working scene" :
                $"\n\nscene.entities[{source.Id:D}].transform\nX {source.X:G17} · Y {source.Y:G17}" +
                $"\nWidth {source.Width:G17} · Height {source.Height:G17} · Rotation {source.Rotation:G17}" +
                $"\n\nscene.entities[{source.Id:D}].component\n{source.Kind}" +
                (source.TargetId is { } target ? $"\ntargetId {target:D}" : "") +
                (source.SecondTargetId is { } second ? $"\nsecondTargetId {second:D}" : "") +
                (source.AssetId is { } asset ? $"\nassetId {asset:D}" : "")) +
                (answer.ProposedTarget is { } proposed ? $"\n\nProposed target\n{proposed.Name}\n{proposed.Id:D}" : "") +
                $"\n\nEvidence SHA-256\n{answer.Sha256}";
        }
        else if (result.Health is { } health)
        {
            var issues = health.Issues.Count == 0 ? "No listed scene issues." : string.Join("\n\n",
                health.Issues.Select(issue => issue.Message + "\n" + string.Join("\n", issue.ObjectIds)));
            SceneAtlasResult.Text = $"Scene health · Working r{health.Scope.Revision}\n\n{issues}\n\nEvidence SHA-256\n{health.Sha256}";
            SceneAtlasSource.Text = "";
        }
        else
        {
            SceneAtlasResult.Text = result.Message ?? "Scene evidence is unavailable. Check read access and try again.";
            SceneAtlasSource.Text = "";
        }
        _diagnostics.Log("scene-atlas-read", new { status = result.Status.ToString(), revision = grant.Scope.Revision,
            objectId = result.Answer?.Source?.Id, healthIssues = result.Health?.Issues.Count });
        UpdateSceneAtlas();
        UpdateScenePresentation();
    });

    private void SceneAtlasHealthClicked(object sender, RoutedEventArgs e)
    {
        SceneAtlasQuestion.Text = "Check scene";
        SceneAtlasAskClicked(sender, e);
    }

    private void SceneAtlasShowClicked(object sender, RoutedEventArgs e) => ShowSceneAtlasObject(_sceneAtlasAnswer?.Source?.Id);
    private void SceneAtlasTargetClicked(object sender, RoutedEventArgs e) =>
        ShowSceneAtlasObject((_sceneAtlasAnswer?.ProposedTarget ?? _sceneAtlasAnswer?.Target)?.Id);

    private void ShowSceneAtlasObject(Guid? id) => WithSceneAtlas(() =>
    {
        if (_sceneAtlasGrant is not { } grant || id is null ||
            grant.Scope != WorkingScope(_session.FreezeWorkingSnapshot()))
            return;
        Objects.SelectedItem = Objects.Items.OfType<SceneObjectItem>().Single(item => item.Id == id);
        Objects.ScrollIntoView(Objects.SelectedItem);
        SideTabs.SelectedItem = InspectorTab;
    });

    private void SceneAtlasPreviewClicked(object sender, RoutedEventArgs e) => WithSceneAtlas(() =>
    {
        if (_sceneAtlasGrant is not { } grant || _sceneAtlasAnswer is not { Proposal: { } proposal } answer)
            return;
        var scope = grant.Scope;
        var preview = _session.PreviewWorkingScene(SceneOperationFor(answer, proposal));
        var review = _sceneAtlasHost.BeginReview(grant, answer, WorkingScope(_session.FreezeWorkingSnapshot()));
        _sceneAtlasDraft = new SceneAtlasDraft(preview, review);
        var action = preview.Before is null ? $"Create {preview.After.Name} at ({preview.After.Transform.X:G}, {preview.After.Transform.Y:G})"
            : ProposalText(answer);
        SceneAtlasDraftText.Text = $"{action}. Apply to the working room as r{scope.Revision + 1}?";
        UpdateScenePresentation();
        SceneAtlasCancel.Focus();
        _diagnostics.Log("scene-atlas-preview", new { objectId = preview.After.Id, scope.Revision, operationId = review.Id });
    });

    private static SceneOperation SceneOperationFor(SceneAnswer answer, SceneProposal proposal)
    {
        var scope = answer.Scope;
        if (proposal.Kind == SceneProposalKind.Add && proposal.ObjectKind is { } kind)
            return new AddSceneEntity(scope.ProjectId, scope.Revision,
                SceneObjectFactory.Create(Guid.NewGuid(), Enum.Parse<SceneObjectKind>(kind.ToString()), proposal.Name!, proposal.X, proposal.Y));
        var source = answer.Source ?? throw new InvalidDataException("The proposed edit needs an object in this working room.");
        var transform = new Transform2D(source.X, source.Y, source.Width, source.Height, source.Rotation);
        return proposal.Kind switch
        {
            SceneProposalKind.Rename => new RenameSceneEntity(scope.ProjectId, scope.Revision, source.Id, proposal.Name!),
            SceneProposalKind.Move => new TransformSceneEntity(scope.ProjectId, scope.Revision, source.Id,
                transform with { X = proposal.X, Y = proposal.Y }),
            SceneProposalKind.Resize => new TransformSceneEntity(scope.ProjectId, scope.Revision, source.Id,
                transform with { Width = proposal.X, Height = proposal.Y }),
            SceneProposalKind.Rotate => new TransformSceneEntity(scope.ProjectId, scope.Revision, source.Id,
                transform with { Rotation = proposal.X }),
            SceneProposalKind.Wire or SceneProposalKind.Unlink => new ConfigureSceneSwitch(scope.ProjectId, scope.Revision,
                source.Id, proposal.TargetId, Enum.Parse<SwitchBehavior>(source.Behavior!))
            {
                SecondTargetId = proposal.TargetId is Guid target && target != source.SecondTargetId ? source.SecondTargetId : null
            },
            SceneProposalKind.Duplicate => new DuplicateSceneEntity(scope.ProjectId, scope.Revision, Guid.NewGuid(), source.Id),
            SceneProposalKind.SetState when source.Kind == SceneSourceKind.Door =>
                new SetSceneDoor(scope.ProjectId, scope.Revision, source.Id, proposal.State),
            SceneProposalKind.SetState => new SetSceneLamp(scope.ProjectId, scope.Revision, source.Id, proposal.State),
            _ => throw new InvalidDataException("The proposed operation is outside the scene profile.")
        };
    }

    private void SceneAtlasCancelClicked(object sender, RoutedEventArgs e)
    {
        _sceneAtlasHost.DiscardReview();
        _sceneAtlasDraft = null;
        UpdateScenePresentation();
    }

    private void SceneAtlasConfirmClicked(object sender, RoutedEventArgs e) => WithSceneAtlas(() =>
    {
        if (_sceneAtlasDraft is not { } draft)
            return;
        if (!string.Equals(_sceneAtlasPath, _session.FilePath, StringComparison.OrdinalIgnoreCase))
            throw new ProjectEditException(EditRejection.StaleRevision, "The project path changed. Reopen the intended project.");
        var receipt = _sceneAtlasHost.Confirm(draft.Review, WorkingScope(_session.FreezeWorkingSnapshot()), (operationId, requireApproval) =>
            _session.ApplyApprovedWorkingScene(operationId, draft.Preview, requireApproval));
        RevokeSceneAtlas();
        RefreshSceneList(receipt.EntityId);
        UpdateStudio();
        SceneAtlasReceipt.IsExpanded = true;
        ShowStatus($"Applied ATLAS edit to working revision {receipt.Revision}. Use Undo to reverse it or Save to keep it.");
        _diagnostics.Log("scene-atlas-commit", receipt);
    });

    private void WithSceneAtlas(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (error is ProjectEditException { Reason: EditRejection.StaleRevision })
                RevokeSceneAtlas();
            SceneAtlasResult.Text = error.Message;
            ShowStatus(error.Message, error: true);
            UpdateSceneAtlas();
            UpdateScenePresentation();
            _diagnostics.Log("scene-atlas-rejected", new { error = error.GetType().Name });
        }
    }

    private static ProjectReadScope WorkingScope(WorkingDocumentSnapshot snapshot) =>
        ScopeOf(snapshot.Project) with { WorkspaceId = snapshot.WorkspaceId };

    private static ReadOnlyMemory<byte> PublishSceneEvidence(MuseumProject saved, ProjectReadScope scope)
    {
        var numbers = SceneNumbers.FromHistory(saved.Scene!);
        var sources = saved.Scene!.Entities.Select(item =>
        {
            var t = item.Transform;
            var (kind, active, target, behavior, assetId, assetName, color) = item.Component switch
            {
                SceneLamp lamp => (SceneSourceKind.Lamp, (bool?)lamp.IsOn, (Guid?)null, (string?)null, (Guid?)null, (string?)null, (string?)null),
                SceneDoor door => (SceneSourceKind.Door, (bool?)door.IsOpen, null, null, null, null, null),
                SceneSwitch control => (SceneSourceKind.Switch, null, control.TargetId, control.Behavior.ToString(), null, null, null),
                SceneSprite image => (SceneSourceKind.Image, null, null, null, (Guid?)image.AssetId,
                    saved.Assets.Single(asset => asset.Id == image.AssetId).Name, null),
                WallPanel wall => (SceneSourceKind.Wall, null, null, null, null, null, wall.Color.ToString()),
                SceneSpawn => (SceneSourceKind.Spawn, null, null, null, null, null, null),
                SceneKey => (SceneSourceKind.Key, null, null, null, null, null, null),
                SceneGoal => (SceneSourceKind.Goal, null, null, null, null, null, null),
                _ => throw new InvalidDataException("An object type is unavailable in the evidence profile.")
            };
            return new SceneSource(item.Id, item.Name, kind, t.X, t.Y, t.Width, t.Height, t.Rotation,
                active, target, behavior, assetId, assetName, color)
            {
                SecondTargetId = (item.Component as SceneSwitch)?.SecondTargetId,
                RequiredKeys = (item.Component as SceneGoal)?.RequiredKeys,
                Number = numbers[item.Id],
                SpawnBlocked = item.Component is SceneSpawn
                    ? MuseumPlaySession.SpawnBlocked(saved.Scene!.Entities, new RoomPosition(t.X, t.Y)) : null
            };
        }).ToArray();
        return SceneEvidenceProfile.Publish(scope, sources);
    }

    private static string FormatAnswer(SceneAnswer answer)
    {
        if (answer.Source is not { } item)
            return $"Working r{answer.Scope.Revision}\n\nProposed: {ProposalText(answer)}";
        var details = item.Kind switch
        {
            SceneSourceKind.Switch => answer.Target is { } target ? (answer.AdditionalTarget is { } second
                ? $"Controls {target.Name} and {second.Name}. {item.Behavior}." : $"Controls {target.Name}. {item.Behavior}.")
                : "The switch is unconnected.",
            SceneSourceKind.Door or SceneSourceKind.Lamp => $"State is {FormatObjectState(item.Kind, item.Active == true)}.",
            SceneSourceKind.Image => $"Uses image {item.AssetName}.",
            SceneSourceKind.Spawn => "Sets the starting position of the visitor.",
            SceneSourceKind.Key => "Collected when the visitor touches it.",
            SceneSourceKind.Goal => $"Win condition requires {item.RequiredKeys} {(item.RequiredKeys == 1 ? "key" : "keys")}.",
            _ => $"Wall color is {item.Color}."
        };
        return $"{item.Name} · {item.Kind}\nX {item.X:G}, Y {item.Y:G} · Working r{answer.Scope.Revision}\n{details}" +
            (answer.Proposal is null ? "" : $"\n\nProposed: {ProposalText(answer)}");
    }

    private static string ProposalText(SceneAnswer answer)
    {
        var proposal = answer.Proposal!;
        if (proposal.Kind == SceneProposalKind.Add)
            return $"Add {proposal.ObjectKind?.ToString().ToLowerInvariant()} {proposal.Name} at ({proposal.X:G}, {proposal.Y:G})";
        var item = answer.Source!;
        var summary = proposal.Kind switch
        {
            SceneProposalKind.Rename => $"name → {proposal.Name}",
            SceneProposalKind.Move => $"({item.X:G}, {item.Y:G}) → ({proposal.X:G}, {proposal.Y:G})",
            SceneProposalKind.Resize => $"size ({item.Width:G}, {item.Height:G}) → ({proposal.X:G}, {proposal.Y:G})",
            SceneProposalKind.Rotate => $"rotation {item.Rotation:G}° → {proposal.X:G}°",
            SceneProposalKind.Wire => $"primary target → {answer.ProposedTarget?.Name}",
            SceneProposalKind.Unlink => "disconnect all targets",
            SceneProposalKind.Duplicate => "duplicate with a new object ID",
            _ => $"{FormatObjectState(item.Kind, item.Active == true)} → {FormatObjectState(item.Kind, proposal.State)}"
        };
        return $"{item.Name} · {summary}";
    }

    private static string FormatObjectState(SceneSourceKind kind, bool active) =>
        kind == SceneSourceKind.Door ? (active ? "open" : "closed") : (active ? "on" : "off");

    private sealed record SceneAtlasDraft(ScenePreview Preview, SceneWriteReview Review);
}

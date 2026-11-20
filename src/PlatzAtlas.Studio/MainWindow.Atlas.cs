using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private readonly ProjectQuestionHost _atlasHost = new();
    private ProjectReadGrant? _atlasGrant;
    private SwitchAnswer? _atlasAnswer;
    private AtlasLightDraft? _atlasDraft;
    private LightPreview? _atlasPreview => _atlasDraft?.Preview;
    private LightCommitReview? _atlasCommitReview;

    private void InitializeAtlas()
    {
        AtlasQuestion.MaxLength = ProjectQuestionHost.MaxQuestionLength;
        AtlasQuestion.TextChanged += (_, _) => ClearAtlasAnswer();
    }

    private void ResetAtlasSession()
    {
        ClearAtlasAccess();
        AtlasQuestion.Text = ProjectQuestionHost.SupportedQuestion;
        AtlasReceiptCard.IsExpanded = false;
    }

    private void ClearAtlasAccess()
    {
        _atlasHost.Revoke();
        _atlasGrant = null;
        ClearAtlasAnswer();
    }

    private void UpdateAtlasAccess()
    {
        var scope = CurrentSavedScope();
        if (_atlasGrant is { } grant && scope != grant.Scope)
        {
            ClearAtlasAccess();
            _diagnostics.Log("atlas-access", new { status = "revision-or-project-changed" });
        }
        else if (_atlasDraft is { } draft && !string.Equals(draft.Path, _session.FilePath, StringComparison.OrdinalIgnoreCase))
            ExitAtlasPreview();

        AtlasAccess.IsEnabled = scope.HasValue;
        AtlasAccess.Content = _atlasGrant.HasValue ? "Revoke access" : "Allow project read";
        AutomationProperties.SetName(AtlasAccess,
            _atlasGrant.HasValue ? "Revoke ATLAS read access" : "Allow ATLAS project read");
        AtlasAsk.IsEnabled = _atlasGrant.HasValue;
        AtlasScopeLabel.Text = scope switch
        {
            null => "Save the current revision to ask ATLAS about it.",
            { Revision: var revision } when _atlasGrant.HasValue => $"Read access for saved revision {revision}.",
            { Revision: var revision } => $"Allow ATLAS to read switch links and lantern details from saved revision {revision}."
        };
        UpdateAtlasReceipt();
    }

    private ProjectReadScope? CurrentSavedScope()
    {
        if (_play is not null || _session.IsDirty || _session.FilePath is null)
            return null;
        return ScopeOf(_session.Current);
    }

    private static ProjectReadScope ScopeOf(MuseumProject project) =>
        new(project.ProjectId, project.SceneId, project.Revision);

    private void AtlasAccessClicked(object sender, RoutedEventArgs e)
    {
        if (_atlasGrant.HasValue)
        {
            ClearAtlasAccess();
            UpdateAtlasAccess();
            AtlasScopeLabel.Text = "Access revoked. Allow a project read to ask again.";
            _diagnostics.Log("atlas-access", new { status = "revoked" });
        }
        else if (CurrentSavedScope() is { } scope)
        {
            var session = _session;
            if (CanReadAtlasHead(session))
            {
                _atlasGrant = _atlasHost.Allow(scope, () => PublishSwitchEvidence(session.ReadSavedSnapshot()));
                UpdateAtlasAccess();
                _diagnostics.Log("atlas-access", new { status = "allowed", revision = scope.Revision });
            }
        }

        UIElementAutomationPeer.FromElement(AtlasScopeLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private bool CanReadAtlasHead(ProjectSession session)
    {
        try
        {
            session.ReadSavedSnapshot();
            return true;
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException)
        {
            RejectAtlasHead(error);
            return false;
        }
    }

    private void RejectAtlasHead(Exception error)
    {
        ClearAtlasAccess();
        UpdateAtlasAccess();
        AtlasScopeLabel.Text = error is ProjectEditException ? error.Message :
            "Could not verify the saved project. Reopen it before asking ATLAS.";
        UIElementAutomationPeer.FromElement(AtlasScopeLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _diagnostics.Log("atlas-access", new { status = "source-rejected", reason = error.GetType().Name });
    }

    private static byte[] PublishSwitchEvidence(MuseumProject saved)
    {
        if (saved.Switches.Count > SwitchEvidenceProfile.MaxRecordsPerKind
            || saved.Objects.Count > SwitchEvidenceProfile.MaxRecordsPerKind)
            throw new InvalidDataException("The room has too many records for the switch evidence profile.");

        return SwitchEvidenceProfile.Publish(ScopeOf(saved),
            saved.Switches.Select(item => new SwitchSource(item.Id, item.Name, item.TargetId)).ToArray(),
            saved.Objects.Select(item => new LanternSource(item.Id, item.AssetId, item.Name, item.Location, item.IsOn)).ToArray());
    }

    private void AtlasAskClicked(object sender, RoutedEventArgs e)
    {
        if (_atlasGrant is not { } grant)
            return;

        if (CurrentSavedScope() != grant.Scope)
        {
            ClearAtlasAccess();
            UpdateAtlasAccess();
            return;
        }
        var started = Stopwatch.GetTimestamp();
        SwitchQuestionResult result;
        try
        {
            result = _atlasHost.Ask(new SwitchQuestion(SwitchEvidenceProfile.Version, grant, AtlasQuestion.Text));
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException)
        {
            RejectAtlasHead(error);
            return;
        }
        ClearAtlasAnswer();
        if (result.Answer is { } answer)
        {
            _atlasAnswer = answer;
            AtlasAnswerOrigin.Text = $"Local lookup · Saved r{answer.Scope.Revision}";
            AtlasAnswerText.Text = $"{answer.Switch.Name} controls the {answer.Lantern.Name} in {answer.Lantern.Location}.";
            AtlasLightState.Text = $"Light is {(answer.Lantern.IsOn ? "on" : "off")}.";
            AtlasProposalText.Text = $"Proposed light change · {LightState(answer.Lantern.IsOn)} → {LightState(!answer.Lantern.IsOn)}";
            AtlasSourceText.Text =
                $"{answer.Switch.TargetField}\n{answer.Switch.TargetId:D}" +
                $"\n\n{answer.Lantern.LocationField}\n{answer.Lantern.Location}" +
                $"\n\n{answer.Lantern.LightField}\n{(answer.Lantern.IsOn ? "true" : "false")}" +
                $"\n\nProject ID\n{answer.Scope.ProjectId:D}\n\nScene ID\n{answer.Scope.SceneId:D}" +
                $"\n\nAsset ID\n{answer.Lantern.AssetId:D}" +
                $"\n\nSnapshot SHA-256\n{answer.SnapshotSha256}";
            AtlasAnswerCard.Visibility = Visibility.Visible;
            UIElementAutomationPeer.FromElement(AtlasAnswerText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        else
        {
            AtlasReadStatus.Text = result.Status switch
            {
                QuestionStatus.UnsupportedQuestion => $"Try “{ProjectQuestionHost.SupportedQuestion}”",
                QuestionStatus.EvidenceUnavailable => "No unique West Switch and linked lantern were found in the saved revision.",
                QuestionStatus.AccessDenied => "ATLAS could not read this revision. Allow access and try again.",
                QuestionStatus.IncompatibleProfile => "This revision uses an unsupported evidence profile.",
                _ => "This revision could not be read using the switch evidence profile."
            };
            AtlasReadStatus.Visibility = Visibility.Visible;
            UIElementAutomationPeer.FromElement(AtlasReadStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        _diagnostics.Log("atlas-read", new
        {
            status = result.Status.ToString(),
            profile = SwitchEvidenceProfile.Version,
            revision = result.Answer?.Scope.Revision,
            durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
    }

    private void AtlasSourceClicked(object sender, RoutedEventArgs e)
    {
        if (_atlasAnswer is not { } answer || _atlasGrant?.Scope != answer.Scope || CurrentSavedScope() != answer.Scope)
            return;

        var item = Objects.Items.OfType<SceneObjectItem>().SingleOrDefault(entry => entry.Id == answer.Lantern.Id);
        if (item is null)
            return;

        ExitAtlasPreview();
        Objects.SelectedItem = item;
        Objects.ScrollIntoView(item);
        SideTabs.SelectedItem = InspectorTab;
        if (Objects.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem selected)
            selected.Focus();
        _diagnostics.Log("atlas-source-selected", new { revision = answer.Scope.Revision });
    }

    private void AtlasPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (_atlasPreview is not null || _atlasGrant is not { } grant || _atlasAnswer is not { } answer)
            return;

        var started = Stopwatch.GetTimestamp();
        try
        {
            var (status, draft) = _atlasHost.StagePreview(grant, ScopeOf(_session.Current), answer, proposal =>
                new AtlasLightDraft(proposal,
                    _session.PreviewSavedLight(new SetLight(proposal.Scope.ProjectId, proposal.Scope.Revision,
                        proposal.ObjectId, proposal.IsOn, proposal.SwitchId) { ExpectedIsOn = proposal.ExpectedIsOn }),
                    _session.FilePath!));

            if (draft is null)
            {
                RejectAtlasPreview(status.ToString(), status == PreviewStatus.StaleBase
                    ? "The revision changed. Save and ask ATLAS again."
                    : "This proposal is no longer available. Ask ATLAS again.");
                return;
            }

            _atlasDraft = draft;
            UpdateScenePresentation();
            UIElementAutomationPeer.FromElement(AtlasPreviewLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            AtlasCommitReviewStart.Focus();
            _diagnostics.Log("atlas-preview", new
            {
                status = "staged",
                revision = draft.Preview.Operation.BaseRevision,
                durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
        catch (ProjectEditException error)
        {
            RejectAtlasPreview(error.Reason.ToString(), error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RejectAtlasPreview("SourceUnavailable", "Could not verify the saved revision. Reopen the project and try again.");
        }
    }

    private void RejectAtlasPreview(string reason, string message)
    {
        ShowStatus($"Preview unavailable. {message}", error: true);
        _diagnostics.Log("atlas-preview", new { status = "rejected", reason });
    }

    private void AtlasPreviewExitClicked(object sender, RoutedEventArgs e)
    {
        ExitAtlasPreview();
        if (AtlasPreviewStart.IsVisible)
            AtlasPreviewStart.Focus();
    }

    private void ExitAtlasPreview()
    {
        if (_atlasPreview is null)
            return;

        _atlasDraft = null;
        _atlasCommitReview = null;
        _atlasHost.DiscardPreview();
        UpdateScenePresentation();
        _diagnostics.Log("atlas-preview", new { status = "closed" });
    }

    private static string LightState(bool isOn) => isOn ? "On" : "Off";

    private sealed record AtlasLightDraft(SwitchLightProposal Proposal, LightPreview Preview, string Path);

    private void ClearAtlasAnswer()
    {
        ExitAtlasPreview();
        _atlasAnswer = null;
        AtlasReadStatus.Visibility = Visibility.Collapsed;
        AtlasReadStatus.Text = "";
        AtlasAnswerCard.Visibility = Visibility.Collapsed;
        AtlasAnswerOrigin.Text = "";
        AtlasAnswerText.Text = "";
        AtlasLightState.Text = "";
        AtlasProposalText.Text = "";
        AtlasSourceText.Text = "";
        AtlasSourceDetails.IsExpanded = false;
    }
}

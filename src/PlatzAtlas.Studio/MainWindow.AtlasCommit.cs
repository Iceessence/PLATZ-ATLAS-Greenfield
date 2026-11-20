using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private void UpdateAtlasCommitControls()
    {
        var reviewing = _atlasCommitReview is not null && _atlasDraft is not null;
        AtlasCommitConfirmation.Visibility = reviewing ? Visibility.Visible : Visibility.Collapsed;
        AtlasCommitReviewStart.Visibility = _atlasDraft is not null && !reviewing
            ? Visibility.Visible : Visibility.Collapsed;

        if (reviewing && _atlasDraft is { } draft)
        {
            var preview = draft.Preview;
            AtlasCommitSummary.Text = $"Save {preview.Before.Location} light {LightState(preview.Before.IsOn)} → " +
                $"{LightState(preview.After.IsOn)} to {Path.GetFileName(draft.Path)} as revision {preview.Operation.BaseRevision + 1}.";
            AtlasCommitSummary.ToolTip = draft.Path;
        }
        else
        {
            AtlasCommitSummary.Text = "";
            AtlasCommitSummary.ToolTip = null;
        }
    }

    private void AtlasCommitReviewClicked(object sender, RoutedEventArgs e)
    {
        if (_atlasCommitReview is not null || _atlasGrant is not { } grant || _atlasDraft is not { } draft)
            return;

        var (status, review) = _atlasHost.BeginCommitReview(grant, ScopeOf(_session.Current), draft.Proposal);
        if (review is null)
        {
            RejectAtlasCommit(status.ToString(), "Ask and preview the change again.");
            return;
        }

        _atlasCommitReview = review;
        UpdateScenePresentation();
        UIElementAutomationPeer.FromElement(AtlasCommitSummary)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        AtlasCommitCancel.Focus();
        _diagnostics.Log("atlas-commit", new { status = "reviewed", operationId = review.OperationId });
    }

    private void AtlasCommitCancelClicked(object sender, RoutedEventArgs e)
    {
        CloseAtlasCommitReview();
        if (AtlasCommitReviewStart.IsVisible)
            AtlasCommitReviewStart.Focus();
    }

    private void CloseAtlasCommitReview()
    {
        if (_atlasCommitReview is not { } review)
            return;

        _atlasHost.CancelCommitReview(review);
        _atlasCommitReview = null;
        UpdateScenePresentation();
    }

    private void AtlasCommitConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_atlasGrant is not { } grant || _atlasCommitReview is not { } review || _atlasDraft is not { } draft)
            return;

        if (!string.Equals(draft.Path, _session.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ExitAtlasPreview();
            RejectAtlasCommit("DestinationChanged", "The destination changed. Preview and review the change again.");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var (status, receipt) = _atlasHost.CommitConfirmed(grant, ScopeOf(_session.Current), review,
                (proposal, requireApproval) => _session.CommitApprovedLight(proposal.OperationId,
                    draft.Preview.Operation, requireApproval));
            if (receipt is null)
            {
                CloseAtlasCommitReview();
                RejectAtlasCommit(status.ToString(), status == CommitStatus.Expired
                    ? "This review expired. Review the save again."
                    : "Approval is no longer valid. Ask and preview the change again.");
                if (AtlasCommitReviewStart.IsVisible)
                    AtlasCommitReviewStart.Focus();
                return;
            }

            ClearAtlasAccess();
            UpdateStudio(started);
            AtlasReceiptCard.IsExpanded = true;
            AtlasReceiptCard.Focus();
            ShowStatus($"Saved {draft.Preview.Before.Location} light {LightState(receipt.IsOn).ToLowerInvariant()} " +
                $"at revision {receipt.Revision}. Use Undo to reverse it.");
            _diagnostics.Log("atlas-commit", new
            {
                status = "committed", receipt.OperationId, receipt.BaseRevision, receipt.Revision,
                receipt.ObjectId, receipt.PayloadSha256, durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
        catch (ProjectEditException error)
        {
            ExitAtlasPreview();
            RejectAtlasCommit(error.Reason.ToString(), error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RejectAtlasCommit("PublishFailed", "Could not write the saved revision. Your preview is still open.");
        }
    }

    private void RejectAtlasCommit(string reason, string message)
    {
        ShowStatus($"Save not applied. {message}", error: true);
        _diagnostics.Log("atlas-commit", new { status = "rejected", reason });
    }

    private void UpdateAtlasReceipt()
    {
        var receipt = _session.LatestAtlasCommit;
        AtlasReceiptCard.Visibility = receipt is null ? Visibility.Collapsed : Visibility.Visible;
        if (receipt is null)
        {
            AtlasReceiptText.Text = "";
            return;
        }

        var location = _session.Current.Objects.Single(item => item.Id == receipt.ObjectId).Location;
        AtlasReceiptText.Text = $"Saved r{receipt.Revision} · {location}\n" +
            $"Light {LightState(receipt.WasOn)} → {LightState(receipt.IsOn)}\n\n" +
            $"Operation ID\n{receipt.OperationId:D}\n\nPayload SHA-256\n{receipt.PayloadSha256}";
    }
}

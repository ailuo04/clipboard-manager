using ClipboardManager.Core.Updates;
using Xunit;

namespace ClipboardManager.Core.Tests;

public sealed class UpdateStateMachineTests
{
    [Fact]
    public void State_machine_accepts_the_complete_update_lifecycle()
    {
        var state = new UpdateStateMachine();

        state.BeginChecking();
        state.ReportAvailable();
        state.BeginDownloading();
        state.BeginVerifying();
        state.BeginApplying();
        state.BeginStartupHealthCheck();
        state.Complete();

        Assert.Equal(UpdateState.Complete, state.State);
        Assert.Null(state.ErrorKind);
        Assert.False(state.CanCancel);
    }

    [Fact]
    public void State_machine_rejects_transitions_that_skip_required_stages()
    {
        var state = new UpdateStateMachine();

        Assert.Throws<InvalidOperationException>(state.BeginVerifying);
        Assert.Equal(UpdateState.Idle, state.State);
    }

    [Fact]
    public void Applying_cannot_be_cancelled()
    {
        var state = new UpdateStateMachine();
        state.BeginChecking();
        state.ReportAvailable();
        state.BeginDownloading();
        state.BeginVerifying();
        state.BeginApplying();

        Assert.False(state.Cancel());
        Assert.Equal(UpdateState.Applying, state.State);
    }

    [Fact]
    public void Recoverable_errors_expose_safe_retry_and_recovery_copy()
    {
        var network = new UpdateStateMachine();
        network.BeginChecking();
        network.Fail(UpdateErrorKind.Network);

        Assert.Equal(UpdateState.Error, network.State);
        Assert.True(network.CanRetry);
        Assert.False(string.IsNullOrWhiteSpace(network.RecoveryMessage));

        var manifest = new UpdateStateMachine();
        manifest.BeginChecking();
        manifest.Fail(UpdateErrorKind.InvalidManifest);

        Assert.False(manifest.CanRetry);
        Assert.False(string.IsNullOrWhiteSpace(manifest.RecoveryMessage));
    }

    [Fact]
    public void Cancelled_download_is_reported_without_entering_apply()
    {
        var state = new UpdateStateMachine();
        state.BeginChecking();
        state.ReportAvailable();
        state.BeginDownloading();

        Assert.True(state.Cancel());
        Assert.Equal(UpdateState.Cancelled, state.State);
        Assert.Equal(UpdateErrorKind.Cancelled, state.ErrorKind);
    }

    [Fact]
    public void Every_error_kind_has_a_stage_and_safe_recovery_copy()
    {
        foreach (UpdateErrorKind kind in Enum.GetValues<UpdateErrorKind>())
        {
            var state = new UpdateStateMachine();
            state.BeginChecking();
            state.Fail(kind);
            Assert.Equal(UpdateState.Checking, state.ErrorStage);
            Assert.False(string.IsNullOrWhiteSpace(state.RecoveryMessage));
        }
    }

    [Fact]
    public void Cancelling_a_check_has_its_own_non_retrying_terminal_state()
    {
        var state = new UpdateStateMachine();
        state.BeginChecking();
        state.CancelChecking();

        Assert.Equal(UpdateState.Cancelled, state.State);
        Assert.Equal(UpdateState.Checking, state.ErrorStage);
        Assert.Equal(UpdateErrorKind.Cancelled, state.ErrorKind);
        Assert.False(state.CanCancel);
        Assert.Contains("检查", state.RecoveryMessage, StringComparison.Ordinal);
    }
}






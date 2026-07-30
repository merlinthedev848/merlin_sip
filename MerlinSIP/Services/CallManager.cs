namespace MerlinSip.Services;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MerlinSip.Models;

/// <summary>
/// Interface for SIP operations required by the Call Manager.
/// </summary>
public interface ICallManagerSipProvider
{
    Task<SipCallResult> SetHeldAsync(bool held, CancellationToken cancellationToken = default);
    Task<SipCallResult> AnswerCallAsync(PendingIncomingCall incoming, CancellationToken cancellationToken = default);
    Task<SipCallResult> ResumeSpecificCallAsync(string callId, CancellationToken cancellationToken = default);
    // Note: SipRegistrationService's SetHeldAsync doesn't take a callId.
    // If it only acts on the current active call, we might need a way to swap the active call.
}

/// <summary>
/// Manages multiple calls, enabling features like Call Waiting and Hold-and-Answer.
/// </summary>
public class CallManager
{
    private readonly ICallManagerSipProvider _sipProvider;

    /// <summary>
    /// Gets the collection of active calls for UI binding.
    /// </summary>
    public ObservableCollection<CallState> ActiveCalls { get; } = new ObservableCollection<CallState>();

    /// <summary>
    /// Event fired when there is a new call waiting.
    /// </summary>
    public event EventHandler<CallWaitingEventArgs>? CallWaiting;

    public CallManager(ICallManagerSipProvider sipProvider)
    {
        _sipProvider = sipProvider ?? throw new ArgumentNullException(nameof(sipProvider));
    }

    /// <summary>
    /// Handles an incoming call when another call is already active.
    /// </summary>
    public async Task HandleIncomingCallAsync(PendingIncomingCall incoming, string callerId)
    {
        var args = new CallWaitingEventArgs(callerId, incoming);
        CallWaiting?.Invoke(this, args);

        switch (args.SelectedAction)
        {
            case CallWaitingAction.HoldAndAnswer:
                await HoldCurrentAndAnswerAsync(incoming);
                break;
            case CallWaitingAction.Reject:
                // Handle reject (e.g. via provider)
                break;
            case CallWaitingAction.SendToVoicemail:
                // Handle send to VM
                break;
        }
    }

    /// <summary>
    /// Puts the current active call on hold and answers the pending incoming call.
    /// </summary>
    public async Task HoldCurrentAndAnswerAsync(PendingIncomingCall incoming, CancellationToken cancellationToken = default)
    {
        var currentCall = ActiveCalls.FirstOrDefault(c => !c.IsOnHold);
        if (currentCall != null)
        {
            // Put current call on hold
            var holdResult = await _sipProvider.SetHeldAsync(true, cancellationToken);
            if (holdResult.Signalled)
            {
                currentCall.IsOnHold = true;
            }
            else
            {
                throw new InvalidOperationException("Failed to put the current call on hold.");
            }
        }

        // Answer the incoming call
        var answerResult = await _sipProvider.AnswerCallAsync(incoming, cancellationToken);
        if (answerResult.Signalled)
        {
            ActiveCalls.Add(new CallState
            {
                CallId = incoming.CallId,
                Target = incoming.RemoteTarget,
                IsOnHold = false,
                StartTime = DateTime.UtcNow
            });
        }
        else
        {
            throw new InvalidOperationException("Failed to answer the incoming call.");
        }
    }

    /// <summary>
    /// Switches to a specific held call, putting the current call on hold if necessary.
    /// </summary>
    public async Task SwitchToHeldCallAsync(string callId, CancellationToken cancellationToken = default)
    {
        var targetCall = ActiveCalls.FirstOrDefault(c => c.CallId == callId);
        if (targetCall == null || !targetCall.IsOnHold)
        {
            return;
        }

        var currentCall = ActiveCalls.FirstOrDefault(c => !c.IsOnHold);
        if (currentCall != null)
        {
            var holdResult = await _sipProvider.SetHeldAsync(true, cancellationToken);
            if (holdResult.Signalled)
            {
                currentCall.IsOnHold = true;
            }
        }

        // Resume the target call
        var resumeResult = await _sipProvider.ResumeSpecificCallAsync(callId, cancellationToken);
        if (resumeResult.Signalled)
        {
            targetCall.IsOnHold = false;
        }
        else
        {
            throw new InvalidOperationException("Failed to resume the target call.");
        }
    }
}
